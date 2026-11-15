using Microsoft.Playwright;

namespace Dib4t.E2E.Harness;

/// <summary>
/// PLAN.md §11.7: starts the legacy app (unchanged, mongo:4.1.4) and the new app (mongo:9.0.8) once per test run,
/// loads the SAME seed backup into both databases, and shares one browser. Use it through
/// <see cref="E2ETest"/>.
/// </summary>
public sealed class E2EHarness : IAsyncLifetime
{
    private IPlaywright? _playwright;

    public HarnessOptions Options { get; } = HarnessOptions.FromEnvironment();

    public LegacyApp Legacy { get; private set; } = null!;

    public NewApp New { get; private set; } = null!;

    public IBrowser Browser { get; private set; } = null!;

    /// <summary>For browsers the harness does not own, e.g. a persistent profile that is closed and reopened.</summary>
    public IPlaywright Playwright => _playwright ?? throw new InvalidOperationException("not started");

    /// <summary>The seed backup as loaded into both apps (the expected database content after setup).</summary>
    public DibbakFile Seed { get; private set; } = null!;

    public IEnumerable<IAppUnderTest> Apps => [Legacy, New];

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Seed = DibbakFile.Read(Options.SeedBackup);
        Legacy = new LegacyApp(Options);
        New = new NewApp();

        // The legacy front-end compile and the containers dominate the start-up time: start everything in parallel.
        var playwright = Microsoft.Playwright.Playwright.CreateAsync();
        await Task.WhenAll(Legacy.StartAsync(cancellationToken), New.StartAsync(cancellationToken), playwright);
        _playwright = await playwright;
        Browser = await _playwright.Chromium.LaunchAsync(new() { Headless = !Options.Headed });

        await LoadSeedAsync(cancellationToken);
    }

    /// <summary>Puts both databases back to the seed backup (for scenarios that need untouched data).</summary>
    public Task LoadSeedAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(Apps.Select(app => app.RestoreAsync(Options.SeedBackup, cancellationToken)));

    internal Task<BrowserSession> OpenSessionAsync(IAppUnderTest app, ViewportSize? viewport) =>
        BrowserSession.OpenAsync(Browser, app, Options.ArtifactsDirectory, viewport);

    public async ValueTask DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.DisposeAsync();
        }

        _playwright?.Dispose();
        var errors = new List<Exception>();
        foreach (var app in new IAsyncDisposable?[] { New, Legacy })
        {
            try
            {
                if (app is not null)
                {
                    await app.DisposeAsync();
                }
            }
#pragma warning disable CA1031 // stop every app even if one fails to stop
            catch (Exception ex)
#pragma warning restore CA1031
            {
                errors.Add(ex);
            }
        }

        if (errors.Count > 0)
        {
            throw new AggregateException("stopping the E2E apps failed", errors);
        }
    }
}

[CollectionDefinition(Name)]
public sealed class E2ECollectionDefinition : ICollectionFixture<E2EHarness>
{
    public const string Name = "E2E";
}
