using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using AElf.OpenTelemetry;
using AElf.OpenTelemetry.ExecutionTime;

namespace Consumer.Services;

[AggregateExecutionTime]
public class ConsumerService : BackgroundService
{
    private readonly ILogger<ConsumerService> _logger;
    private readonly MinioUploader _minioUploader;
    private readonly string _consumerGroupName;
    private readonly string _redisConnectionString;
    private readonly string _streamName;
    private readonly IDatabase _db;
    private readonly string _consumerName;
    private readonly IInstrumentationProvider _instrumentationProvider;
    private string _id;

    public ConsumerService(
        ILogger<ConsumerService> logger, 
        MinioUploader minioUploader,
        IInstrumentationProvider instrumentationProvider)
    {
        _logger = logger;
        _minioUploader = minioUploader;
        _instrumentationProvider = instrumentationProvider;
        _consumerGroupName = Environment.GetEnvironmentVariable("CONSUMER_GROUP_NAME") ?? "consumergroup";
        _redisConnectionString = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING") ?? "localhost";
        _streamName = Environment.GetEnvironmentVariable("STREAM_NAME") ?? $"buildstream";
        _db = ConnectionMultiplexer.Connect(_redisConnectionString).GetDatabase();
        _consumerName = Guid.NewGuid().ToString();
        _id = string.Empty;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var activity = _instrumentationProvider.ActivitySource.StartActivity($"{nameof(ConsumerService)}.{nameof(ExecuteAsync)}");
        _logger.LogInformation("Consumer Background Service is starting.");
        await Init();

        _logger.LogInformation("Consumer Background Service is doing background work.");
        _logger.LogInformation($"Consumer {_consumerName} is listening to stream {_streamName}");

        while (!stoppingToken.IsCancellationRequested)
        {
            using var processActivity = _instrumentationProvider.ActivitySource.StartActivity($"{nameof(ConsumerService)}.ProcessMessage");
            
            if (!string.IsNullOrEmpty(_id))
            {
                await _db.StreamAcknowledgeAsync(_streamName, _consumerGroupName, _id);
                _id = string.Empty;
            }

            var result = await _db.StreamReadGroupAsync(_streamName, _consumerGroupName, _consumerName, ">", 1);

            if (result.Length != 0)
            {
                _id = result.FirstOrDefault().Id.ToString() ?? string.Empty;
                var dict = ParseResult(result.First());
                string? key;
                if (dict.TryGetValue("key", out var k))
                {
                    key = k;
                    processActivity?.SetTag("message.key", key);
                }
                else
                {
                    _logger.LogInformation("No key found in payload");
                    continue;
                }

                var obj = JsonSerializer.Deserialize<Dictionary<string, string>>(dict["payload"]);

                Stream? message;

                if (obj == null)
                {
                    continue;
                }

                try
                {
                    if (obj.TryGetValue("command", out string? command))
                    {
                        processActivity?.SetTag("message.command", command);
                        
                        if (command == "build" || command == "test")
                        {
                            message = await ProcessOperation(key, command);
                        }
                        else if (command == "template")
                        {
                            if (obj.TryGetValue("template", out string? template) &&
                                obj.TryGetValue("projectName", out string? projectName))
                            {
                                processActivity?.SetTag("message.template", template);
                                processActivity?.SetTag("message.projectName", projectName);
                                message = await ProcessTemplate(template, projectName);
                            }
                            else
                            {
                                _logger.LogInformation("Invalid template command found in payload");
                                continue;
                            }
                        }
                        else
                        {
                            _logger.LogInformation("Invalid command found in payload");
                            continue;
                        }
                    }
                    else
                    {
                        _logger.LogInformation("No command found in payload");
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    processActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    message = new MemoryStream(Encoding.UTF8.GetBytes(ex.Message));
                }

                if (message != null)
                {
                    await _minioUploader.UploadFileFromStreamAsync(message, key + "_result");
                }
            }
        }
    }

    private static Dictionary<string, string> ParseResult(StreamEntry entry) => entry.Values.ToDictionary(x => x.Name.ToString(), x => x.Value.ToString());

    [AggregateExecutionTime]
    private async Task<Stream> ProcessOperation(string key, string operation)
    {
        using var activity = _instrumentationProvider.ActivitySource.StartActivity($"{nameof(ConsumerService)}.{nameof(ProcessOperation)}");
        activity?.SetTag("operation", operation);
        activity?.SetTag("key", key);

        // download the file from Minio
        var file = await _minioUploader.DownloadFileAsync(key);

        return operation switch
        {
            "build" => await ProcessBuild(file),
            "test" => await ProcessTest(file),
            _ => throw new InvalidOperationException("Invalid operation"),
        };
    }

    [AggregateExecutionTime]
    private async Task<Stream> ProcessBuild(Stream file)
    {
        using var activity = _instrumentationProvider.ActivitySource.StartActivity($"{nameof(ConsumerService)}.{nameof(ProcessBuild)}");
        
        var (zipPath, tempPath) = await ExtractZipFile(file);
        activity?.SetTag("tempPath", tempPath);
        activity?.SetTag("zipPath", zipPath);

        try
        {
            // find the first .csproj file that does not contain "Tests.csproj"
            var csprojFile = Directory.GetFiles(tempPath, "*.csproj", SearchOption.AllDirectories)
                .FirstOrDefault(x => !x.Contains("Tests.csproj"));

            if (string.IsNullOrEmpty(csprojFile))
            {
                var error = "No csproj file found";
                activity?.SetStatus(ActivityStatusCode.Error, error);
                activity?.SetTag("error.message", error);
                throw new InvalidOperationException(error);
            }

            activity?.SetTag("project.file", csprojFile);

            // build the project
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"build {csprojFile} -p:RunAnalyzers=false",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            process.WaitForExit();

            activity?.SetTag("build.output", output);
            activity?.SetTag("build.exit_code", process.ExitCode);

            if (process.ExitCode != 0)
            {
                activity?.SetStatus(ActivityStatusCode.Error, output);
                activity?.SetTag("error.message", output);
                throw new InvalidOperationException(output);
            }

            // find the dll file and return it
            var dllFile = Directory.GetFiles(tempPath, "*.dll.patched", SearchOption.AllDirectories).FirstOrDefault();
            if (string.IsNullOrEmpty(dllFile))
            {
                var error = "No dll file found";
                activity?.SetStatus(ActivityStatusCode.Error, error);
                activity?.SetTag("error.message", error);
                throw new InvalidOperationException(error);
            }

            activity?.SetTag("dll.file", dllFile);

            // get base64 encoded string of the dll file
            var bytes = await File.ReadAllBytesAsync(dllFile);
            var base64 = Convert.ToBase64String(bytes);
            activity?.SetTag("response.size", base64.Length);

            // convert to stream
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(base64));
            return stream;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("error.message", ex.Message);
            activity?.SetTag("error.stack_trace", ex.StackTrace);
            throw;
        }
        finally
        {
            CleanUp(tempPath);
        }
    }

    [AggregateExecutionTime]
    private async Task<Stream> ProcessTest(Stream file)
    {
        using var activity = _instrumentationProvider.ActivitySource.StartActivity($"{nameof(ConsumerService)}.{nameof(ProcessTest)}");
        
        var (zipPath, tempPath) = await ExtractZipFile(file);
        activity?.SetTag("tempPath", tempPath);
        activity?.SetTag("zipPath", zipPath);

        try
        {
            // find the first csproj file that contains "Tests.csproj"
            var csprojFile = Directory.GetFiles(tempPath, "*.csproj", SearchOption.AllDirectories)
                .FirstOrDefault(x => x.Contains("Tests.csproj"));

            if (string.IsNullOrEmpty(csprojFile))
            {
                var error = "No test csproj file found";
                activity?.SetStatus(ActivityStatusCode.Error, error);
                activity?.SetTag("error.message", error);
                throw new InvalidOperationException(error);
            }

            activity?.SetTag("project.file", csprojFile);

            // run tests
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"test {csprojFile} --logger \\\"console;verbosity=detailed\\\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            process.WaitForExit();

            activity?.SetTag("test.output", output);
            activity?.SetTag("test.exit_code", process.ExitCode);
            activity?.SetTag("response.size", output.Length);

            // convert to stream
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(output));
            return stream;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("error.message", ex.Message);
            activity?.SetTag("error.stack_trace", ex.StackTrace);
            throw;
        }
        finally
        {
            CleanUp(tempPath);
        }
    }

    [AggregateExecutionTime]
    private async Task<Stream> ProcessTemplate(string template, string projectName)
    {
        using var activity = _instrumentationProvider.ActivitySource.StartActivity($"{nameof(ConsumerService)}.{nameof(ProcessTemplate)}");
        activity?.SetTag("template", template);
        activity?.SetTag("projectName", projectName);

        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var templatePath = Path.Combine(tempPath, "template");
            Directory.CreateDirectory(templatePath);

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"new {template} -n {projectName}",
                    WorkingDirectory = templatePath,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            await process.WaitForExitAsync();
            
            var zipPath = Path.Combine(tempPath, $"template.zip");
            ZipFile.CreateFromDirectory(templatePath, zipPath);

            var file = File.ReadAllBytes(zipPath);
            var base64 = Convert.ToBase64String(file);

            var stream = new MemoryStream(Encoding.UTF8.GetBytes(base64));

            return stream;
        }
        finally
        {
            CleanUp(tempPath);
        }
    }

    private static Task<(string, string)> ExtractZipFile(Stream file)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        // extract the zip from the file
        var zipPath = Path.Combine(tempPath, "extracted");
        // extract the zip file
        Directory.CreateDirectory(tempPath);
        using (var fileStream = File.Create(zipPath))
        {
            file.CopyTo(fileStream);
        }
        // extract all nested folders and files
        ZipFile.ExtractToDirectory(zipPath, tempPath);
        // pretty print the folder structure and files, similar to `tree` command
        PrintDirectoryTree(tempPath, "", true);

        return Task.FromResult((zipPath, tempPath));
    }

    private static void PrintDirectoryTree(string dirPath, string indent, bool isLast)
    {
        // Print the current directory
        Console.WriteLine($"{indent}+- {Path.GetFileName(dirPath)}");

        // Update the indentation for subdirectories
        indent += isLast ? "   " : "|  ";

        // Get all subdirectories and files
        var subDirs = Directory.GetDirectories(dirPath);
        var files = Directory.GetFiles(dirPath);

        // Loop through each subdirectory
        for (int i = 0; i < subDirs.Length; i++)
        {
            bool isLastDir = (i == subDirs.Length - 1) && (files.Length == 0);
            PrintDirectoryTree(subDirs[i], indent, isLastDir);
        }

        // Loop through each file
        for (int i = 0; i < files.Length; i++)
        {
            bool isLastFile = i == files.Length - 1;
            Console.WriteLine($"{indent}+- {Path.GetFileName(files[i])}");
        }
    }

    private async Task Init()
    {
        await InitStream(_streamName, _consumerGroupName);
        await DownloadNugetCache();
        await InstallDotnetTemplates();
    }

    private async Task InitStream(string streamName, string groupName)
    {
        if (!await _db.KeyExistsAsync(streamName) ||
        (await _db.StreamGroupInfoAsync(streamName)).All(x => x.Name != groupName))
        {
            await _db.StreamCreateConsumerGroupAsync(streamName, groupName, "0-0", true);
        }
    }

    private async Task DownloadNugetCache()
    {
        var file = await _minioUploader.DownloadFileAsync("nuget-cache.zip");

        // nuget path
        var nugetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget/packages");

        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempPath);

        using (var fileStream = File.Create(Path.Combine(tempPath, "nuget-cache.zip")))
        {
            file.CopyTo(fileStream);
        }

        ZipFile.ExtractToDirectory(Path.Combine(tempPath, "nuget-cache.zip"), nugetPath);

        CleanUp(tempPath);

        _logger.LogInformation("Downloaded Nuget Cache");
    }

    private async Task InstallDotnetTemplates()
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "new --install AElf.ContractTemplates",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        await process.WaitForExitAsync();

        _logger.LogInformation("Installed Dotnet Templates");
    }

    private static void CleanUp(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }
}