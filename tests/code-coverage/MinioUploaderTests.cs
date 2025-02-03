using System.Text;
using Amazon.S3;
using Common;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace code_coverage;

public class MinioUploaderTests
{
    private readonly string _bucketName = "test-bucket";
    private readonly string _accessKey = "test-access-key";
    private readonly string _secretKey = "test-secret-key";
    private readonly string _serviceUrl = "http://localhost:9000";

    [Fact]
    public void Constructor_WithServiceUrl_CreatesValidInstance()
    {
        // Act
        var uploader = new MinioUploader(_bucketName, _accessKey, _secretKey, _serviceUrl);

        // Assert
        Assert.NotNull(uploader);
    }

    [Fact]
    public void Constructor_WithoutServiceUrl_CreatesValidInstance()
    {
        // Act
        var uploader = new MinioUploader(_bucketName, _accessKey, _secretKey, null);

        // Assert
        Assert.NotNull(uploader);
    }

    [Fact]
    public async Task UploadFileFromStreamAsync_ValidStream_UploadsSuccessfully()
    {
        // Arrange
        var uploader = new MinioUploader(_bucketName, _accessKey, _secretKey, _serviceUrl);
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("test content"));
        var key = "test-key";

        // Act & Assert
        await uploader.UploadFileFromStreamAsync(stream, key);
    }

    [Fact]
    public async Task UploadFileFromIFormFileAsync_ValidFile_UploadsSuccessfully()
    {
        // Arrange
        var uploader = new MinioUploader(_bucketName, _accessKey, _secretKey, _serviceUrl);
        var mockFile = new Mock<IFormFile>();
        var content = "test content";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        mockFile.Setup(f => f.OpenReadStream()).Returns(stream);
        mockFile.Setup(f => f.Length).Returns(stream.Length);
        var key = "test-key";

        // Act & Assert
        await uploader.UploadFileFromIFormFileAsync(mockFile.Object, key);
    }

    [Fact]
    public async Task UploadFileFromIFormFileAsync_EmptyFile_ThrowsException()
    {
        // Arrange
        var uploader = new MinioUploader(_bucketName, _accessKey, _secretKey, _serviceUrl);
        var mockFile = new Mock<IFormFile>();
        mockFile.Setup(f => f.Length).Returns(0);
        var key = "test-key";

        // Act & Assert
        await Assert.ThrowsAsync<Exception>(() => uploader.UploadFileFromIFormFileAsync(mockFile.Object, key));
    }

    [Fact]
    public async Task DownloadFileAsync_ValidKey_ReturnsStream()
    {
        // Arrange
        var uploader = new MinioUploader(_bucketName, _accessKey, _secretKey, _serviceUrl);
        var key = "test-key";

        // Act
        var stream = await uploader.DownloadFileAsync(key);

        // Assert
        Assert.NotNull(stream);
    }
} 