using Dib4t.Application.Nas;
using Microsoft.Extensions.Logging;

namespace Dib4t.Infrastructure.Nas;

/// <summary>
/// The NAS when it is off (not Production, or <c>Nas:Enabled=false</c>; PLAN.md §4.7, D5): logs each command it
/// would run, with the same text the SSH adapter sends, and reports success. Never opens a connection.
/// </summary>
internal sealed partial class LoggingNasFileShare(string reason, ILogger<LoggingNasFileShare> logger) : INasFileShare
{
    public bool IsEnabled => false;

    public Task<NasResult> CreateBudgetFolderAsync(BudgetFolder folder, CancellationToken cancellationToken)
    {
        if (!NasCommands.TryCreateBudgetFolder(folder, out var commands))
        {
            LogNulRefused(folder.Path);
            return Task.FromResult(NasResult.Failed);
        }

        foreach (var command in commands.MakeDirectories.Append(commands.Check))
        {
            LogWouldRun(reason, command);
        }

        return Task.FromResult(NasResult.Done);
    }

    public Task<NasResult> RenameBudgetFolderAsync(BudgetFolder current, BudgetFolder renamed, CancellationToken cancellationToken)
    {
        if (!NasCommands.TryRenameBudgetFolder(current, renamed, out var command))
        {
            LogNulRefused(renamed.Path);
            return Task.FromResult(NasResult.Failed);
        }

        LogWouldRun(reason, command);
        return Task.FromResult(NasResult.Done);
    }

    [LoggerMessage(EventId = 140, Level = LogLevel.Information, Message = "NAS off ({Reason}), would run: {Command}")]
    private partial void LogWouldRun(string reason, string command);

    [LoggerMessage(EventId = 141, Level = LogLevel.Error, Message = "NAS path refused, it contains a NUL character: {Path}")]
    private partial void LogNulRefused(string path);
}
