using System.Diagnostics;
using System.Net.Sockets;
using Dib4t.Application.Nas;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Dib4t.Infrastructure.Nas;

/// <summary>
/// The NAS over SSH (SSH.NET; legacy <c>ssh2-promise</c>, PLAN.md §5). One connection per operation, one exec
/// channel per command, in the legacy order. A command fails on a non-zero exit status. Every step is logged in the
/// category <c>Nas</c> (§3.3): connect/disconnect, each command's exact text, exit status, stdout/stderr (at most
/// 2 KB each) and duration. The password is never logged.
/// </summary>
internal sealed partial class SshNasFileShare(IOptions<NasOptions> options, ILogger<SshNasFileShare> logger) : INasFileShare
{
    /// <summary>Longest stdout/stderr text kept in a log entry (PLAN.md §3.3).</summary>
    public const int MaxLoggedOutput = 2048;

    public bool IsEnabled => true;

    public Task<NasResult> CreateBudgetFolderAsync(BudgetFolder folder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!NasCommands.TryCreateBudgetFolder(folder, out var commands))
        {
            LogNulRefused(folder.Path);
            return Task.FromResult(NasResult.Failed);
        }

        return RunAsync(async run =>
        {
            foreach (var command in commands.MakeDirectories)
            {
                if (await run(command) is null)
                {
                    return NasResult.Failed;
                }
            }

            var check = await run(commands.Check);
            return check is null ? NasResult.Failed
                : string.Equals(check.Trim(), "1", StringComparison.Ordinal) ? NasResult.Done
                : NasResult.FolderMissing;
        }, cancellationToken);
    }

    public Task<NasResult> RenameBudgetFolderAsync(BudgetFolder current, BudgetFolder renamed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(renamed);
        if (!NasCommands.TryRenameBudgetFolder(current, renamed, out var command))
        {
            LogNulRefused(renamed.Path);
            return Task.FromResult(NasResult.Failed);
        }

        return RunAsync(async run => await run(command) is null ? NasResult.Failed : NasResult.Done, cancellationToken);
    }

    public Task<NasResult> MoveBudgetFolderToJobAsync(BudgetFolder budget, JobFolder job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!NasCommands.TryMoveToJob(budget, job, out var commands))
        {
            LogNulRefused(job.Path);
            return Task.FromResult(NasResult.Failed);
        }

        return RunAsync(async run =>
        {
            foreach (var command in commands.MakeDirectories.Concat(commands.Moves))
            {
                if (await run(command) is null)
                {
                    return NasResult.Failed;
                }
            }

            // D4: legacy never looks at these checks; here a missing folder is a warning, the outcome stays the same.
            foreach (var check in commands.Checks)
            {
                if (await run(check) is { } stdout && !string.Equals(stdout.Trim(), "1", StringComparison.Ordinal))
                {
                    LogMovedFolderMissing(check);
                }
            }

            return NasResult.Done;
        }, cancellationToken);
    }

    /// <summary>Connects, runs <paramref name="body"/> (its <c>run</c> answers stdout, or <c>null</c> when the command failed) and disconnects.</summary>
    private async Task<NasResult> RunAsync(Func<Func<string, Task<string?>>, Task<NasResult>> body, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var connection = new PasswordConnectionInfo(settings.Host, settings.Port, settings.UserName, settings.Password)
        {
            Timeout = TimeSpan.FromSeconds(settings.ConnectTimeoutSeconds),
        };
        using var client = new SshClient(connection);
        string? fingerprint = null;
        client.HostKeyReceived += (_, e) =>
        {
            fingerprint = e.FingerPrintSHA256.TrimEnd('=');
            e.CanTrust = settings.PinnedFingerprint() is not { } pinned || string.Equals(pinned, fingerprint, StringComparison.Ordinal);
        };

        var connecting = Stopwatch.StartNew();
        try
        {
            await client.ConnectAsync(cancellationToken);
        }
        catch (Exception exception) when (IsConnectionFailure(exception, cancellationToken))
        {
            if (exception is SshConnectionException && fingerprint is not null && settings.PinnedFingerprint() is { } pinned
                && !string.Equals(pinned, fingerprint, StringComparison.Ordinal))
            {
                LogFingerprintRefused(settings.Host, settings.Port, fingerprint);
            }
            else
            {
                LogConnectFailed(exception, settings.Host, settings.Port, settings.UserName, connecting.ElapsedMilliseconds, exception.Message);
            }

            return NasResult.Failed;
        }

        LogConnected(settings.Host, settings.Port, settings.UserName, connecting.ElapsedMilliseconds, fingerprint);
        if (settings.PinnedFingerprint() is null)
        {
            LogFingerprintNotPinned(settings.Host, fingerprint);
        }

        var connected = Stopwatch.StartNew();
        try
        {
            return await body(command => RunCommandAsync(client, command, settings, cancellationToken));
        }
        finally
        {
            client.Disconnect();
            LogDisconnected(settings.Host, settings.Port, connected.ElapsedMilliseconds);
        }
    }

    private async Task<string?> RunCommandAsync(SshClient client, string text, NasOptions settings, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.CommandTimeoutSeconds));
        using var command = client.CreateCommand(text);
        try
        {
            await command.ExecuteAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogCommandTimedOut(text, started.ElapsedMilliseconds);
            return null;
        }
        catch (Exception exception) when (exception is SshException or SocketException or IOException or InvalidOperationException)
        {
            LogCommandBroken(exception, text, started.ElapsedMilliseconds, exception.Message);
            return null;
        }

        var stdout = command.Result;
        var (loggedStdout, loggedStderr) = (Trimmed(stdout), Trimmed(command.Error));
        if (command.ExitStatus == 0)
        {
            LogCommandRan(text, 0, started.ElapsedMilliseconds, loggedStdout, loggedStderr);
            return stdout;
        }

        LogCommandFailed(text, command.ExitStatus, started.ElapsedMilliseconds, loggedStdout, loggedStderr);
        return null;
    }

    private static bool IsConnectionFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is SshException or SocketException or IOException or TimeoutException or InvalidOperationException
        || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    internal static string Trimmed(string text) => text.Length <= MaxLoggedOutput ? text : text[..MaxLoggedOutput] + "…";

    [LoggerMessage(EventId = 130, Level = LogLevel.Information,
        Message = "NAS {Host}:{Port}: connected as {SshUser} in {ElapsedMs} ms (host SHA256:{Fingerprint})")]
    private partial void LogConnected(string host, int port, string sshUser, long elapsedMs, string? fingerprint);

    [LoggerMessage(EventId = 131, Level = LogLevel.Warning,
        Message = "NAS {Host}: host SHA256:{Fingerprint} accepted without a check; set Nas:HostFingerprint to pin it")]
    private partial void LogFingerprintNotPinned(string host, string? fingerprint);

    [LoggerMessage(EventId = 132, Level = LogLevel.Error,
        Message = "NAS {Host}:{Port}: host SHA256:{Fingerprint} refused, Nas:HostFingerprint names another one")]
    private partial void LogFingerprintRefused(string host, int port, string fingerprint);

    [LoggerMessage(EventId = 133, Level = LogLevel.Error,
        Message = "NAS {Host}:{Port}: no connection as {SshUser} after {ElapsedMs} ms: {Reason}")]
    private partial void LogConnectFailed(Exception exception, string host, int port, string sshUser, long elapsedMs, string reason);

    [LoggerMessage(EventId = 134, Level = LogLevel.Information,
        Message = "NAS ran: {Command} → exit {ExitCode} in {ElapsedMs} ms (stdout: {Stdout}, stderr: {Stderr})")]
    private partial void LogCommandRan(string command, int exitCode, long elapsedMs, string stdout, string stderr);

    [LoggerMessage(EventId = 135, Level = LogLevel.Error,
        Message = "NAS command failed: {Command} → exit {ExitCode} in {ElapsedMs} ms (stdout: {Stdout}, stderr: {Stderr})")]
    private partial void LogCommandFailed(string command, int? exitCode, long elapsedMs, string stdout, string stderr);

    [LoggerMessage(EventId = 136, Level = LogLevel.Error, Message = "NAS command timed out after {ElapsedMs} ms: {Command}")]
    private partial void LogCommandTimedOut(string command, long elapsedMs);

    [LoggerMessage(EventId = 137, Level = LogLevel.Error, Message = "NAS command broken off after {ElapsedMs} ms: {Command}: {Reason}")]
    private partial void LogCommandBroken(Exception exception, string command, long elapsedMs, string reason);

    [LoggerMessage(EventId = 138, Level = LogLevel.Information, Message = "NAS {Host}:{Port}: disconnected after {ElapsedMs} ms")]
    private partial void LogDisconnected(string host, int port, long elapsedMs);

    [LoggerMessage(EventId = 144, Level = LogLevel.Warning, Message = "NAS folder not there after the move (D4, the job is created anyway): {Command}")]
    private partial void LogMovedFolderMissing(string command);

    [LoggerMessage(EventId = 139, Level = LogLevel.Error, Message = "NAS path refused, it contains a NUL character: {Path}")]
    private partial void LogNulRefused(string path);
}
