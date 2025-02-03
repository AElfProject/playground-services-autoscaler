using System.Text;
using System.Diagnostics;
using Common;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace code_coverage;

public class TemplateProgramTests
{
    private readonly Mock<Common.IMinioUploader> _minioUploaderMock;
    private readonly Mock<ILogger> _loggerMock;

    public TemplateProgramTests()
    {
        _minioUploaderMock = new Mock<Common.IMinioUploader>();
        _loggerMock = new Mock<ILogger>();
    }

    [Fact(Skip = "Requires dotnet command and AElf templates to be installed")]
    public async Task PopulateNugetCache_ExecutesSuccessfully()
    {
        // Arrange
        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempPath);

        try
        {
            // Act & Assert
            await PopulateNugetCache(tempPath);
        }
        finally
        {
            if (Directory.Exists(tempPath))
            {
                Directory.Delete(tempPath, true);
            }
        }
    }

    [Fact]
    public async Task UploadPackageCache_ExecutesSuccessfully()
    {
        // Arrange
        _minioUploaderMock.Setup(m => m.UploadFileFromStreamAsync(It.IsAny<Stream>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        // Act & Assert
        await UploadPackageCache();
    }

    [Fact(Skip = "Requires dotnet command and AElf templates to be installed")]
    public async Task InstallContractTemplates_ExecutesSuccessfully()
    {
        // Arrange
        _minioUploaderMock.Setup(m => m.UploadFileFromStreamAsync(It.IsAny<Stream>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        // Act & Assert
        await InstallContractTemplates();
    }

    private bool IsDotnetAvailable()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task PopulateNugetCache(string workingDirectory)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "new aelf -n HelloWorld",
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                throw new Exception($"Failed to create project. Exit code: {process.ExitCode}");
            }

            var srcPath = Path.Combine(workingDirectory, "src");
            if (!Directory.Exists(srcPath))
            {
                Directory.CreateDirectory(srcPath);
            }

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

            if (process.ExitCode != 0)
            {
                throw new Exception($"Failed to restore packages. Exit code: {process.ExitCode}");
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to populate NuGet cache: {ex.Message}", ex);
        }
    }

    private async Task UploadPackageCache()
    {
        var nugetCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget/packages");
        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempPath);

        try
        {
            var zipPath = Path.Combine(tempPath, "nuget-cache.zip");
            await using var stream = new MemoryStream();
            await _minioUploaderMock.Object.UploadFileFromStreamAsync(stream, "nuget-cache.zip");
        }
        finally
        {
            if (Directory.Exists(tempPath))
            {
                Directory.Delete(tempPath, true);
            }
        }
    }

    private async Task InstallContractTemplates()
    {
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

            if (process.ExitCode != 0)
            {
                throw new Exception($"Failed to install templates. Exit code: {process.ExitCode}");
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            var templates = output
                .Split("\n")
                .SkipWhile(line => !line.Contains("Template Name"))
                .Skip(2)
                .Select(line =>
                {
                    var match = System.Text.RegularExpressions.Regex.Match(line, @"\s{2,}([a-zA-Z0-9-]+)\s{2,}");
                    return match.Success ? match.Groups[1].Value : null;
                })
                .Where(template => template != null)
                .ToList();

            var stream = new MemoryStream();
            var writer = new StreamWriter(stream);
            foreach (var template in templates)
            {
                await writer.WriteLineAsync(template);
            }
            await writer.FlushAsync();
            stream.Position = 0;
            await _minioUploaderMock.Object.UploadFileFromStreamAsync(stream, "contract/templates.txt");
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to install contract templates: {ex.Message}", ex);
        }
    }
} 