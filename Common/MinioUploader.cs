using System;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Transfer;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Http;

namespace Common;

public class MinioUploader
{
    private readonly string _bucketName;
    private readonly AmazonS3Client _s3Client;

    public MinioUploader(string bucketName, string accessKey, string secretKey, string serviceURL)
    {
        _bucketName = bucketName;
        var config = new AmazonS3Config
        {
            ServiceURL = serviceURL,
            ForcePathStyle = true // Required for MinIO
        };

        _s3Client = new AmazonS3Client(accessKey, secretKey, config);

        CreateBucketAsync().Wait();
    }

    public async Task UploadFileFromStreamAsync(Stream fileStream, string key)
    {
        using var fileTransferUtility = new TransferUtility(_s3Client);
        await fileTransferUtility.UploadAsync(fileStream, _bucketName, key);
    }

    public async Task UploadFileFromIFormFileAsync(IFormFile file, string key)
    {
        if (file == null || file.Length == 0)
        {
            throw new Exception("A valid file is required.");
        }

        using var stream = file.OpenReadStream();
        await UploadFileFromStreamAsync(stream, key);
    }

    public async Task<Stream> DownloadFileAsync(string key)
    {
        try
        {
            var request = new GetObjectRequest
            {
                BucketName = _bucketName,
                Key = key
            };

            var response = await _s3Client.GetObjectAsync(request);
            return response.ResponseStream;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error downloading file: " + ex.Message);
            throw; // Rethrow to make sure the caller knows something went wrong
        }
    }

    public async Task CreateBucketAsync()
    {
        try
        {
            // check if bucket exists
            var listBucketsResponse = await _s3Client.ListBucketsAsync();
            if (listBucketsResponse.Buckets.Any(b => b.BucketName == _bucketName))
            {
                return;
            }

            var request = new PutBucketRequest
            {
                BucketName = _bucketName
            };

            await _s3Client.PutBucketAsync(request);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error creating bucket: " + ex.Message);
            throw; // Rethrow to make sure the caller knows something went wrong
        }
    }
}