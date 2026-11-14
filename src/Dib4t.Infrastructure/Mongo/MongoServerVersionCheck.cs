using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.Infrastructure.Mongo;

/// <summary>
/// At startup (PLAN.md §2.2): logs the server's <c>buildInfo.version</c> and <b>stops the app</b> when the driver does
/// not support the server (e.g. the legacy 4.1.4 by mistake). Runs in the background, so the host still starts
/// without a reachable database; while the server cannot be reached it logs an error and tries again every
/// <see cref="MongoOptions.StartupCheckRetryInterval"/>. A version other than the tested
/// <see cref="MongoOptions.TestedServerMajorVersion"/>.x is a warning, not a stop.
/// </summary>
internal sealed partial class MongoServerVersionCheck(
    IMongoClient client,
    IOptions<MongoOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<MongoServerVersionCheck> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var buildInfo = await client.GetDatabase("admin")
                    .RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1), cancellationToken: stoppingToken);
                Report(buildInfo.GetValue("version", BsonNull.Value).ToString() ?? string.Empty);
                return;
            }
            catch (MongoIncompatibleDriverException exception)
            {
                LogIncompatible(exception);
                lifetime.StopApplication();
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is TimeoutException or MongoException)
            {
                LogUnreachable(exception, options.Value.StartupCheckRetryInterval.TotalSeconds);
            }

            try
            {
                await Task.Delay(options.Value.StartupCheckRetryInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Report(string version)
    {
        if (version.StartsWith($"{MongoOptions.TestedServerMajorVersion}.", StringComparison.Ordinal))
        {
            LogVersion(version);
        }
        else
        {
            LogUntestedVersion(version, MongoOptions.TestedServerMajorVersion);
        }
    }

    [LoggerMessage(EventId = 110, Level = LogLevel.Information, Message = "MongoDB server version {ServerVersion}")]
    private partial void LogVersion(string serverVersion);

    [LoggerMessage(EventId = 111, Level = LogLevel.Warning,
        Message = "MongoDB server version {ServerVersion} has not been tested; the app is tested on {TestedMajorVersion}.x")]
    private partial void LogUntestedVersion(string serverVersion, int testedMajorVersion);

    [LoggerMessage(EventId = 112, Level = LogLevel.Critical,
        Message = "MongoDB server is not supported by the driver; stopping the app")]
    private partial void LogIncompatible(Exception exception);

    [LoggerMessage(EventId = 113, Level = LogLevel.Error,
        Message = "MongoDB server not reachable at startup; retrying in {RetrySeconds} s")]
    private partial void LogUnreachable(Exception exception, double retrySeconds);
}
