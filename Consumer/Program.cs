using System.Diagnostics;
using System.IO.Compression;
using StackExchange.Redis;
using Common;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text;

var consumerGroupName = Environment.GetEnvironmentVariable("CONSUMER_GROUP_NAME") ?? "consumergroup";
var redisConnectionString = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING") ?? "localhost";
var operation = Environment.GetEnvironmentVariable("OPERATION") ?? "build";
var streamName = Environment.GetEnvironmentVariable("STREAM_NAME") ?? $"{operation}stream";
var minioBucketName = Environment.GetEnvironmentVariable("MINIO_BUCKET_NAME") ?? "your-bucket-name";
var minioAccessKey = Environment.GetEnvironmentVariable("MINIO_ACCESS_KEY") ?? "your-access-key";
var minioSecretKey = Environment.GetEnvironmentVariable("MINIO_SECRET_KEY") ?? "your-secret-key";
var minioServiceURL = Environment.GetEnvironmentVariable("MINIO_SERVICE_URL") ?? "http://localhost:9000";

void ConfigureServices(IServiceCollection services)
{
    // Add MinioUploader
    services.AddSingleton<MinioUploader>(provider =>
        new MinioUploader(minioBucketName, minioAccessKey, minioSecretKey, minioServiceURL));
}

// Setup dependency injection
var serviceCollection = new ServiceCollection();
ConfigureServices(serviceCollection);

var serviceProvider = serviceCollection.BuildServiceProvider();

// Use the MinioUploader
var minioUploader = serviceProvider.GetRequiredService<MinioUploader>();

var redis = ConnectionMultiplexer.Connect(redisConnectionString);
IDatabase db = redis.GetDatabase();

var consumerName = Guid.NewGuid().ToString();

var consumerGroupReadTask = Task.Run(async () =>
{
    Console.WriteLine($"Starting consumer {consumerName} for {operation} stream");
    string id = string.Empty;
    while (true)
    {
        if (!string.IsNullOrEmpty(id))
        {
            await db.StreamAcknowledgeAsync(streamName, consumerGroupName, id);
            id = string.Empty;
        }
        // read the next result from the stream
        var result = await db.StreamReadGroupAsync(streamName, consumerGroupName, consumerName, ">", 1);
        if (result.Length != 0)
        {
            id = result.FirstOrDefault().Id.ToString() ?? string.Empty;
            var dict = ParseResult(result.First());
            var key = string.Empty;
            if (dict.TryGetValue("key", out var k))
            {
                key = k;
            }
            else
            {
                Console.WriteLine("No key found in payload");
                continue;
            }

            var obj = JsonSerializer.Deserialize<Dictionary<string, string>>(dict["payload"]);

            Stream? message = null;

            if (obj == null)
            {
                continue;
            }

            try
            {
                if (obj.TryGetValue("command", out string? command))
                {
                    if (command == "build" || command == "test")
                    {
                        message = await ProcessOperation(key, command);
                    }
                    else if (command == "template")
                    {
                        var template = obj["template"];
                        var templateName = obj["templateName"];
                        message = await GenerateTemplate(template, templateName);
                    }
                    else
                    {
                        Console.WriteLine("Invalid command found in payload");
                        continue;
                    }
                }
                else
                {
                    Console.WriteLine("No command found in payload");
                    continue;
                }
            }
            catch (Exception ex)
            {
                message = new MemoryStream(Encoding.UTF8.GetBytes(ex.Message));
            }

            if (message != null)
            {
                await minioUploader.UploadFileFromStreamAsync(message, key + "_result");
            }
        }
        await Task.Delay(1000);
    }
});

Dictionary<string, string> ParseResult(StreamEntry entry) => entry.Values.ToDictionary(x => x.Name.ToString(), x => x.Value.ToString());

async Task<Stream> ProcessOperation(string key, string operation)
{
    // download the file from Minio
    var file = await minioUploader.DownloadFileAsync(key);

    return operation switch
    {
        "build" => await ProcessBuild(file),
        "test" => await ProcessTest(file),
        _ => throw new InvalidOperationException("Invalid operation"),
    };
}

async Task<Stream> ProcessBuild(Stream file)
{
    var (zipPath, tempPath) = await ExtractZipFile(file);

    try
    {
        // find the first .csproj file that does not contain "Tests.csproj"
        var csprojFile = Directory.GetFiles(tempPath, "*.csproj", SearchOption.AllDirectories)
            .FirstOrDefault(x => !x.Contains("Tests.csproj"));

        if (string.IsNullOrEmpty(csprojFile))
        {
            throw new InvalidOperationException("No csproj file found");
        }

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
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var output = process.StandardOutput.ReadToEnd();
            throw new InvalidOperationException(output);
        }

        // find the dll file and return it
        var dllFile = Directory.GetFiles(tempPath, "*.dll", SearchOption.AllDirectories).FirstOrDefault();
        if (string.IsNullOrEmpty(dllFile))
        {
            throw new InvalidOperationException("No dll file found");
        }

        // convert to stream
        var stream = new FileStream(dllFile, FileMode.Open);
        return stream;
    }
    finally
    {
        await CleanUp(tempPath);
    }
}

async Task<Stream> ProcessTest(Stream file)
{
    var (zipPath, tempPath) = await ExtractZipFile(file);

    try
    {
        // find the first csproj file that contains "Tests.csproj"
        var csprojFile = Directory.GetFiles(tempPath, "*.csproj", SearchOption.AllDirectories)
            .FirstOrDefault(x => x.Contains("Tests.csproj"));

        if (string.IsNullOrEmpty(csprojFile))
        {
            throw new InvalidOperationException("No test csproj file found");
        }

        // run tests
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"test {csprojFile}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        process.WaitForExit();

        var output = process.StandardOutput.ReadToEnd();

        // convert to stream
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(output));
        return stream;
    }
    finally
    {
        await CleanUp(tempPath);
    }
}

Task<(string, string)> ExtractZipFile(Stream file)
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

Task CleanUp(string tempPath)
{
    if (Directory.Exists(tempPath))
    {
        Directory.Delete(tempPath, true);
    }
    return Task.CompletedTask;
}

static void PrintDirectoryTree(string dirPath, string indent, bool isLast)
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

async Task Init()
{
    await InitStream(streamName, consumerGroupName);
}

async Task InitStream(string streamName, string groupName)
{
    if (!await db.KeyExistsAsync(streamName) ||
    (await db.StreamGroupInfoAsync(streamName)).All(x => x.Name != groupName))
    {
        await db.StreamCreateConsumerGroupAsync(streamName, groupName, "0-0", true);
    }
}

// initialize the stream
await Init();

async Task PopulateNugetCache()
{
    // check if the cache is already populated
    var nugetCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    if (Directory.Exists(nugetCache))
    {
        return;
    }

    // measure the time taken
    var timer = Stopwatch.StartNew();
    // run dotnet tool install AElf.ContractTemplates
    var process = Process.Start("dotnet", "new --install AElf.ContractTemplates");
    await process.WaitForExitAsync();

    // run dotnet new aelf -n HelloWorld
    process = Process.Start("dotnet", "new aelf -n HelloWorld");
    await process.WaitForExitAsync();

    // run dotnet restore
    process = Process.Start("dotnet", "restore test");
    await process.WaitForExitAsync();

    // print the directory tree
    PrintDirectoryTree(nugetCache, "", true);

    Console.WriteLine($"Populated nuget cache in {timer.ElapsedMilliseconds}ms");
}

async Task<Stream> GenerateTemplate(string template, string templateName)
{
    try
    {
        // run dotnet new aelf -n HelloWorld in a temporary directory
        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempPath);

        // start the process in the temporary directory
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"new {template} -n {templateName}",
                WorkingDirectory = tempPath,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        await process.WaitForExitAsync();

        Console.WriteLine($"Generated template {template} with name {templateName}");

        // zip the contents of the temporary directory
        var tempZipPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempZipPath);
        var zipPath = Path.Combine(tempZipPath, "contract.zip");
        ZipFile.CreateFromDirectory(tempPath, zipPath);

        // convert the zip file to stream
        var zipStream = new FileStream(zipPath, FileMode.Open);

        // cleanup the temporary directories
        await CleanUp(tempPath);
        await CleanUp(tempZipPath);

        return zipStream;
    }
    catch (Exception ex)
    {
        return new MemoryStream(Encoding.UTF8.GetBytes(ex.Message));
    }
}

await PopulateNugetCache();

// run the task
await consumerGroupReadTask;
