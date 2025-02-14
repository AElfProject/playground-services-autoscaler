using Common;
using Microsoft.Extensions.DependencyInjection;
using Consumer.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AElf.OpenTelemetry;
using Microsoft.Extensions.Configuration;

var minioBucketName = Environment.GetEnvironmentVariable("MINIO_BUCKET_NAME") ?? "your-bucket-name";
var minioAccessKey = Environment.GetEnvironmentVariable("MINIO_ACCESS_KEY") ?? "your-access-key";
var minioSecretKey = Environment.GetEnvironmentVariable("MINIO_SECRET_KEY") ?? "your-secret-key";
var minioServiceURL = Environment.GetEnvironmentVariable("MINIO_SERVICE_URL");

var builder = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((hostContext, config) =>
    {
        config.SetBasePath(Directory.GetCurrentDirectory())
              .AddJsonFile("appsettings.json", optional: false);
    })
    .ConfigureServices((context, services) =>
    {
        services.AddApplication<OpenTelemetryModule>();
        services.AddSingleton(provider => new MinioUploader(minioBucketName, minioAccessKey, minioSecretKey, minioServiceURL));
        services.AddHostedService<ConsumerService>();
    })
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole();
    });

var host = builder.Build();
await host.RunAsync();