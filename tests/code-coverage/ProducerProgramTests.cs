using System.Text;
using Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;

namespace code_coverage;

public class ProducerProgramTests
{
    private readonly TestMinioUploader _minioUploader;

    public ProducerProgramTests()
    {
        _minioUploader = new TestMinioUploader();
    }

    [Fact(Skip = "Producer project needs to be refactored to use IMinioUploader")]
    public async Task ShareGet_ValidId_ReturnsFile()
    {
        // Arrange
        var id = "test-id";

        // Act
        var result = await _minioUploader.DownloadFileAsync(id);

        // Assert
        Assert.NotNull(result);
        var content = await new StreamReader(result).ReadToEndAsync();
        Assert.Equal("test content", content);
    }

    [Fact(Skip = "Producer project needs to be refactored to use IMinioUploader")]
    public async Task ShareCreate_ValidFile_ReturnsOk()
    {
        // Arrange
        var mockFile = new Mock<IFormFile>();
        var content = "test content";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        mockFile.Setup(f => f.OpenReadStream()).Returns(stream);
        mockFile.Setup(f => f.Length).Returns(stream.Length);

        // Act
        await _minioUploader.UploadFileFromIFormFileAsync(mockFile.Object, "test-key");

        // Assert - if no exception is thrown, the test passes
    }
} 