using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Common;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace code_coverage;

public class MinioUploaderTests
{
    private readonly TestMinioUploader _uploader;

    public MinioUploaderTests()
    {
        _uploader = new TestMinioUploader();
    }

    [Fact]
    public async Task UploadFileFromStreamAsync_ValidStream_UploadsSuccessfully()
    {
        // Arrange
        var content = "test content";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var key = "test-key";

        // Act
        await _uploader.UploadFileFromStreamAsync(stream, key);

        // Assert
        var downloadedStream = await _uploader.DownloadFileAsync(key);
        using var reader = new StreamReader(downloadedStream);
        var downloadedContent = await reader.ReadToEndAsync();
        Assert.Equal(content, downloadedContent);
    }

    [Fact]
    public async Task UploadFileFromIFormFileAsync_ValidFile_UploadsSuccessfully()
    {
        // Arrange
        var mockFile = new Mock<IFormFile>();
        var content = "test content";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        mockFile.Setup(f => f.OpenReadStream()).Returns(stream);
        mockFile.Setup(f => f.Length).Returns(stream.Length);
        var key = "test-key";

        // Act
        await _uploader.UploadFileFromIFormFileAsync(mockFile.Object, key);

        // Assert
        var downloadedStream = await _uploader.DownloadFileAsync(key);
        using var reader = new StreamReader(downloadedStream);
        var downloadedContent = await reader.ReadToEndAsync();
        Assert.Equal(content, downloadedContent);
    }

    [Fact]
    public async Task UploadFileFromIFormFileAsync_EmptyFile_ThrowsException()
    {
        // Arrange
        var mockFile = new Mock<IFormFile>();
        mockFile.Setup(f => f.Length).Returns(0);
        var key = "test-key";

        // Act & Assert
        await Assert.ThrowsAsync<Exception>(() => _uploader.UploadFileFromIFormFileAsync(mockFile.Object, key));
    }

    [Fact]
    public async Task DownloadFileAsync_ValidKey_ReturnsStream()
    {
        // Arrange
        var content = "test content";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var key = "test-key";
        await _uploader.UploadFileFromStreamAsync(stream, key);

        // Act
        var result = await _uploader.DownloadFileAsync(key);

        // Assert
        Assert.NotNull(result);
        using var reader = new StreamReader(result);
        var downloadedContent = await reader.ReadToEndAsync();
        Assert.Equal(content, downloadedContent);
    }

    [Fact]
    public async Task DownloadFileAsync_InvalidKey_ThrowsException()
    {
        // Arrange
        var key = "non-existent-key";

        // Act & Assert
        await Assert.ThrowsAsync<AmazonS3Exception>(() => _uploader.DownloadFileAsync(key));
    }
} 