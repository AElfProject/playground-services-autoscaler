using System.Diagnostics;
using System.Text.Json;
using Common;
using StackExchange.Redis;

var consumerGroupName = Environment.GetEnvironmentVariable("CONSUMER_GROUP_NAME") ?? "consumergroup";
var redisConnectionString = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING") ?? "localhost";
var buildStreamName = Environment.GetEnvironmentVariable("BUILD_STREAM_NAME") ?? "buildstream";
var testStreamName = Environment.GetEnvironmentVariable("TEST_STREAM_NAME") ?? "teststream";
var templateStreamName = Environment.GetEnvironmentVariable("TEMPLATE_STREAM_NAME") ?? "templatestream";
var timeout = int.Parse(Environment.GetEnvironmentVariable("TIMEOUT") ?? "10000");

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure Redis
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = redisConnectionString; // Update if your Redis server is different
});

// Register the RedisCacheService from the Common library
builder.Services.AddScoped<IRedisCacheService, RedisCacheService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();

var redis = ConnectionMultiplexer.Connect(redisConnectionString);
IDatabase db = redis.GetDatabase();

// post formdata file upload to Redis stream
app.MapPost("/build", async (IFormFile file, IRedisCacheService cacheService) =>
{
    var payload = await PrepareFile(file);
    var key = Guid.NewGuid().ToString();
    var result = await SendToRedisAndWatchForResults(buildStreamName, key, payload, cacheService);
    return Results.Ok(result);
})
.DisableAntiforgery();

app.MapPost("/test", async (IFormFile file, IRedisCacheService cacheService) =>
{
    var payload = await PrepareFile(file);
    var key = Guid.NewGuid().ToString();
    var result = await SendToRedisAndWatchForResults(testStreamName, key, payload, cacheService);
    return Results.Ok(result);
})
.DisableAntiforgery();

app.MapGet("/templates", () =>
{
    return new List<string> { "aelf", "aelf-lottery", "aelf-nft-sale", "aelf-simple-dao" };
});

app.MapGet("/template", async (string template, string templateName, IRedisCacheService cacheService) =>
{
    var payload = JsonSerializer.Serialize(new { template, templateName });
    var key = $"{template}-{templateName}";
    var result = await SendToRedisAndWatchForResults(templateStreamName, key, payload, cacheService);
    return Results.Ok(result);
});

app.Run();

Task<string> PrepareFile(IFormFile file)
{
    // convert file to byte array
    byte[] fileBytes;
    using (var ms = new MemoryStream())
    {
        file.CopyTo(ms);
        fileBytes = ms.ToArray();
    }

    var payload = JsonSerializer.Serialize(new { file = Convert.ToBase64String(fileBytes) });

    // convert file to base64 string
    return Task.FromResult(payload);
}

// reusable code for the above two endpoints
async Task<string> SendToRedisAndWatchForResults(string streamName, string key, string payload, IRedisCacheService cacheService)
{
    await Init();

    // check the cache first
    var cachedData = await cacheService.GetCachedDataAsync<string>(key);
    if (!string.IsNullOrEmpty(cachedData))
    {
        Console.WriteLine($"Message with id {key} returned from cache");
        return cachedData;
    }

    // https://github.com/StackExchange/StackExchange.Redis/issues/1718#issuecomment-1219592426
    var Expiry = TimeSpan.FromMinutes(3);
    var add = await db.ExecuteAsync("XADD", streamName, "MINID", "=", DateTimeOffset.UtcNow.Subtract(Expiry).ToUnixTimeMilliseconds(), "*", "id", key, "payload", payload);
    var msgId = add.ToString();
    Console.WriteLine($"Message added to stream {streamName} with id {key} as {msgId}");

    var timer = Stopwatch.StartNew();

    var consumer = Guid.NewGuid().ToString();

    while (timer.ElapsedMilliseconds < timeout)
    {
        var cachedResponse = await cacheService.GetCachedDataAsync<string>(key);

        if (string.IsNullOrEmpty(cachedResponse))
        {
            await Task.Delay(1000);
            continue;
        }

        Console.WriteLine($"Message with id {key} processed in {timer.ElapsedMilliseconds}ms");
        return cachedResponse;
    }

    return "Timeout";
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