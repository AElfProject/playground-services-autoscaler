using Common;
using Microsoft.AspNetCore.Http;

namespace code_coverage;

public class TestMinioUploader : MinioUploader
{
    private readonly Dictionary<string, byte[]> _storage = new();

    public TestMinioUploader() : base("test-bucket", "test-access", "test-secret", "http://localhost:9000")
    {
    }

    public new async Task UploadFileFromStreamAsync(Stream fileStream, string key)
    {
        using var ms = new MemoryStream();
        await fileStream.CopyToAsync(ms);
        _storage[key] = ms.ToArray();
    }

    public new async Task UploadFileFromIFormFileAsync(IFormFile file, string key)
    {
        if (file == null || file.Length == 0)
        {
            throw new Exception("A valid file is required.");
        }

        using var stream = file.OpenReadStream();
        await UploadFileFromStreamAsync(stream, key);
    }

    public new Task<Stream> DownloadFileAsync(string key)
    {
        if (_storage.TryGetValue(key, out var data))
        {
            return Task.FromResult<Stream>(new MemoryStream(data));
        }
        throw new Amazon.S3.AmazonS3Exception($"The specified key '{key}' does not exist.");
    }
} 