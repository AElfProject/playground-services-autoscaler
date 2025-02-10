using System.Text;
using Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace code_coverage;

public class ProducerProgramTests
{
    private readonly Mock<IMinioUploader> _minioUploaderMock;
    private readonly Mock<ILogger> _loggerMock;

    public ProducerProgramTests()
    {
        _minioUploaderMock = new Mock<IMinioUploader>();
        _loggerMock = new Mock<ILogger>();
    }

    [Fact]
    public async Task ShareGet_ValidId_ReturnsFile()
    {
        // Arrange
        var id = "test-id";
        var expectedContent = "test content";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(expectedContent));
        
        _minioUploaderMock.Setup(x => x.DownloadFileAsync(id))
            .ReturnsAsync(stream);

        // Act
        var result = await _minioUploaderMock.Object.DownloadFileAsync(id);

        // Assert
        Assert.NotNull(result);
        var content = await new StreamReader(result).ReadToEndAsync();
        Assert.Equal(expectedContent, content);
        
        _minioUploaderMock.Verify(x => x.DownloadFileAsync(id), Times.Once);
    }

    [Fact]
    public async Task ShareCreate_ValidFile_ReturnsOk()
    {
        // Arrange
        var mockFile = new Mock<IFormFile>();
        var content = "test content";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        mockFile.Setup(f => f.OpenReadStream()).Returns(stream);
        mockFile.Setup(f => f.Length).Returns(stream.Length);
        var key = "test-key";

        _minioUploaderMock.Setup(x => x.UploadFileFromIFormFileAsync(mockFile.Object, key))
            .Returns(Task.CompletedTask);

        // Act
        await _minioUploaderMock.Object.UploadFileFromIFormFileAsync(mockFile.Object, key);

        // Assert
        _minioUploaderMock.Verify(x => x.UploadFileFromIFormFileAsync(mockFile.Object, key), Times.Once);
    }
} 