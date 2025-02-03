using Microsoft.AspNetCore.Http;

namespace Common;

public interface IMinioUploader
{
    Task UploadFileFromStreamAsync(Stream fileStream, string key);
    Task UploadFileFromIFormFileAsync(IFormFile file, string key);
    Task<Stream> DownloadFileAsync(string key);
} 