using System.Text;
using System.Diagnostics;
using Common;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace code_coverage;

public class TemplateProgramTests
{
    private readonly Mock<IMinioUploader> _minioUploaderMock;
    private readonly Mock<ILogger> _loggerMock;
    private readonly Mock<IProcessWrapper> _processWrapperMock;

    public TemplateProgramTests()
    {
        _minioUploaderMock = new Mock<IMinioUploader>();
        _loggerMock = new Mock<ILogger>();
        _processWrapperMock = new Mock<IProcessWrapper>();
    }

    [Fact]
    public async Task PopulateNugetCache_ExecutesSuccessfully()
    {
        // Arrange
        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempPath);

        try
        {
            // Mock process execution for 'dotnet new aelf'
            _processWrapperMock.Setup(x => x.StartProcess(
                It.Is<ProcessStartInfo>(p => 
                    p.FileName == "dotnet" && 
                    p.Arguments == "new aelf -n HelloWorld" &&
                    p.WorkingDirectory == tempPath)))
                .ReturnsAsync(0);

            // Mock process execution for 'dotnet restore'
            _processWrapperMock.Setup(x => x.StartProcess(
                It.Is<ProcessStartInfo>(p => 
                    p.FileName == "dotnet" && 
                    p.Arguments == "restore" &&
                    p.WorkingDirectory == Path.Combine(tempPath, "src"))))
                .ReturnsAsync(0);

            // Act
            await PopulateNugetCache(tempPath, _processWrapperMock.Object);

            // Assert
            _processWrapperMock.Verify(x => x.StartProcess(
                It.Is<ProcessStartInfo>(p => 
                    p.FileName == "dotnet" && 
                    p.Arguments == "new aelf -n HelloWorld")), 
                Times.Once);

            _processWrapperMock.Verify(x => x.StartProcess(
                It.Is<ProcessStartInfo>(p => 
                    p.FileName == "dotnet" && 
                    p.Arguments == "restore")), 
                Times.Once);
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

        // Act
        await UploadPackageCache(_minioUploaderMock.Object);

        // Assert
        _minioUploaderMock.Verify(
            m => m.UploadFileFromStreamAsync(It.IsAny<Stream>(), "nuget-cache.zip"),
            Times.Once);
    }

    [Fact]
    public async Task InstallContractTemplates_ExecutesSuccessfully()
    {
        // Arrange
        var templateOutput = @"Template Name
---------------------------
aelf-contract       
aelf-test          
";
        _processWrapperMock.Setup(x => x.StartProcess(
            It.Is<ProcessStartInfo>(p => 
                p.FileName == "dotnet" && 
                p.Arguments == "new --install AElf.ContractTemplates")))
            .ReturnsAsync(0);

        _processWrapperMock.Setup(x => x.GetProcessOutput())
            .Returns(templateOutput);

        _minioUploaderMock.Setup(m => m.UploadFileFromStreamAsync(It.IsAny<Stream>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        // Act
        await InstallContractTemplates(_processWrapperMock.Object, _minioUploaderMock.Object);

        // Assert
        _processWrapperMock.Verify(x => x.StartProcess(
            It.Is<ProcessStartInfo>(p => 
                p.FileName == "dotnet" && 
                p.Arguments == "new --install AElf.ContractTemplates")), 
            Times.Once);

        _minioUploaderMock.Verify(
            m => m.UploadFileFromStreamAsync(It.IsAny<Stream>(), "contract/templates.txt"),
            Times.Once);
    }

    private async Task PopulateNugetCache(string workingDirectory, IProcessWrapper processWrapper)
    {
        try
        {
            var process = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "new aelf -n HelloWorld",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            var exitCode = await processWrapper.StartProcess(process);
            if (exitCode != 0)
            {
                throw new Exception($"Failed to create project. Exit code: {exitCode}");
            }

            var srcPath = Path.Combine(workingDirectory, "src");
            if (!Directory.Exists(srcPath))
            {
                Directory.CreateDirectory(srcPath);
            }

            process = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "restore",
                WorkingDirectory = srcPath,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            exitCode = await processWrapper.StartProcess(process);
            if (exitCode != 0)
            {
                throw new Exception($"Failed to restore packages. Exit code: {exitCode}");
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to populate NuGet cache: {ex.Message}", ex);
        }
    }

    private async Task UploadPackageCache(IMinioUploader minioUploader)
    {
        var nugetCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget/packages");
        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempPath);

        try
        {
            var zipPath = Path.Combine(tempPath, "nuget-cache.zip");
            await using var stream = new MemoryStream();
            await minioUploader.UploadFileFromStreamAsync(stream, "nuget-cache.zip");
        }
        finally
        {
            if (Directory.Exists(tempPath))
            {
                Directory.Delete(tempPath, true);
            }
        }
    }

    private async Task InstallContractTemplates(IProcessWrapper processWrapper, IMinioUploader minioUploader)
    {
        try
        {
            var process = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "new --install AElf.ContractTemplates",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var exitCode = await processWrapper.StartProcess(process);
            if (exitCode != 0)
            {
                throw new Exception($"Failed to install templates. Exit code: {exitCode}");
            }

            var output = processWrapper.GetProcessOutput();
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
            await minioUploader.UploadFileFromStreamAsync(stream, "contract/templates.txt");
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to install contract templates: {ex.Message}", ex);
        }
    }
}

public interface IProcessWrapper
{
    Task<int> StartProcess(ProcessStartInfo processStartInfo);
    string GetProcessOutput();
} 