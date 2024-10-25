using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Common;
using StackExchange.Redis;

var consumerGroupName = Environment.GetEnvironmentVariable("CONSUMER_GROUP_NAME") ?? "consumergroup";
var redisConnectionString = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING") ?? "localhost";
var buildStreamName = Environment.GetEnvironmentVariable("BUILD_STREAM_NAME") ?? "buildstream";
var testStreamName = Environment.GetEnvironmentVariable("TEST_STREAM_NAME") ?? "teststream";
var templateStreamName = Environment.GetEnvironmentVariable("TEMPLATE_STREAM_NAME") ?? "templatestream";
var timeout = int.Parse(Environment.GetEnvironmentVariable("TIMEOUT") ?? "10000");
var minioBucketName = Environment.GetEnvironmentVariable("MINIO_BUCKET_NAME") ?? "your-bucket-name";
var minioAccessKey = Environment.GetEnvironmentVariable("MINIO_ACCESS_KEY") ?? "your-access-key";
var minioSecretKey = Environment.GetEnvironmentVariable("MINIO_SECRET_KEY") ?? "your-secret-key";
var minioServiceURL = Environment.GetEnvironmentVariable("MINIO_SERVICE_URL") ?? "http://localhost:9000";

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSingleton(provider =>
    new MinioUploader(minioBucketName, minioAccessKey, minioSecretKey, minioServiceURL));

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();

var redis = ConnectionMultiplexer.Connect(redisConnectionString);
IDatabase db = redis.GetDatabase();

// post formdata file upload to Redis stream
app.MapPost("/build", async (IFormFile file, MinioUploader minioUploader) =>
{
    try
    {
        var key = Guid.NewGuid().ToString();
        await minioUploader.UploadFileFromIFormFileAsync(file, key);
        var payload = JsonSerializer.Serialize(new { command = "build" });
        var result = await SendToRedis(buildStreamName, key, payload);

        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Internal server error: {ex.Message}");
    }
})
.DisableAntiforgery();

app.MapPost("/test", async (IFormFile file, MinioUploader minioUploader) =>
{
    try
    {
        var key = Guid.NewGuid().ToString();
        await minioUploader.UploadFileFromIFormFileAsync(file, key);
        var payload = JsonSerializer.Serialize(new { command = "test" });
        var result = await SendToRedis(testStreamName, key, payload);
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Internal server error: {ex.Message}");
    }
})
.DisableAntiforgery();

app.MapGet("/templates", () =>
{
    return new List<string> { "aelf", "aelf-lottery", "aelf-nft-sale", "aelf-simple-dao" };
});

app.MapGet("/template", async (string template, string templateName) =>
{
    var payload = JsonSerializer.Serialize(new { command = "template", template, templateName });
    var key = $"{template}-{templateName}";
    var result = await SendToRedis(templateStreamName, key, payload);
    return Results.Ok(result);
});

app.MapGet("/share/get", async (string key, MinioUploader minioUploader) =>
{
    if (string.IsNullOrWhiteSpace(key))
    {
        return Results.BadRequest("File key is required.");
    }

    try
    {
        var stream = await minioUploader.DownloadFileAsync(key);
        return Results.File(stream, "application/octet-stream", key + ".zip");
    }
    catch (Exception ex)
    {
        return Results.Problem($"Internal server error: {ex.Message}");
    }
});

app.MapPost("/share/create", async (IFormFile file, MinioUploader minioUploader) =>
{
    try
    {
        var key = Guid.NewGuid().ToString();
        await minioUploader.UploadFileFromIFormFileAsync(file, key);
        return Results.Ok(key);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Internal server error: {ex.Message}");
    }
})
.DisableAntiforgery();

app.Run();

async Task<Stream> SendToRedis(string streamName, string key, string payload)
{
    await Init();

    // https://github.com/StackExchange/StackExchange.Redis/issues/1718#issuecomment-1219592426
    var Expiry = TimeSpan.FromMinutes(3);
    var add = await db.ExecuteAsync("XADD", streamName, "MINID", "=", DateTimeOffset.UtcNow.Subtract(Expiry).ToUnixTimeMilliseconds(), "*", "key", key, "payload", payload);
    var msgId = add.ToString();
    Console.WriteLine($"Message added to stream {streamName} with key {key} as {msgId}");

    // check minio for the result
    var sw = Stopwatch.StartNew();

    // get minio required service
    var minioUploader = app.Services.GetRequiredService<MinioUploader>();
    // check the key from minio
    while (sw.ElapsedMilliseconds < timeout)
    {
        try
        {
            var stream = await minioUploader.DownloadFileAsync(key + "_result");
            if (stream != null)
            {
                return stream;
            }
        }
        catch (Exception)
        {
            // ignore
        }

        await Task.Delay(1000);
    }

    // create a timeout response
    return new MemoryStream(Encoding.UTF8.GetBytes("Timeout"));
}

async Task Init()
{
    await InitStream(buildStreamName, consumerGroupName);
    await InitStream(testStreamName, consumerGroupName);
    await InitStream(templateStreamName, consumerGroupName);
}

async Task InitStream(string streamName, string groupName)
{
    if (!await db.KeyExistsAsync(streamName) ||
    (await db.StreamGroupInfoAsync(streamName)).All(x => x.Name != groupName))
    {
        await db.StreamCreateConsumerGroupAsync(streamName, groupName, "0-0", true);
    }
}