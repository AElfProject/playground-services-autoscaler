using Common;
using Microsoft.AspNetCore.Http;

namespace code_coverage;

public class TestMinioUploader : MinioUploader
{
    public TestMinioUploader() : base("test-bucket", "test-access", "test-secret", "http://localhost:9000")
    {
    }

    public new Task UploadFileFromStreamAsync(Stream fileStream, string key)
    {
        return Task.CompletedTask;
    }

    public new Task UploadFileFromIFormFileAsync(IFormFile file, string key)
    {
        return Task.CompletedTask;
    }

    public new Task<Stream> DownloadFileAsync(string key)
    {
        return Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("test content")));
    }
} 