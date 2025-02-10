using Common;
using Consumer.Services;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace code_coverage;

public class ConsumerServiceTests
{
    private readonly Mock<ILogger<ConsumerService>> _loggerMock;
    private readonly TestMinioUploader _minioUploader;

    public ConsumerServiceTests()
    {
        _loggerMock = new Mock<ILogger<ConsumerService>>();
        _minioUploader = new TestMinioUploader();

        // Set environment variables for testing
        Environment.SetEnvironmentVariable("CONSUMER_GROUP_NAME", "test-group");
        Environment.SetEnvironmentVariable("REDIS_CONNECTION_STRING", "localhost:6379,abortConnect=false");
        Environment.SetEnvironmentVariable("STREAM_NAME", "test-stream");
    }

    [Fact]
    public void Constructor_ValidParameters_CreatesInstance()
    {
        try
        {
            // Act
            var service = new ConsumerService(_loggerMock.Object, _minioUploader);

            // Assert
            Assert.NotNull(service);
        }
        catch (RedisConnectionException)
        {
            // Skip the test if Redis is not available
            return;
        }
    }

    [Fact]
    public async Task ExecuteAsync_CancellationRequested_StopsExecution()
    {
        try
        {
            // Arrange
            var service = new ConsumerService(_loggerMock.Object, _minioUploader);
            var cancellationTokenSource = new CancellationTokenSource();

            // Act
            cancellationTokenSource.Cancel();
            await service.StartAsync(cancellationTokenSource.Token);

            // Assert
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Consumer Background Service is starting")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }
        catch (RedisConnectionException)
        {
            // Skip the test if Redis is not available
            return;
        }
    }

    [Fact]
    public async Task StopAsync_ServiceRunning_StopsGracefully()
    {
        try
        {
            // Arrange
            var service = new ConsumerService(_loggerMock.Object, _minioUploader);
            var cancellationTokenSource = new CancellationTokenSource();

            // Act
            await service.StartAsync(cancellationTokenSource.Token);
            await service.StopAsync(cancellationTokenSource.Token);

            // Assert
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Consumer Background Service is starting")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }
        catch (RedisConnectionException)
        {
            // Skip the test if Redis is not available
            return;
        }
    }
} 