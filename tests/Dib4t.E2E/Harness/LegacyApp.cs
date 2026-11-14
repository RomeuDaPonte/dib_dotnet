using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace Dib4t.E2E.Harness;

/// <summary>
/// The UNCHANGED legacy app (C10), started and stopped with <c>tools/legacy/legacy.sh</c> (PLAN.md §10 Phase 0):
/// mongo:4.1.4 on 127.0.0.1:27017, back-end on :3001, front-end on :3000. Those ports are hard-coded in the legacy
/// code, so only one legacy instance can run on the machine. The harness gives it its own DB volume
/// (<see cref="HarnessOptions.LegacyE2EVolume"/>), so the dev copy of the data is never touched.
/// <para>
/// MongoDB.Driver 3.x refuses MongoDB 4.1.4, so the legacy database is restored and read only through the legacy
/// API: <c>POST /api/database/restore</c> and <c>GET /api/database/backup</c> (raw BSON, no side effects).
/// </para>
/// </summary>
public sealed class LegacyApp : IAppUnderTest, IAsyncDisposable
{
    private static readonly int[] Ports = [27017, 3001, 3000];

    private readonly HarnessOptions _options;
    private readonly HttpClient _api;
    private bool _startedByHarness;
    private string? _token;

    public LegacyApp(HarnessOptions options)
    {
        _options = options;
        _api = new HttpClient { BaseAddress = ApiBaseUrl, Timeout = TimeSpan.FromMinutes(5) };
    }

    public string Name => "legacy";

    public Uri UiBaseUrl { get; } = new("http://localhost:3000");

    public Uri ApiBaseUrl { get; } = new("http://localhost:3001/api/");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var busy = Ports.Where(PortOpen).ToList();
        if (busy.Count > 0 && !_options.ReuseLegacy)
        {
            throw new InvalidOperationException(
                $"Port(s) {string.Join(", ", busy)} in use (a legacy app is already running?). The E2E run starts its own " +
                "legacy app on a separate DB volume. Stop it with `tools/legacy/legacy.sh down`, or set DIB4T_E2E_REUSE_LEGACY=1 " +
                "to run against it (its database is then REPLACED by the seed backup).");
        }

        if (busy.Count == 0)
        {
            _startedByHarness = true;
            await RunScriptAsync(["up", "--fresh"], TimeSpan.FromMinutes(8), cancellationToken);
        }
        else if (busy.Count != Ports.Length)
        {
            throw new InvalidOperationException(
                $"DIB4T_E2E_REUSE_LEGACY=1, but only ports {string.Join(", ", busy)} answer; the E2E run needs mongo, back-end and front-end (legacy.sh up).");
        }

        _token = (await RunScriptAsync(["token"], TimeSpan.FromMinutes(1), cancellationToken)).Trim();
        _api.DefaultRequestHeaders.Add("x-auth-token", _token);
    }

    public async Task RestoreAsync(string backupPath, CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(await File.ReadAllBytesAsync(backupPath, cancellationToken));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await _api.PostAsync(new Uri("database/restore", UriKind.Relative), content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"legacy restore failed ({(int)response.StatusCode}): {body}");
        }
    }

    public async Task<DbSnapshot> SnapshotAsync(CancellationToken cancellationToken) =>
        DibbakFile.Parse(await DownloadBackupAsync(cancellationToken)).ToSnapshot();

    /// <summary>A <c>.dibbak</c> made by the legacy app itself (<c>GET /api/database/backup</c>, Settings → Base de dados).</summary>
    public Task<byte[]> DownloadBackupAsync(CancellationToken cancellationToken) =>
        _api.GetByteArrayAsync(new Uri("database/backup", UriKind.Relative), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        _api.Dispose();
        if (_startedByHarness)
        {
            // Stops mongo cleanly (docker stop) and deletes only the E2E volume.
            await RunScriptAsync(["down", "--purge"], TimeSpan.FromMinutes(2), CancellationToken.None);
        }
    }

    private static bool PortOpen(int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", port).Wait(TimeSpan.FromMilliseconds(300));
        }
        catch (AggregateException)
        {
            return false;
        }
    }

    private async Task<string> RunScriptAsync(string[] arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _options.RepoRoot,
        };
        start.ArgumentList.Add(_options.LegacyScript);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["LEGACY_MONGO_VOLUME"] = HarnessOptions.LegacyE2EVolume;
        start.Environment.Remove("NODE_ENV"); // never production mode: no NAS, no printers (§5.1)

        using var process = Process.Start(start)!;
        var output = new StringBuilder();
        var errors = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (errors) { errors.AppendLine(e.Data); } } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"legacy.sh {string.Join(' ', arguments)} did not finish in {timeout}:\n{output}{errors}");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"legacy.sh {string.Join(' ', arguments)} failed ({process.ExitCode}):\n{output}{errors}");
        }

        return output.ToString();
    }
}
