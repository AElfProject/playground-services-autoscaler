using System.Diagnostics;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();

var consumerGroupName = Environment.GetEnvironmentVariable("CONSUMER_GROUP_NAME") ?? "consumergroup";
var redisConnectionString = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING") ?? "localhost";
var buildStreamName = Environment.GetEnvironmentVariable("BUILD_STREAM_NAME") ?? "buildstream";
var buildResultStreamName = Environment.GetEnvironmentVariable("BUILD_RESULT_STREAM_NAME") ?? "buildresultstream";
var testStreamName = Environment.GetEnvironmentVariable("TEST_STREAM_NAME") ?? "teststream";
var testResultStreamName = Environment.GetEnvironmentVariable("TEST_RESULT_STREAM_NAME") ?? "testresultstream";
var timeout = int.Parse(Environment.GetEnvironmentVariable("TIMEOUT") ?? "10000");

var redis = ConnectionMultiplexer.Connect(redisConnectionString);
IDatabase db = redis.GetDatabase();

// post formdata file upload to Redis stream
app.MapPost("/build", async (IFormFile file) =>
{
    var result = await SendToRedisAndWatchForResults(buildStreamName, file, buildResultStreamName);
    return Results.Ok(result);
})
.DisableAntiforgery();

app.MapPost("/test", async (IFormFile file) =>
{
    var result = await SendToRedisAndWatchForResults(testStreamName, file, testResultStreamName);
    return Results.Ok(result);
})
.DisableAntiforgery();

await PopulateNugetCache();

app.Run();

// reusable code for the above two endpoints
async Task<string> SendToRedisAndWatchForResults(string streamName, IFormFile file, string resultStreamName)
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
    var messageId = await db.StreamAddAsync(streamName, [new("id", key), new("file", fileBase64)], "*", 100);

    var timer = Stopwatch.StartNew();

    while (timer.ElapsedMilliseconds < timeout)
    {
        // read the result with same id as the message above, where the id is inside the message itself
        var result = await db.StreamReadGroupAsync(resultStreamName, consumerGroupName, "consumer", ">", 1);
        if (result.Any())
        {
            var current = result.First();
            var dict = ParseResult(current);

            if (dict["id"] == key)
            {
                await db.StreamAcknowledgeAsync(resultStreamName, consumerGroupName, current.Id);

                try
                {
                    return dict["message"];
                }
                catch (Exception)
                {
                    try
                    {
                        return dict["error"];
                    }
                    catch (Exception ex)
                    {
                        return ex.Message;
                    }
                }
            }
        }
        await Task.Delay(1000);
    }

    // since there is a timeout, acknowledge the original message to avoid reprocessing
    await db.StreamAcknowledgeAsync(streamName, consumerGroupName, messageId);

    return "Timeout";
}

Dictionary<string, string> ParseResult(StreamEntry entry) => entry.Values.ToDictionary(x => x.Name.ToString(), x => x.Value.ToString());

async Task Init()
{
    await InitStream(buildStreamName, consumerGroupName);
    await InitStream(buildResultStreamName, consumerGroupName);
    await InitStream(testStreamName, consumerGroupName);
    await InitStream(testResultStreamName, consumerGroupName);
}

async Task InitStream(string streamName, string groupName)
{
    if (!await db.KeyExistsAsync(streamName) ||
    (await db.StreamGroupInfoAsync(streamName)).All(x => x.Name != groupName))
    {
        await db.StreamCreateConsumerGroupAsync(streamName, groupName, "0-0", true);
    }
}

async Task PopulateNugetCache()
{
    // run dotnet tool install AElf.ContractTemplates
    var process = Process.Start("dotnet", "tool install AElf.ContractTemplates -g");
    await process.WaitForExitAsync();

    // run dotnet new aelf -n HelloWorld
    process = Process.Start("dotnet", "new aelf -n HelloWorld");
    await process.WaitForExitAsync();

    // run dotnet restore
    process = Process.Start("dotnet", "restore");
    await process.WaitForExitAsync();

    // find the location of the nuget cache
    var nugetCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    // print the directory tree
    PrintDirectoryTree(nugetCache, "", true);
}

static void PrintDirectoryTree(string dirPath, string indent, bool isLast)
{
    // Print the current directory
    Console.WriteLine($"{indent}+- {Path.GetFileName(dirPath)}");

    // Update the indentation for subdirectories
    indent += isLast ? "   " : "|  ";

    // Get all subdirectories and files
    var subDirs = Directory.GetDirectories(dirPath);
    var files = Directory.GetFiles(dirPath);

    // Loop through each subdirectory
    for (int i = 0; i < subDirs.Length; i++)
    {
        bool isLastDir = (i == subDirs.Length - 1) && (files.Length == 0);
        PrintDirectoryTree(subDirs[i], indent, isLastDir);
    }

    // Loop through each file
    for (int i = 0; i < files.Length; i++)
    {
        bool isLastFile = i == files.Length - 1;
        Console.WriteLine($"{indent}+- {Path.GetFileName(files[i])}");
    }
}