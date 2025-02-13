using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using System.Text;
using System.Text.Json;
using AElf.OpenTelemetry;
using AElf.OpenTelemetry.ExecutionTime;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

var minioBucketName = Environment.GetEnvironmentVariable("MINIO_BUCKET_NAME") ?? "your-bucket-name";
var minioAccessKey = Environment.GetEnvironmentVariable("MINIO_ACCESS_KEY") ?? "your-access-key";
var minioSecretKey = Environment.GetEnvironmentVariable("MINIO_SECRET_KEY") ?? "your-secret-key";
var minioServiceURL = Environment.GetEnvironmentVariable("MINIO_SERVICE_URL") ?? "http://localhost:9000";

var _minioUploader = new MinioUploader(minioBucketName, minioAccessKey, minioSecretKey, minioServiceURL);
var _logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger("TemplateJob");

var builder = WebApplication.CreateBuilder(args);

// Add OpenTelemetry configuration
builder.Configuration.AddJsonFile("appsettings.json", optional: false);
builder.Services.AddApplication<OpenTelemetryModule>();

// Add services to the container.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseSwagger();
app.UseSwaggerUI();

string prefix = "/playground";

app.MapGet($"{prefix}/template", HandleTemplateRequest);

[AggregateExecutionTime]
async Task<IResult> HandleTemplateRequest(string template, string projectName, IInstrumentationProvider instrumentationProvider)
{
    using var activity = instrumentationProvider.ActivitySource.StartActivity($"Template.HandleTemplateRequest");
    activity?.SetTag("template.name", template);
    activity?.SetTag("project.name", projectName);

    try
    {
        // Create a temporary directory
        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempPath);
        activity?.SetTag("temp.path", tempPath);

        // Run dotnet new command
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"new {template} -n {projectName}",
                WorkingDirectory = tempPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        activity?.SetTag("process.exit_code", process.ExitCode);
        activity?.SetTag("process.output", output);

        if (process.ExitCode != 0)
        {
            activity?.SetStatus(ActivityStatusCode.Error, error);
            activity?.SetTag("error.message", error);
            return TypedResults.BadRequest(error);
        }

        // Zip the project directory
        var zipPath = Path.Combine(Path.GetTempPath(), $"{projectName}.zip");
        ZipFile.CreateFromDirectory(Path.Combine(tempPath, projectName), zipPath);
        activity?.SetTag("zip.path", zipPath);

        // Read the zip file and return it
        var bytes = await File.ReadAllBytesAsync(zipPath);
        activity?.SetTag("response.size", bytes.Length);

        // Clean up
        Directory.Delete(tempPath, true);
        File.Delete(zipPath);

        return TypedResults.File(bytes, "application/zip", $"{projectName}.zip");
    }
    catch (Exception ex)
    {
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity?.SetTag("error.message", ex.Message);
        activity?.SetTag("error.stack_trace", ex.StackTrace);
        return TypedResults.Problem($"Internal server error: {ex.Message}");
    }
}

async Task InstallContractTemplates()
{
    _logger.LogInformation("Installing ContractTemplates");

    try
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

        // read the output and log it
        var output = await process.StandardOutput.ReadToEndAsync();
        _logger.LogInformation(output);

        // extract the short name of each template
        //   Sample output:
        //   Warning: use of 'dotnet new --install' is deprecated. Use 'dotnet new install' instead.
        //   For more information, run: 
        //      dotnet new install -h

        //   The following template packages will be installed:
        //      AElf.ContractTemplates

        //   AElf.ContractTemplates (version 1.0.2) is already installed, it will be replaced with latest version.
        //   AElf.ContractTemplates::1.0.2 was successfully uninstalled.
        //   Success: AElf.ContractTemplates::1.0.2 installed the following templates:
        //   Template Name                       Short Name       Language  Tags              
        //   ----------------------------------  ---------------  --------  ------------------
        //   AElf Contract                       aelf             [C#]      AElf/SmartContract
        //   AElf Contract LotteryGame Template  aelf-lottery     [C#]      AElf/SmartContract
        //   AElf Contract NftSale Template      aelf-nft-sale    [C#]      AElf/SmartContract
        //   AElf Contract SimpleDAO Template    aelf-simple-dao  [C#]      AElf/SmartContract
        // (blank line)
        // (blank line)
        // get the short name of each template, which is the second column
        var templates = output
            .Split("\n")
            .SkipWhile(line => !line.Contains("Template Name"))
            .Skip(2)
            .Select(line =>
            {
                var match = Regex.Match(line, @"\s{2,}([a-zA-Z0-9-]+)\s{2,}");
                return match.Success ? match.Groups[1].Value : null;
            })
            .Where(template => template != null)
            .ToList();

        // save the list to minio
        var stream = new MemoryStream();
        var writer = new StreamWriter(stream);
        foreach (var template in templates)
        {
            await writer.WriteLineAsync(template);
        }
        await writer.FlushAsync();
        stream.Position = 0;
        await _minioUploader.UploadFileFromStreamAsync(stream, "contract/templates.txt");

        foreach (var template in templates)
        {
            if (!string.IsNullOrWhiteSpace(template))
            {
                await GenerateTemplateAsync(template);
                _logger.LogInformation($"Generated template {template}");
            }
        }
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error installing ContractTemplates");
    }
}

async Task GenerateTemplateAsync(string template)
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
                Arguments = $"new {template} -n HelloWorld",
                WorkingDirectory = tempPath,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        await process.WaitForExitAsync();

        _logger.LogInformation($"Generated template {template}");

        // zip the contents of the temporary directory
        var tempZipPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempZipPath);
        var zipPath = Path.Combine(tempZipPath, "contract.zip");
        ZipFile.CreateFromDirectory(tempPath, zipPath);

        // convert the zip file to stream
        var zipStream = new FileStream(zipPath, FileMode.Open);

        // cleanup the temporary directories
        CleanUp(tempPath);
        CleanUp(tempZipPath);

        // upload to minio
        await _minioUploader.UploadFileFromStreamAsync(zipStream, $"contract/{template}.zip");
        _logger.LogInformation($"Uploaded template {template}");
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error generating template {template}", template);
    }
}


async Task PopulateNugetCache()
{
    var WorkingDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    // create the working directory
    Directory.CreateDirectory(WorkingDirectory);

    var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "new aelf -n HelloWorld",
            WorkingDirectory = WorkingDirectory,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }
    };
    process.Start();
    await process.WaitForExitAsync();

    var srcPath = Path.Combine(WorkingDirectory, "src");

    // run dotnet restore in the temporary directory
    process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "restore",
            WorkingDirectory = srcPath,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }
    };
    process.Start();
    await process.WaitForExitAsync();
    _logger.LogInformation("Populated Nuget Cache");

    // cleanup the temporary directories
    CleanUp(WorkingDirectory);
}

async Task UploadPackageCache()
{
    // get the nuget package cache directory
    var nugetCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget/packages");

    // create a zip file of the nuget package cache
    var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    Directory.CreateDirectory(tempPath);
    var zipPath = Path.Combine(tempPath, "nuget-cache.zip");
    ZipFile.CreateFromDirectory(nugetCache, zipPath);

    // convert the zip file to stream
    var zipStream = new FileStream(zipPath, FileMode.Open);

    // upload to minio
    await _minioUploader.UploadFileFromStreamAsync(zipStream, "nuget-cache.zip");
    _logger.LogInformation("Uploaded Nuget Cache");

    // cleanup the temporary directories
    CleanUp(tempPath);
}

static void CleanUp(string path)
{
    if (Directory.Exists(path))
    {
        Directory.Delete(path, true);
    }
}

await InstallContractTemplates();
await PopulateNugetCache();
await UploadPackageCache();