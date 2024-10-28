using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Consumer.Services;

public class TemplateService : BackgroundService
{
    private readonly ILogger<TemplateService> _logger;
    private readonly MinioUploader _minioUploader;

    public TemplateService(ILogger<TemplateService> logger, MinioUploader minioUploader)
    {
        _logger = logger;
        _minioUploader = minioUploader;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Template Background Service is starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Template Background Service is doing background work.");

            await InstallContractTemplates();
            await PopulateNugetCache();

            // perform the task every 24 hours
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }

        _logger.LogInformation("Template Background Service is stopping.");
    }

    private async Task InstallContractTemplates()
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

    private async Task GenerateTemplateAsync(string template)
    {
        _logger.LogInformation("Template Background Service is doing background work.");

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

    private static void CleanUp(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }

    private async Task PopulateNugetCache()
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
}