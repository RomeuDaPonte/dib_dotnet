using Dib4t.Application.Database;
using Dib4t.Application.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Dib4t.Infrastructure.Mongo;

/// <summary>
/// Runs <see cref="DefaultDataSeeder"/> at startup (legacy <c>startup/seedBd.js</c>, PLAN.md §7.2). Hosted services
/// start before the server accepts requests, so with a reachable database the seed is done before the first request
/// and can never interleave with a restore. When the database does not answer within
/// <see cref="MongoOptions.StartupSeedTimeout"/>, the app starts anyway and the seed is retried in the background
/// every <see cref="MongoOptions.StartupCheckRetryInterval"/>, each time under the <see cref="DatabaseOperationLock"/>
/// (it waits while a backup or restore runs).
/// </summary>
internal sealed partial class StartupSeeding(
    IServiceScopeFactory scopes,
    DatabaseOperationLock operationLock,
    IOptions<MongoOptions> options,
    ILogger<StartupSeeding> logger) : IHostedService, IDisposable
{
    public const string Operation = "seed";

    private readonly CancellationTokenSource _stopping = new();
    private Task? _background;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Value.StartupSeedTimeout);
        if (await TrySeedAsync(timeout.Token))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _background = RetryAsync(_stopping.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        if (_background is not null)
        {
            await _background.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    public void Dispose() => _stopping.Dispose();

    private async Task RetryAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.Value.StartupCheckRetryInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (await TrySeedAsync(stoppingToken))
            {
                return;
            }
        }
    }

    private async Task<bool> TrySeedAsync(CancellationToken cancellationToken)
    {
        using var held = operationLock.TryAcquire(Operation);
        if (held is null)
        {
            LogBusy(operationLock.BusyWith);
            return false;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<DefaultDataSeeder>().SeedAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is TimeoutException or MongoException or OperationCanceledException)
        {
            LogSeedFailed(exception, options.Value.StartupCheckRetryInterval.TotalSeconds);
            return false;
        }
    }

    [LoggerMessage(EventId = 120, Level = LogLevel.Error,
        Message = "Default data (prices, administrator) not seeded: the database did not answer; retrying every {RetrySeconds} s")]
    private partial void LogSeedFailed(Exception exception, double retrySeconds);

    [LoggerMessage(EventId = 121, Level = LogLevel.Information, Message = "Default data seeding waits: {Operation} is running")]
    private partial void LogBusy(string? operation);
}
