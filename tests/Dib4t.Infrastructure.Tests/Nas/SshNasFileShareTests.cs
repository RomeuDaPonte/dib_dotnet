using Dib4t.Application.Nas;
using Dib4t.Infrastructure.Logging;
using Dib4t.Infrastructure.Nas;
using Dib4t.SharedKernel;
using Dib4t.Tests.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dib4t.Infrastructure.Tests.Nas;

/// <summary>
/// The SSH.NET NAS adapter against the fake NAS (an OpenSSH container that records commands, PLAN.md §5.1; never
/// the real NAS): the folders it makes and moves, names taken literally (quotes, <c>$</c>, backticks, newlines,
/// non-NFC: D20 and §5 "SSH safety"), the outcomes, the host key check and a <c>Nas</c> log entry per step
/// (§3.3) without the password. The no-op adapter logs the very same command texts.
/// </summary>
public sealed class SshNasFileShareTests(NasFake nas) : IClassFixture<NasFake>, IAsyncLifetime
{
    /// <summary>Every character that broke or could break the legacy concatenation, in a client and a descriptive.</summary>
    private static readonly BudgetFolder Tricky = new(2026, "Pastelaria D'Ouro $HOME `id` (Porto)", "26-0001",
        " Molde \"especial\"  O'Neill\nlinha 2 'x'\\' ");

    /// <summary>Non-NFC on purpose (§4.5): c + combining cedilla, a + combining tilde.</summary>
    private static readonly BudgetFolder NonNfc = new(2026, "Fundição Norte", "26-0002", "Caixa");

    private readonly ListLogger<SshNasFileShare> _logger = new();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await nas.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Create_makes_the_folder_then_geometria_and_documentos_and_checks_it_names_taken_literally()
    {
        var result = await Adapter().CreateBudgetFolderAsync(Tricky, Cancellation);
        var nonNfc = await Adapter().CreateBudgetFolderAsync(NonNfc, Cancellation);

        result.Should().Be(NasResult.Done);
        nonNfc.Should().Be(NasResult.Done);
        var path = Tricky.Path;
        (await OpsAsync()).Should().Equal(
            NasOp.MakeDirectory(path), NasOp.MakeDirectory(path + "/Geometria"), NasOp.MakeDirectory(path + "/Documentos"), NasOp.TestDirectory(path),
            NasOp.MakeDirectory(NonNfc.Path), NasOp.MakeDirectory(NonNfc.Path + "/Geometria"), NasOp.MakeDirectory(NonNfc.Path + "/Documentos"),
            NasOp.TestDirectory(NonNfc.Path));
        (await nas.FoldersAsync()).Should().Equal(new[]
        {
            "/share/Obras", "/share/Orçamentos", "/share/Orçamentos/2026",
            "/share/Orçamentos/2026/" + Tricky.ClientName, path, path + "/Documentos", path + "/Geometria",
            "/share/Orçamentos/2026/" + NonNfc.ClientName, NonNfc.Path, NonNfc.Path + "/Documentos", NonNfc.Path + "/Geometria",
        }.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Every_step_is_logged_with_the_exact_command_and_never_the_password()
    {
        await Adapter().CreateBudgetFolderAsync(Tricky, Cancellation);

        var commands = await nas.CommandsAsync();
        _logger.Entries.Select(e => e.EventId).Should().Equal(130, 134, 134, 134, 134, 138);
        _logger.Entries.Where(e => e.EventId == 134).Select(e => e.Properties["Command"]).Should().Equal(commands);
        _logger.Entries[^2].Properties["Stdout"].Should().Be("1\n");
        _logger.Entries[0].Message.Should().Contain($"connected as {NasFake.UserName}").And.Contain(nas.HostFingerprint);
        _logger.Entries.Should().NotContain(e => e.Message.Contains(NasFake.Password, StringComparison.Ordinal)
            || e.Properties.Values.Any(v => v.Contains(NasFake.Password, StringComparison.Ordinal)));
        LogCategoryMap.Resolve(typeof(SshNasFileShare).FullName).Should().Be(LogCategories.Nas);
        LogCategoryMap.Resolve(typeof(LoggingNasFileShare).FullName).Should().Be(LogCategories.Nas);
    }

    [Fact]
    public async Task The_no_op_adapter_logs_the_same_commands_and_connects_to_nothing()
    {
        var logger = new ListLogger<LoggingNasFileShare>();
        var off = new LoggingNasFileShare("environment Development", logger);
        await Adapter().CreateBudgetFolderAsync(Tricky, Cancellation);
        await Adapter().RenameBudgetFolderAsync(Tricky, Tricky with { Descriptive = "Novo" }, Cancellation);
        var sent = await nas.CommandsAsync();
        await nas.ResetAsync();

        (await off.CreateBudgetFolderAsync(Tricky, Cancellation)).Should().Be(NasResult.Done);
        (await off.RenameBudgetFolderAsync(Tricky, Tricky with { Descriptive = "Novo" }, Cancellation)).Should().Be(NasResult.Done);

        off.IsEnabled.Should().BeFalse();
        logger.Entries.Select(e => e.Properties["Command"]).Should().Equal(sent);
        logger.Entries.Should().AllSatisfy(e => e.Message.Should().StartWith("NAS off (environment Development), would run: "));
        (await nas.CommandsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_folder_missing_after_mkdir_is_folder_missing()
    {
        await nas.HideFoldersAsync();

        (await Adapter().CreateBudgetFolderAsync(NonNfc, Cancellation)).Should().Be(NasResult.FolderMissing);
    }

    [Fact]
    public async Task A_failing_mkdir_stops_the_creation_and_is_logged_with_its_stderr()
    {
        await nas.MakeFileAsync("/share/Orçamentos/2026");

        var result = await Adapter().CreateBudgetFolderAsync(NonNfc, Cancellation);

        result.Should().Be(NasResult.Failed);
        (await nas.CommandsAsync()).Should().HaveCount(1);
        var failed = _logger.Entries.Single(e => e.EventId == 135);
        failed.Level.Should().Be(LogLevel.Error);
        failed.Properties["ExitCode"].Should().Be("1");
        failed.Properties["Stderr"].Should().Contain("mkdir");
        _logger.Entries[^1].EventId.Should().Be(138, "the connection is closed after a failure too");
    }

    [Fact]
    public async Task Rename_moves_the_folder_with_its_content()
    {
        var renamed = Tricky with { Descriptive = "Máquina de corte CNC" };
        await nas.MakeFolderAsync(Tricky.Path + "/Documentos");

        var result = await Adapter().RenameBudgetFolderAsync(Tricky, renamed, Cancellation);

        result.Should().Be(NasResult.Done);
        (await OpsAsync()).Should().Equal(NasOp.Move(Tricky.Path, renamed.Path));
        (await nas.FoldersAsync()).Should().Contain(renamed.Path + "/Documentos").And.NotContain(Tricky.Path);
    }

    [Fact]
    public async Task Renaming_a_missing_folder_fails_as_in_legacy()
    {
        var result = await Adapter().RenameBudgetFolderAsync(NonNfc, NonNfc with { Descriptive = "Outra" }, Cancellation);

        result.Should().Be(NasResult.Failed);
        _logger.Entries.Should().ContainSingle(e => e.EventId == 135);
    }

    [Fact]
    public async Task A_wrong_password_or_no_server_fails_without_running_anything()
    {
        var wrongPassword = await Adapter(o => o.Password = "wrong").CreateBudgetFolderAsync(NonNfc, Cancellation);
        var noServer = await Adapter(o => (o.Host, o.Port, o.ConnectTimeoutSeconds) = ("127.0.0.1", 1, 2)).CreateBudgetFolderAsync(NonNfc, Cancellation);

        wrongPassword.Should().Be(NasResult.Failed);
        noServer.Should().Be(NasResult.Failed);
        (await nas.CommandsAsync()).Should().BeEmpty();
        _logger.Entries.Where(e => e.EventId == 133).Should().HaveCount(2).And.AllSatisfy(e => e.Level.Should().Be(LogLevel.Error));
        _logger.Entries.Should().NotContain(e => e.Message.Contains("wrong", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_pinned_host_key_is_checked_and_an_unpinned_one_is_a_warning()
    {
        var pinned = await Adapter().CreateBudgetFolderAsync(NonNfc, Cancellation);
        var withoutPrefix = await Adapter(o => o.HostFingerprint = o.HostFingerprint["SHA256:".Length..] + "=").CreateBudgetFolderAsync(NonNfc, Cancellation);
        _logger.Entries.Should().NotContain(e => e.EventId == 131);
        var unpinned = await Adapter(o => o.HostFingerprint = "").CreateBudgetFolderAsync(NonNfc, Cancellation);
        var other = await Adapter(o => o.HostFingerprint = "SHA256:ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og").CreateBudgetFolderAsync(NonNfc, Cancellation);

        (pinned, withoutPrefix, unpinned, other).Should().Be((NasResult.Done, NasResult.Done, NasResult.Done, NasResult.Failed));
        _logger.Entries.Should().ContainSingle(e => e.EventId == 131).Which.Level.Should().Be(LogLevel.Warning);
        _logger.Entries.Should().ContainSingle(e => e.EventId == 132).Which.Message.Should().Contain(nas.HostFingerprint["SHA256:".Length..]);
        (await nas.CommandsAsync()).Should().HaveCount(12, "nothing runs on a host with another key");
    }

    [Fact]
    public async Task A_command_that_takes_too_long_is_a_failure()
    {
        await nas.SlowDownAsync();

        var result = await Adapter(o => o.CommandTimeoutSeconds = 1).RenameBudgetFolderAsync(NonNfc, NonNfc with { Descriptive = "Outra" }, Cancellation);

        result.Should().Be(NasResult.Failed);
        _logger.Entries.Should().ContainSingle(e => e.EventId == 136);
    }

    [Fact]
    public async Task A_nul_in_a_name_is_refused_before_connecting()
    {
        var result = await Adapter().CreateBudgetFolderAsync(NonNfc with { Descriptive = "a\0b" }, Cancellation);
        var renamed = await Adapter().RenameBudgetFolderAsync(NonNfc, NonNfc with { Descriptive = "a\0b" }, Cancellation);

        (result, renamed).Should().Be((NasResult.Failed, NasResult.Failed));
        _logger.Entries.Select(e => e.EventId).Should().Equal(139, 139);
    }

    private SshNasFileShare Adapter(Action<NasOptions>? configure = null)
    {
        var options = new NasOptions
        {
            Enabled = true,
            Host = nas.Host,
            Port = nas.Port,
            UserName = NasFake.UserName,
            Password = NasFake.Password,
            HostFingerprint = nas.HostFingerprint,
        };
        configure?.Invoke(options);
        return new SshNasFileShare(Options.Create(options), _logger);
    }

    private async Task<IReadOnlyList<NasOp>> OpsAsync() => [.. (await nas.CommandsAsync()).SelectMany(NasOp.FromCommand)];
}

/// <summary>Keeps every entry with its event id, level, rendered message and properties (as text).</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<Entry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        var properties = state is IEnumerable<KeyValuePair<string, object?>> pairs
            ? pairs.ToDictionary(p => p.Key, p => Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "", StringComparer.Ordinal)
            : [];
        lock (Entries)
        {
            Entries.Add(new Entry(logLevel, eventId.Id, formatter(state, exception), properties, exception));
        }
    }

    internal sealed record Entry(LogLevel Level, int EventId, string Message, IReadOnlyDictionary<string, string> Properties, Exception? Exception);
}
