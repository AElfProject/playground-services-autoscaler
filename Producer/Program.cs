using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Common;
using StackExchange.Redis;
using nClam;
using AElf.OpenTelemetry;

var consumerGroupName = Environment.GetEnvironmentVariable("CONSUMER_GROUP_NAME") ?? "consumergroup";
var redisConnectionString = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING") ?? "localhost";
var buildStreamName = Environment.GetEnvironmentVariable("BUILD_STREAM_NAME") ?? "buildstream";
var timeout = int.Parse(Environment.GetEnvironmentVariable("TIMEOUT") ?? "180000");
var minioBucketName = Environment.GetEnvironmentVariable("MINIO_BUCKET_NAME") ?? "your-bucket-name";
var minioAccessKey = Environment.GetEnvironmentVariable("MINIO_ACCESS_KEY") ?? "your-access-key";
var minioSecretKey = Environment.GetEnvironmentVariable("MINIO_SECRET_KEY") ?? "your-secret-key";
var minioServiceURL = Environment.GetEnvironmentVariable("MINIO_SERVICE_URL");
var clamHost = Environment.GetEnvironmentVariable("CLAM_HOST") ?? "localhost";

var builder = WebApplication.CreateBuilder(args);

// Add OpenTelemetry configuration
builder.Configuration.AddJsonFile("appsettings.json", optional: false);
builder.Services.AddApplication<OpenTelemetryModule>();

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

var redis = ConnectionMultiplexer.Connect(redisConnectionString);
IDatabase db = redis.GetDatabase();

var clam = new ClamClient(clamHost);

string prefix = "/playground";

// post formdata file upload to Redis stream
app.MapPost($"{prefix}/build", async (IFormFile contractFiles, MinioUploader minioUploader) =>
{
    try
    {
        var key = Guid.NewGuid().ToString();
        await minioUploader.UploadFileFromIFormFileAsync(contractFiles, key);
        var payload = JsonSerializer.Serialize(new { command = "build" });
        var result = await SendToRedis(buildStreamName, key, payload);

        using var reader = new StreamReader(result, Encoding.UTF8);
        var content = await reader.ReadToEndAsync();

        return Results.Text(content);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Internal server error: {ex.Message}");
    }
})
.DisableAntiforgery();

app.MapPost($"{prefix}/test", async (IFormFile contractFiles, MinioUploader minioUploader) =>
{
    try
    {
        var key = Guid.NewGuid().ToString();
        await minioUploader.UploadFileFromIFormFileAsync(contractFiles, key);
        var payload = JsonSerializer.Serialize(new { command = "test" });
        var result = await SendToRedis(buildStreamName, key, payload);

        using var reader = new StreamReader(result, Encoding.UTF8);
        var content = await reader.ReadToEndAsync();

        return Results.Text(content);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Internal server error: {ex.Message}");
    }
})
.DisableAntiforgery();

app.MapGet($"{prefix}/templates", (MinioUploader minioUploader) =>
{
    // download the templates.txt file from minio
    var stream = minioUploader.DownloadFileAsync("contract/templates.txt").Result;
    using var reader = new StreamReader(stream, Encoding.UTF8);
    var content = reader.ReadToEnd();
    // split the content by new line
    var templates = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    return Results.Ok(templates);
});

app.MapGet($"{prefix}/template", async (string template, string projectName) =>
{
    if (string.IsNullOrWhiteSpace(template))
    {
        return Results.BadRequest("Template name is required.");
    }

    if (string.IsNullOrWhiteSpace(projectName))
    {
        return Results.BadRequest("Project name is required.");
    }

    try
    {
        var key = Guid.NewGuid().ToString();
        var payload = JsonSerializer.Serialize(new { command = "template", template, projectName });
        var result = await SendToRedis(buildStreamName, key, payload);

        // result is a zip file, read it and return the content in base64 string
        using var reader = new StreamReader(result, Encoding.UTF8);
        var content = await reader.ReadToEndAsync();

        return Results.Text(content);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Internal server error: {ex.Message}");
    }
});

app.MapGet(prefix + "/share/get/{id}", async (string id, MinioUploader minioUploader) =>
{
    if (string.IsNullOrWhiteSpace(id))
    {
        return Results.BadRequest("File ID is required.");
    }

    try
    {
        var stream = await minioUploader.DownloadFileAsync(id);
        return Results.File(stream, "application/octet-stream", id + ".zip");
    }
    catch (Exception ex)
    {
        return Results.Problem($"Internal server error: {ex.Message}");
    }
});

app.MapPost($"{prefix}/share/create", async (IFormFile file, MinioUploader minioUploader) =>
{
    try
    {
        var key = Guid.NewGuid().ToString();
        var scanResult = await clam.SendAndScanFileAsync(file.OpenReadStream());

        switch (scanResult.Result)
        {
            case ClamScanResults.Clean:
                await minioUploader.UploadFileFromIFormFileAsync(file, key);
                return Results.Ok(new {id = key});
            case ClamScanResults.VirusDetected:
                Console.WriteLine("Virus Found!");
                Console.WriteLine("Virus name: {0}", scanResult.InfectedFiles?.FirstOrDefault()?.VirusName);
                return Results.Problem("Virus detected in the file. Aborting upload.");
            case ClamScanResults.Error:
                Console.WriteLine("Error: {0}", scanResult.RawResult);
                return Results.Problem("An error occurred while scanning the file.");
        }

        return Results.Problem("An error occurred while scanning the file.");
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