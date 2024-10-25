using System.Diagnostics;
using Common;
using StackExchange.Redis;

var consumerGroupName = Environment.GetEnvironmentVariable("CONSUMER_GROUP_NAME") ?? "consumergroup";
var redisConnectionString = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING") ?? "localhost";
var buildStreamName = Environment.GetEnvironmentVariable("BUILD_STREAM_NAME") ?? "buildstream";
var testStreamName = Environment.GetEnvironmentVariable("TEST_STREAM_NAME") ?? "teststream";
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

var allResults = new Dictionary<string, string>();

// post formdata file upload to Redis stream
app.MapPost("/build", async (IFormFile file, IRedisCacheService cacheService) =>
{
    var result = await SendToRedisAndWatchForResults(buildStreamName, file, cacheService);
    return Results.Ok(result);
})
.DisableAntiforgery();

app.MapPost("/test", async (IFormFile file, IRedisCacheService cacheService) =>
{
    var result = await SendToRedisAndWatchForResults(testStreamName, file, cacheService);
    return Results.Ok(result);
})
.DisableAntiforgery();

app.Run();

// reusable code for the above two endpoints
async Task<string> SendToRedisAndWatchForResults(string streamName, IFormFile file, IRedisCacheService cacheService)
{
    await Init();

    string key = Guid.NewGuid().ToString();

    // convert file to byte array
    byte[] fileBytes;
    using (var ms = new MemoryStream())
    {
        file.CopyTo(ms);
        fileBytes = ms.ToArray();
    }

    // convert file to base64 string
    var fileBase64 = Convert.ToBase64String(fileBytes);

    // Adding a message to the stream
    // var messageId = await db.StreamAddAsync(streamName, [new("id", key), new("file", fileBase64)], "*", 100);

    // https://github.com/StackExchange/StackExchange.Redis/issues/1718#issuecomment-1219592426
    var Expiry = TimeSpan.FromMinutes(3);
    var add = await db.ExecuteAsync("XADD", streamName, "MINID", "=", DateTimeOffset.UtcNow.Subtract(Expiry).ToUnixTimeMilliseconds(), "*", "id", key, "file", fileBase64);
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
}

async Task InitStream(string streamName, string groupName)
{
    if (!await db.KeyExistsAsync(streamName) ||
    (await db.StreamGroupInfoAsync(streamName)).All(x => x.Name != groupName))
    {
        await db.StreamCreateConsumerGroupAsync(streamName, groupName, "0-0", true);
    }
}