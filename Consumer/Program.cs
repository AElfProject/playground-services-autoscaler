using System.Diagnostics;
using System.IO.Compression;
using StackExchange.Redis;

var consumerGroupName = Environment.GetEnvironmentVariable("CONSUMER_GROUP_NAME") ?? "consumergroup";
var redisConnectionString = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING") ?? "localhost";
var operation = Environment.GetEnvironmentVariable("OPERATION") ?? "build";
var streamName = Environment.GetEnvironmentVariable("STREAM_NAME") ?? $"{operation}stream";
var resultStreamName = Environment.GetEnvironmentVariable("RESULT_STREAM_NAME") ?? $"{operation}resultstream";

var redis = ConnectionMultiplexer.Connect(redisConnectionString);
IDatabase db = redis.GetDatabase();

var consumerName = Guid.NewGuid().ToString();

var consumerGroupReadTask = Task.Run(async () =>
{
    Console.WriteLine($"Starting consumer {consumerName} for {operation} stream");
    string id = string.Empty;
    while (true)
    {
        if (!string.IsNullOrEmpty(id))
        {
            await db.StreamAcknowledgeAsync(streamName, consumerGroupName, id);
            id = string.Empty;
        }
        // read the next result from the stream
        var result = await db.StreamReadGroupAsync(streamName, consumerGroupName, consumerName, ">", 1);
        if (result.Any())
        {
            id = result.FirstOrDefault().Id.ToString() ?? string.Empty;
            var dict = ParseResult(result.First());
            var file = dict["file"];

            try
            {
                var message = await ProcessOperation(file, operation);

                await db.StreamAddAsync(resultStreamName, [new("id", dict["id"]), new("message", message)], "*", 100);
            }
            catch (Exception ex)
            {
                // problem processing file
                await db.StreamAddAsync(resultStreamName, [new("id", dict["id"]), new("error", ex.Message)], "*", 100);
            }
        }
        await Task.Delay(1000);
    }
});

Dictionary<string, string> ParseResult(StreamEntry entry) => entry.Values.ToDictionary(x => x.Name.ToString(), x => x.Value.ToString());

async Task<string> ProcessOperation(string file, string operation)
{
    return operation switch
    {
        "build" => await ProcessBuild(file),
        "test" => await ProcessTest(file),
        _ => throw new InvalidOperationException("Invalid operation"),
    };
}

async Task<string> ProcessBuild(string file)
{
    var (zipPath, tempPath) = await ExtractZipFile(file);

    try
    {
        // find the first .csproj file that does not contain "Tests.csproj"
        var csprojFile = Directory.GetFiles(tempPath, "*.csproj", SearchOption.AllDirectories)
            .FirstOrDefault(x => !x.Contains("Tests.csproj"));

        if (string.IsNullOrEmpty(csprojFile))
        {
            throw new InvalidOperationException("No csproj file found");
        }

        // build the project
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build {csprojFile}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var output = process.StandardOutput.ReadToEnd();
            throw new InvalidOperationException(output);
        }

        // find the dll file and return it
        var dllFile = Directory.GetFiles(tempPath, "*.dll", SearchOption.AllDirectories).FirstOrDefault();
        if (string.IsNullOrEmpty(dllFile))
        {
            throw new InvalidOperationException("No dll file found");
        }

        // convert to base64
        var dllBytes = File.ReadAllBytes(dllFile);
        return Convert.ToBase64String(dllBytes);
    }
    finally
    {
        await CleanUp(tempPath);
    }
}

async Task<string> ProcessTest(string file)
{
    var (zipPath, tempPath) = await ExtractZipFile(file);

    try
    {
        // find the first csproj file that contains "Tests.csproj"
        var csprojFile = Directory.GetFiles(tempPath, "*.csproj", SearchOption.AllDirectories)
            .FirstOrDefault(x => x.Contains("Tests.csproj"));

        if (string.IsNullOrEmpty(csprojFile))
        {
            throw new InvalidOperationException("No test csproj file found");
        }

        // run tests
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"test {csprojFile}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        process.WaitForExit();

        var output = process.StandardOutput.ReadToEnd();
        return output;
    }
    finally
    {
        await CleanUp(tempPath);
    }
}

Task<(string, string)> ExtractZipFile(string file)
{
    var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    var fileBytes = Convert.FromBase64String(file);
    var zipPath = Path.Combine(tempPath, Guid.NewGuid().ToString());
    // extract the zip file
    Directory.CreateDirectory(tempPath);
    File.WriteAllBytes(zipPath, fileBytes);
    // extract all nested folders and files
    ZipFile.ExtractToDirectory(zipPath, tempPath);
    // pretty print the folder structure and files, similar to `tree` command
    PrintDirectoryTree(tempPath, "", true);

    return Task.FromResult((zipPath, tempPath));
}

Task CleanUp(string tempPath)
{
    if (Directory.Exists(tempPath))
    {
        Directory.Delete(tempPath, true);
    }
    return Task.CompletedTask;
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

async Task Init()
{
    await InitStream(streamName, consumerGroupName);
    await InitStream(resultStreamName, consumerGroupName);
}

async Task InitStream(string streamName, string groupName)
{
    if (!await db.KeyExistsAsync(streamName) ||
    (await db.StreamGroupInfoAsync(streamName)).All(x => x.Name != groupName))
    {
        await db.StreamCreateConsumerGroupAsync(streamName, groupName, "0-0", true);
    }
}

// initialize the stream
await Init();

async Task PopulateNugetCache()
{
    // run dotnet tool install AElf.ContractTemplates
    var process = Process.Start("dotnet", "new --install AElf.ContractTemplates");
    await process.WaitForExitAsync();

    // run dotnet new aelf -n HelloWorld
    process = Process.Start("dotnet", "new aelf -n HelloWorld");
    await process.WaitForExitAsync();

    // run dotnet restore
    process = Process.Start("dotnet", "restore test");
    await process.WaitForExitAsync();

    // find the location of the nuget cache
    var nugetCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    // print the directory tree
    PrintDirectoryTree(nugetCache, "", true);
}

await PopulateNugetCache();

// run the task
await consumerGroupReadTask;
