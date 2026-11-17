using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;

namespace Dib4t.Tests.Common;

/// <summary>
/// The fake NAS (PLAN.md §5.1): an OpenSSH server (<c>tests/fixtures/nas-fake</c>) whose login shell records every
/// command it runs, with <c>/share/Orçamentos</c> and <c>/share/Obras</c>. Tests never connect to the real NAS.
/// One container per test class; <see cref="ResetAsync"/> empties it between tests.
/// </summary>
public sealed class NasFake : IAsyncLifetime
{
    public const string UserName = "webapp";
    public const string Password = "fake-nas-password";

    /// <summary>The SSH user's home, as in <c>tests/fixtures/legacy/ssh</c> (relative paths resolve here).</summary>
    public const string Home = "/home/ssh-user";

    private const string CommandLog = "/var/log/nas/commands";
    private const string HideFoldersFlag = "/var/log/nas/hide-folders";
    private const string SlowFlag = "/var/log/nas/slow";

    private static readonly IFutureDockerImage Image = new ImageFromDockerfileBuilder()
        .WithDockerfileDirectory(Path.Combine(Fixtures.Root, "nas-fake"))
        .WithDockerfile("Dockerfile")
        .WithName("dib4t-nas-fake:test")
        .WithCleanUp(false)
        .Build();

    private static readonly SemaphoreSlim ImageBuild = new(1, 1);
    private static bool _imageBuilt;

    private IContainer _container = null!;

    public string Host => _container.Hostname;

    public int Port => _container.GetMappedPublicPort(22);

    /// <summary>The host key as <c>ssh-keygen -lf</c> prints it (<c>SHA256:…</c>).</summary>
    public string HostFingerprint { get; private set; } = string.Empty;

    /// <summary>The <c>Nas</c> configuration that points the app at this fake (SSH on, when the environment is Production).</summary>
    public IReadOnlyDictionary<string, string> Settings => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Nas:Enabled"] = "true",
        ["Nas:Host"] = Host,
        ["Nas:Port"] = Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Nas:UserName"] = UserName,
        ["Nas:Password"] = Password,
        ["Nas:HostFingerprint"] = HostFingerprint,
    };

    public async ValueTask InitializeAsync()
    {
        var cancellation = TestContext.Current.CancellationToken;
        await ImageBuild.WaitAsync(cancellation);
        try
        {
            if (!_imageBuilt)
            {
                await Image.CreateAsync(cancellation);
                _imageBuilt = true;
            }
        }
        finally
        {
            ImageBuild.Release();
        }

        _container = new ContainerBuilder(Image)
            .WithPortBinding(22, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server listening on"))
            .Build();
        await _container.StartAsync(cancellation);
        var fingerprint = await RunAsRootAsync("ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub | cut -d' ' -f2");
        HostFingerprint = fingerprint.Trim();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Back to the start: only the two empty share folders, no recorded commands, folders not hidden.</summary>
    public Task ResetAsync() => RunAsRootAsync(
        $"rm -rf /share/* {CommandLog} {HideFoldersFlag} {SlowFlag} && mkdir -p /share/Orçamentos /share/Obras && chown -R {UserName} /share");

    /// <summary>The folder check answers 0 from now on (mkdir succeeds, the folder seems missing).</summary>
    public Task HideFoldersAsync() => RunAsRootAsync($"touch {HideFoldersFlag}");

    /// <summary>Every command sleeps 5 s from now on.</summary>
    public Task SlowDownAsync() => RunAsRootAsync($"touch {SlowFlag}");

    /// <summary>Makes an empty <b>file</b> at <paramref name="path"/> (a <c>mkdir -p</c> through it then fails).</summary>
    public Task MakeFileAsync(string path) => ExecAsync(["su", UserName, "-s", "/bin/sh", "-c", "touch \"$0\"", path]);

    /// <summary>Every command the NAS ran since the last reset, in order, exactly as received.</summary>
    public async Task<IReadOnlyList<string>> CommandsAsync()
    {
        var log = await RunAsRootAsync($"cat {CommandLog} 2>/dev/null || true");
        return [.. log.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => Encoding.UTF8.GetString(Convert.FromBase64String(line)))];
    }

    /// <summary>Every folder under <c>/share</c> (absolute paths, ordinal order), the two share folders included.</summary>
    public async Task<IReadOnlyList<string>> FoldersAsync()
    {
        var found = await RunAsRootAsync("find /share -mindepth 1 -type d -print0"); // a name may contain a newline
        return [.. found.Split('\0', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal)];
    }

    /// <summary>Makes <paramref name="path"/> (and its parents) as the SSH user.</summary>
    public Task MakeFolderAsync(string path) => ExecAsync(["su", UserName, "-s", "/bin/sh", "-c", "mkdir -p \"$0\"", path]);

    private Task<string> RunAsRootAsync(string script) => ExecAsync(["/bin/sh", "-c", script]);

    private async Task<string> ExecAsync(IList<string> command)
    {
        var result = await _container.ExecAsync(command, TestContext.Current.CancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Fake NAS: {string.Join(' ', command)} → exit {result.ExitCode}: {result.Stderr}");
        }

        return result.Stdout;
    }
}
