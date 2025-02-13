using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Common;
using StackExchange.Redis;
using nClam;
using AElf.OpenTelemetry;
using System.Diagnostics;

const string ServiceName = "Producer";

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
app.MapPost($"{prefix}/build", async (IFormFile contractFiles, MinioUploader minioUploader, IInstrumentationProvider instrumentationProvider) =>
{
    using var activity = instrumentationProvider.ActivitySource.StartActivity($"{ServiceName}.Build");
    try
    {
        var key = Guid.NewGuid().ToString();
        activity?.SetTag("file.key", key);
        activity?.SetTag("file.name", contractFiles.FileName);
        activity?.SetTag("file.size", contractFiles.Length);

        await minioUploader.UploadFileFromIFormFileAsync(contractFiles, key);
        var payload = JsonSerializer.Serialize(new { command = "build" });
        var result = await SendToRedis(buildStreamName, key, payload);

        using var reader = new StreamReader(result, Encoding.UTF8);
        var content = await reader.ReadToEndAsync();
        
        activity?.SetTag("response.content", content);
        return Results.Text(content);
    }
    catch (Exception ex)
    {
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity?.SetTag("error.message", ex.Message);
        return Results.Problem($"Internal server error: {ex.Message}");
    }
})
.DisableAntiforgery();

app.MapPost($"{prefix}/test", async (IFormFile contractFiles, MinioUploader minioUploader, IInstrumentationProvider instrumentationProvider) =>
{
    using var activity = instrumentationProvider.ActivitySource.StartActivity($"{ServiceName}.Test");
    try
    {
        var key = Guid.NewGuid().ToString();
        activity?.SetTag("file.key", key);
        activity?.SetTag("file.name", contractFiles.FileName);
        activity?.SetTag("file.size", contractFiles.Length);

        await minioUploader.UploadFileFromIFormFileAsync(contractFiles, key);
        var payload = JsonSerializer.Serialize(new { command = "test" });
        var result = await SendToRedis(buildStreamName, key, payload);

        using var reader = new StreamReader(result, Encoding.UTF8);
        var content = await reader.ReadToEndAsync();
        
        activity?.SetTag("response.content", content);
        return Results.Text(content);
    }
    catch (Exception ex)
    {
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity?.SetTag("error.message", ex.Message);
        return Results.Problem($"Internal server error: {ex.Message}");
    }
})
.DisableAntiforgery();

app.MapGet($"{prefix}/templates", async (MinioUploader minioUploader, IInstrumentationProvider instrumentationProvider) =>
{
    using var activity = instrumentationProvider.ActivitySource.StartActivity($"{ServiceName}.GetTemplates");
    try
    {
        var stream = await minioUploader.DownloadFileAsync("contract/templates.txt");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = reader.ReadToEnd();
        var templates = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        
        activity?.SetTag("templates.count", templates.Length);
        activity?.SetTag("response.templates", string.Join(",", templates));
        return Results.Ok(templates);
    }
    catch (Exception ex)
    {
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity?.SetTag("error.message", ex.Message);
        return Results.Problem($"Internal server error: {ex.Message}");
    }
});

app.MapGet($"{prefix}/template", async (string template, string projectName, IInstrumentationProvider instrumentationProvider) =>
{
    using var activity = instrumentationProvider.ActivitySource.StartActivity($"{ServiceName}.GetTemplate");
    activity?.SetTag("template.name", template);
    activity?.SetTag("project.name", projectName);

    if (string.IsNullOrWhiteSpace(template))
    {
        activity?.SetStatus(ActivityStatusCode.Error, "Template name is required");
        return Results.BadRequest("Template name is required.");
    }

    if (string.IsNullOrWhiteSpace(projectName))
    {
        activity?.SetStatus(ActivityStatusCode.Error, "Project name is required");
        return Results.BadRequest("Project name is required.");
    }

    try
    {
        var key = Guid.NewGuid().ToString();
        activity?.SetTag("request.key", key);
        var payload = JsonSerializer.Serialize(new { command = "template", template, projectName });
        var result = await SendToRedis(buildStreamName, key, payload);

        using var reader = new StreamReader(result, Encoding.UTF8);
        var content = await reader.ReadToEndAsync();
        
        activity?.SetTag("response.content_length", content.Length);
        return Results.Text(content);
    }
    catch (Exception ex)
    {
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity?.SetTag("error.message", ex.Message);
        return Results.Problem($"Internal server error: {ex.Message}");
    }
});

app.MapGet(prefix + "/share/get/{id}", async (string id, MinioUploader minioUploader, IInstrumentationProvider instrumentationProvider) =>
{
    using var activity = instrumentationProvider.ActivitySource.StartActivity($"{ServiceName}.GetSharedFile");
    activity?.SetTag("file.id", id);

    if (string.IsNullOrWhiteSpace(id))
    {
        activity?.SetStatus(ActivityStatusCode.Error, "File ID is required");
        return Results.BadRequest("File ID is required.");
    }

    try
    {
        var stream = await minioUploader.DownloadFileAsync(id);
        activity?.SetTag("response.file_name", id + ".zip");
        return Results.File(stream, "application/octet-stream", id + ".zip");
    }
    catch (Exception ex)
    {
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity?.SetTag("error.message", ex.Message);
        return Results.Problem($"Internal server error: {ex.Message}");
    }
});

app.MapPost($"{prefix}/share/create", async (IFormFile file, MinioUploader minioUploader, IInstrumentationProvider instrumentationProvider) =>
{
    using var activity = instrumentationProvider.ActivitySource.StartActivity($"{ServiceName}.CreateShare");
    activity?.SetTag("file.name", file.FileName);
    activity?.SetTag("file.size", file.Length);

    try
    {
        var key = Guid.NewGuid().ToString();
        activity?.SetTag("file.key", key);
        var scanResult = await clam.SendAndScanFileAsync(file.OpenReadStream());

        switch (scanResult.Result)
        {
            case ClamScanResults.Clean:
                await minioUploader.UploadFileFromIFormFileAsync(file, key);
                activity?.SetTag("scan.result", "clean");
                return Results.Ok(new {id = key});
            case ClamScanResults.VirusDetected:
                var virusName = scanResult.InfectedFiles?.FirstOrDefault()?.VirusName;
                activity?.SetStatus(ActivityStatusCode.Error, $"Virus detected: {virusName}");
                activity?.SetTag("scan.result", "virus_detected");
                activity?.SetTag("scan.virus_name", virusName);
                return Results.Problem("Virus detected in the file. Aborting upload.");
            case ClamScanResults.Error:
                activity?.SetStatus(ActivityStatusCode.Error, scanResult.RawResult);
                activity?.SetTag("scan.result", "error");
                activity?.SetTag("scan.error", scanResult.RawResult);
                return Results.Problem("An error occurred while scanning the file.");
        }

        return Results.Problem("An error occurred while scanning the file.");
    }
    catch (Exception ex)
    {
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity?.SetTag("error.message", ex.Message);
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