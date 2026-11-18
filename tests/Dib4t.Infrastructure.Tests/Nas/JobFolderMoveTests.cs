using Dib4t.Application.Nas;
using Dib4t.Infrastructure.Nas;
using Dib4t.Tests.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dib4t.Infrastructure.Tests.Nas;

/// <summary>
/// The job folder move of the SSH adapter against the fake NAS (PLAN.md §5, §5.1; never the real NAS): the legacy
/// <c>ops</c> (three <c>mkdir -p</c>, two <c>mv</c>), the D4 checks on the absolute job folders (a warning when a
/// folder is missing, never a failure), names taken literally (D20), a failing command stopping the operation, and
/// the no-op adapter logging the same command texts.
/// </summary>
public sealed class JobFolderMoveTests(NasFake nas) : IClassFixture<NasFake>, IAsyncLifetime
{
    private static readonly BudgetFolder Budget = new(2025, "Pastelaria D'Ouro $HOME `id`", "25-0001", " Molde \"especial\"  O'Neill ");
    private static readonly JobFolder Job = new(2026, Budget.ClientName, Budget.BudgetNumber, " Molde \"especial\"  O'Neill (rev. 1)");

    private readonly ListLogger<SshNasFileShare> _logger = new();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await nas.ResetAsync();
        await nas.MakeFolderAsync(Budget.Path + "/Geometria");
        await nas.MakeFolderAsync(Budget.Path + "/Documentos");
        await nas.MakeFolderAsync(Budget.Path + "/Outros"); // anything else stays with the budget
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task The_move_makes_the_job_folders_moves_documents_and_geometry_and_checks_them()
    {
        var result = await Adapter().MoveBudgetFolderToJobAsync(Budget, Job, Cancellation);

        result.Should().Be(NasResult.Done);
        (await OpsAsync()).Should().Equal(
            NasOp.MakeDirectory(Job.Path + "/CAD"), NasOp.MakeDirectory(Job.Path + "/CAD/Equipamento"), NasOp.MakeDirectory(Job.Path + "/CAM"),
            NasOp.Move(Budget.Path + "/Documentos", Job.Path + "/Informações Técnicas"), NasOp.Move(Budget.Path + "/Geometria", Job.Path + "/CAD/Artigo"),
            NasOp.TestDirectory(Job.Path + "/Informações Técnicas"), NasOp.TestDirectory(Job.Path + "/CAD/Artigo"));
        (await nas.FoldersAsync()).Should().Equal(new[]
        {
            "/share/Obras", "/share/Obras/2026", "/share/Obras/2026/" + Job.ClientName, Job.Path, Job.Path + "/CAD", Job.Path + "/CAD/Artigo",
            Job.Path + "/CAD/Equipamento", Job.Path + "/CAM", Job.Path + "/Informações Técnicas",
            "/share/Orçamentos", "/share/Orçamentos/2025", "/share/Orçamentos/2025/" + Budget.ClientName, Budget.Path, Budget.Path + "/Outros",
        }.Order(StringComparer.Ordinal));
        _logger.Entries.Select(e => e.EventId).Should().Equal(130, 134, 134, 134, 134, 134, 134, 134, 138);
        _logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task D4_a_folder_missing_after_the_move_is_a_warning_not_a_failure()
    {
        await nas.HideFoldersAsync();

        var result = await Adapter().MoveBudgetFolderToJobAsync(Budget, Job, Cancellation);

        result.Should().Be(NasResult.Done);
        var warnings = _logger.Entries.Where(e => e.EventId == 144).ToList();
        warnings.Should().HaveCount(2).And.AllSatisfy(e => e.Level.Should().Be(LogLevel.Warning));
        warnings[0].Properties["Command"].Should().Contain("Informações Técnicas");
    }

    [Fact]
    public async Task A_missing_budget_folder_fails_at_its_mv_and_nothing_after_it_runs()
    {
        await nas.ResetAsync();

        var result = await Adapter().MoveBudgetFolderToJobAsync(Budget, Job, Cancellation);

        result.Should().Be(NasResult.Failed);
        (await OpsAsync()).Select(o => o.Op).Should().Equal("mkdir-p", "mkdir-p", "mkdir-p", "mv");
        _logger.Entries.Should().ContainSingle(e => e.EventId == 135).Which.Properties["Stderr"].Should().Contain("Documentos");
    }

    [Fact]
    public async Task A_failing_mkdir_stops_before_anything_is_moved()
    {
        await nas.MakeFileAsync("/share/Obras/2026"); // a file where the year folder should be

        var result = await Adapter().MoveBudgetFolderToJobAsync(Budget, Job, Cancellation);

        result.Should().Be(NasResult.Failed);
        (await OpsAsync()).Should().ContainSingle().Which.Op.Should().Be("mkdir-p");
        (await nas.FoldersAsync()).Should().Contain(Budget.Path + "/Documentos");
    }

    [Fact]
    public async Task The_no_op_adapter_logs_the_same_commands()
    {
        await Adapter().MoveBudgetFolderToJobAsync(Budget, Job, Cancellation);
        var sent = await nas.CommandsAsync();
        var logger = new ListLogger<LoggingNasFileShare>();

        var result = await new LoggingNasFileShare("environment Development", logger).MoveBudgetFolderToJobAsync(Budget, Job, Cancellation);

        result.Should().Be(NasResult.Done);
        logger.Entries.Select(e => e.Properties["Command"]).Should().Equal(sent);
        logger.Entries.Should().AllSatisfy(e => e.Message.Should().StartWith("NAS off (environment Development), would run: "));
    }

    [Fact]
    public async Task A_nul_in_a_name_is_refused_before_anything_is_sent()
    {
        var result = await Adapter().MoveBudgetFolderToJobAsync(Budget, Job with { Descriptive = "a\0b" }, Cancellation);
        var off = await new LoggingNasFileShare("x", new ListLogger<LoggingNasFileShare>()).MoveBudgetFolderToJobAsync(Budget with { ClientName = "\0" }, Job, Cancellation);

        (result, off).Should().Be((NasResult.Failed, NasResult.Failed));
        (await nas.CommandsAsync()).Should().BeEmpty();
    }

    private SshNasFileShare Adapter() => new(Options.Create(new NasOptions
    {
        Enabled = true,
        Host = nas.Host,
        Port = nas.Port,
        UserName = NasFake.UserName,
        Password = NasFake.Password,
        HostFingerprint = nas.HostFingerprint,
    }), _logger);

    private async Task<IReadOnlyList<NasOp>> OpsAsync() => [.. (await nas.CommandsAsync()).SelectMany(NasOp.FromCommand)];
}
