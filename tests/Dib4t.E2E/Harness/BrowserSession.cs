using Microsoft.Playwright;

namespace Dib4t.E2E.Harness;

/// <summary>
/// One browser context + page on one app, opened through <see cref="E2ETest.OpenAsync"/> and disposed after the
/// test. Records a Playwright trace that is kept only when the test fails
/// (<c>TestResults/e2e-artifacts/&lt;test&gt;-&lt;app&gt;.zip</c>; view with <c>playwright show-trace</c>), and collects
/// console errors and uncaught page errors so a scenario can assert there were none.
/// </summary>
public sealed class BrowserSession : IAsyncDisposable
{
    private readonly List<string> _errors = [];
    private readonly string _artifactsDirectory;

    private BrowserSession(IBrowserContext context, IPage page, IAppUnderTest app, string artifactsDirectory)
    {
        Context = context;
        Page = page;
        App = app;
        _artifactsDirectory = artifactsDirectory;
        page.Console += (_, message) => { if (message.Type == "error") { lock (_errors) { _errors.Add("console: " + message.Text); } } };
        page.PageError += (_, error) => { lock (_errors) { _errors.Add("page error: " + error); } };
    }

    public IBrowserContext Context { get; }

    public IPage Page { get; }

    public IAppUnderTest App { get; }

    /// <summary>Console errors and uncaught exceptions seen so far.</summary>
    public IReadOnlyList<string> Errors { get { lock (_errors) { return [.. _errors]; } } }

    internal static async Task<BrowserSession> OpenAsync(IBrowser browser, IAppUnderTest app, string artifactsDirectory, ViewportSize? viewport)
    {
        var context = await browser.NewContextAsync(new()
        {
            BaseURL = app.UiBaseUrl.ToString(),
            ViewportSize = viewport ?? new ViewportSize { Width = 1366, Height = 768 },
            Locale = "pt-PT",
            TimezoneId = "Europe/Lisbon",
        });
        context.SetDefaultTimeout(15_000);
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = false });
        var page = await context.NewPageAsync();
        return new BrowserSession(context, page, app, artifactsDirectory);
    }

    public async ValueTask DisposeAsync()
    {
        var test = TestContext.Current;
        var failed = test.TestState?.Result is TestResult.Failed; // set once the test has run (class cleanup)
        string? path = null;
        if (failed)
        {
            Directory.CreateDirectory(_artifactsDirectory);
            var name = string.Concat((test.Test?.TestDisplayName ?? "test").Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            path = Path.Combine(_artifactsDirectory, $"{name[..Math.Min(name.Length, 120)]}-{App.Name}.zip");
        }

        await Context.Tracing.StopAsync(new() { Path = path });
        await Context.DisposeAsync();
    }
}
