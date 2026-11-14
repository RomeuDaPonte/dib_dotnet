using Dib4t.Infrastructure.Logging;
using Dib4t.Tests.Common;
using Dib4t.Tests.Common.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Dib4t.Server.Tests.Logging;

/// <summary>The app on mongo:9.0.8 with the test-only endpoints of <see cref="LoggingTestController"/>.</summary>
public sealed class LoggingApp : AppWithMongo
{
    /// <summary>Rows of the store once <paramref name="predicate"/> holds (the request entry is written after the response).</summary>
    public Task<IReadOnlyList<LogRow>> RowsAsync(Func<IReadOnlyList<LogRow>, bool> predicate) =>
        LogStoreFile.WaitForAsync(
            LogStorePath,
            predicate,
            () => Factory.Services.GetRequiredService<LogStoreSink>().FlushAsync(TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services => services.AddControllers().AddApplicationPart(typeof(LoggingTestController).Assembly));
}
