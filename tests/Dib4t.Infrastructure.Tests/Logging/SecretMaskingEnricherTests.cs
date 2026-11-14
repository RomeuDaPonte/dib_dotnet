using System.Globalization;
using Dib4t.Infrastructure.Logging;
using Dib4t.Tests.Common.Logging;
using Serilog;
using Serilog.Events;

namespace Dib4t.Infrastructure.Tests.Logging;

/// <summary>No password, hash, token, key or secret reaches a sink (PLAN.md §3.3).</summary>
public sealed class SecretMaskingEnricherTests
{
    private const string Secret = "S3cr3t-value-7f1c";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static LogEvent Log(Action<ILogger> write)
    {
        var sink = new CollectingSink();
        using (var logger = new LoggerConfiguration().Enrich.With<SecretMaskingEnricher>().WriteTo.Sink(sink).CreateLogger())
        {
            write(logger);
        }

        return sink.Events.Should().ContainSingle().Subject;
    }

    [Theory]
    [InlineData("Password")]
    [InlineData("newPassword")]
    [InlineData("Token")]
    [InlineData("x-auth-token")]
    [InlineData("JwtKey")]
    [InlineData("ClientSecret")]
    public void Secret_named_properties_are_masked_in_properties_and_message(string name)
    {
        var logEvent = Log(logger => logger.Information("Login {Email} with {" + name.Replace("-", "_", StringComparison.Ordinal) + "}", "ana@dib4t.local", Secret));

        logEvent.RenderMessage(CultureInfo.InvariantCulture).Should().Be("Login \"ana@dib4t.local\" with \"***\"");
        logEvent.Properties["Email"].ToString().Should().Be("\"ana@dib4t.local\"");
    }

    [Fact]
    public void Secrets_inside_objects_dictionaries_and_lists_are_masked()
    {
        var logEvent = Log(logger => logger.Information("User {@User} {@Headers} {@Users}",
            new { Email = "ana@dib4t.local", Password = Secret, Session = new { Token = Secret, Id = 7 } },
            new Dictionary<string, string> { ["x-auth-token"] = Secret, ["accept"] = "json" },
            new[] { new { Name = "a", ApiKey = Secret } }));

        var rendered = logEvent.RenderMessage(CultureInfo.InvariantCulture);
        rendered.Should().NotContain(Secret);
        rendered.Should().Contain("ana@dib4t.local").And.Contain("Id: 7").And.Contain("\"json\"").And.Contain("Name: \"a\"");
    }

    [Fact]
    public void Events_without_secrets_are_left_unchanged()
    {
        var logEvent = Log(logger => logger.Information("Budget {Number} by {@User}", "26-0001", new { Email = "ana@dib4t.local" }));

        logEvent.RenderMessage(CultureInfo.InvariantCulture).Should().Be("Budget \"26-0001\" by { Email: \"ana@dib4t.local\" }");
    }

    [Fact]
    public async Task Secrets_never_reach_the_log_store_file()
    {
        var path = LogStoreFile.NewTempPath();
        try
        {
            var sink = new LogStoreSink(new SqliteLogStore(path), new LogsOptions(), TimeProvider.System);
            using (var logger = new LoggerConfiguration().Enrich.With<SecretMaskingEnricher>().WriteTo.Sink(sink).CreateLogger())
            {
                logger.Information("Login {Email} {Password}", "ana@dib4t.local", Secret);
                logger.Information("Settings {@Jwt}", new { Key = Secret, Issuer = "dib4t" });
            } // disposing the logger disposes the sink, which writes what is queued

            var rows = await LogStoreFile.ReadAsync(path, Cancellation);
            rows.Should().HaveCount(2);
            rows.Should().AllSatisfy(row => (row.Message + row.PropertiesJson).Should().NotContain(Secret).And.Contain("***"));
        }
        finally
        {
            LogStoreFile.Delete(path);
        }
    }
}
