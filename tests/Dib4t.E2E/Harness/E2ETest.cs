using Microsoft.Playwright;

namespace Dib4t.E2E.Harness;

/// <summary>
/// Base class of every E2E test: shares the <see cref="E2EHarness"/> (started once per run) and disposes the
/// browser sessions after the test, when xUnit knows the result (traces of failed tests are kept).
/// </summary>
[Collection(E2ECollectionDefinition.Name)]
public abstract class E2ETest(E2EHarness harness) : IAsyncLifetime
{
    private readonly List<BrowserSession> _sessions = [];

    protected E2EHarness Harness { get; } = harness;

    protected static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A fresh browser context and page on <paramref name="app"/> (default viewport 1366×768).</summary>
    protected async Task<BrowserSession> OpenAsync(IAppUnderTest app, ViewportSize? viewport = null)
    {
        var session = await Harness.OpenSessionAsync(app, viewport);
        _sessions.Add(session);
        return session;
    }

    public virtual ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public virtual async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions)
        {
            await session.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}
