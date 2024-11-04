using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Common;
using StackExchange.Redis;

var consumerGroupName = Environment.GetEnvironmentVariable("CONSUMER_GROUP_NAME") ?? "consumergroup";
var redisConnectionString = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING") ?? "localhost";
var buildStreamName = Environment.GetEnvironmentVariable("BUILD_STREAM_NAME") ?? "buildstream";
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

        using var reader = new StreamReader(result, Encoding.UTF8);
        var content = await reader.ReadToEndAsync();

        return Results.Ok(content);
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
        var result = await SendToRedis(buildStreamName, key, payload);

        using var reader = new StreamReader(result, Encoding.UTF8);
        var content = await reader.ReadToEndAsync();

        return Results.Ok(content);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Internal server error: {ex.Message}");
    }
})
.DisableAntiforgery();

app.MapGet("/templates", (MinioUploader minioUploader) =>
{
    // download the templates.txt file from minio
    var stream = minioUploader.DownloadFileAsync("contract/templates.txt").Result;
    using var reader = new StreamReader(stream, Encoding.UTF8);
    var content = reader.ReadToEnd();
    // split the content by new line
    var templates = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    return Results.Ok(templates);
});

app.MapGet("/template", async (string template, MinioUploader minioUploader) =>
{
    if (string.IsNullOrWhiteSpace(template))
    {
        return Results.BadRequest("Template name is required.");
    }

    // get template from minio
    var result = await minioUploader.DownloadFileAsync($"contract/{template}.zip");

    return Results.File(result, "application/octet-stream", template + ".zip");
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

Dictionary<string, string> ParseResult(StreamEntry entry) => entry.Values.ToDictionary(x => x.Name.ToString(), x => x.Value.ToString());

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
    // position is the message id, it should be greater than the current datetime
    var position = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

    // consume the result from the stream
    while (sw.ElapsedMilliseconds < timeout)
    {
        var minioUploader = app.Services.GetRequiredService<MinioUploader>();
        try
        {
            var stream = await minioUploader.DownloadFileAsync(key + "_result");
            if (stream != null)
            {
                Console.WriteLine($"Result for {key} received in {sw.ElapsedMilliseconds}ms");
                return stream;
            }
        }
        catch (Exception)
        {
            await Task.Delay(1000);
            continue;
        }
    }

    // create a timeout response
    return new MemoryStream(Encoding.UTF8.GetBytes("Timeout"));
}

async Task Init()
{
    await InitStream(buildStreamName, consumerGroupName);
}

async Task InitStream(string streamName, string groupName)
{
    if (!await db.KeyExistsAsync(streamName) ||
    (await db.StreamGroupInfoAsync(streamName)).All(x => x.Name != groupName))
    {
        await db.StreamCreateConsumerGroupAsync(streamName, groupName, "0-0", true);
    }
}