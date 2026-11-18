using System.Text;
using Dib4t.Application.Printing;
using Dib4t.Domain.Settings;
using Dib4t.Infrastructure.Logging;
using Dib4t.Infrastructure.Printing;
using Dib4t.Infrastructure.Tests.Nas;
using Dib4t.SharedKernel;
using Dib4t.Tests.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dib4t.Infrastructure.Tests.Printing;

/// <summary>
/// The TCP label printer against an in-test listener (PLAN.md §5 "Wire behavior", §5.1; never a real printer): exactly
/// <c>UTF8(payload + "\n")</c> and a normal close, nothing before; no reply awaited; failures (refused, unreachable,
/// stalled, an address that is not one) logged in the category <c>Printer</c> and reported as <c>false</c>; the
/// adapter per environment; the no-op adapter's log line.
/// </summary>
public sealed class LabelPrinterTests : IAsyncDisposable
{
    private const string Producao = "\n      \u001bia3,^TS00126-0007,15/3/2026,Máquina de corte,Metalúrgica Exemplo, Lda.,26-0007,\n      ";

    private readonly PrinterFake _printer = new();
    private readonly ListLogger<TcpLabelPrinter> _logger = new();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _printer.DisposeAsync();

    [Fact]
    public async Task The_wire_carries_exactly_the_utf8_payload_and_a_line_feed()
    {
        var printed = await Adapter().PrintAsync(Printer(_printer.Port), Producao, Cancellation);

        printed.Should().BeTrue();
        var received = (await _printer.WaitForAsync(1)).Single();
        Convert.ToHexStringLower(received).Should().Be(
            "0a2020202020201b6961332c5e545330303132362d303030372c31352f332f323032362c4dc3a17175696e6120646520636f7274652c"
            + "4d6574616cc3ba7267696361204578656d706c6f2c204c64612e2c32362d303030372c0a2020202020200a",
            "the wireHex of job-golden.json (labels[0])");
        _logger.Entries.Should().ContainSingle().Which.Message.Should().StartWith($"Printer 127.0.0.1:{_printer.Port}: label producao sent, {received.Length} bytes");
        LogCategoryMap.Resolve(typeof(TcpLabelPrinter).FullName).Should().Be(LogCategories.Printer);
        LogCategoryMap.Resolve(typeof(LoggingLabelPrinter).FullName).Should().Be(LogCategories.Printer);
    }

    [Fact]
    public async Task A_printer_that_never_reads_or_answers_does_not_hold_the_request()
    {
        _printer.Stalled = true;
        var started = System.Diagnostics.Stopwatch.StartNew();

        var printed = await Adapter().PrintAsync(Printer(_printer.Port), Producao, Cancellation);

        printed.Should().BeTrue("the bytes fit the socket buffer; no reply is awaited (legacy waits 2 s and logs an error)");
        started.ElapsedMilliseconds.Should().BeLessThan(1500);
    }

    [Fact]
    public async Task A_refused_connection_is_logged_and_reported()
    {
        var port = _printer.Port;
        await _printer.DisposeAsync();

        var printed = await Adapter().PrintAsync(Printer(port), Producao, Cancellation);

        printed.Should().BeFalse();
        _logger.Entries.Should().ContainSingle().Which.Should().Match<ListLogger<TcpLabelPrinter>.Entry>(e => e.Level == LogLevel.Error && e.EventId == 162);
    }

    [Fact]
    public async Task An_unreachable_printer_times_out_after_the_connect_timeout()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();

        var printed = await Adapter(connectMs: 300).PrintAsync(Printer(9100, "192.0.2.1"), Producao, Cancellation); // TEST-NET-1: never answers

        printed.Should().BeFalse();
        started.ElapsedMilliseconds.Should().BeLessThan(3000);
        _logger.Entries.Should().ContainSingle(e => e.EventId == 161 || e.EventId == 162);
    }

    [Theory]
    [InlineData("192.168.2.32/24", 9100.0)]
    [InlineData("impressora", 9100.0)]
    [InlineData(null, 9100.0)]
    [InlineData("127.0.0.1", 9100.5)]
    [InlineData("127.0.0.1", 70000.0)]
    [InlineData("127.0.0.1", null)]
    public async Task Settings_that_are_not_an_address_and_port_are_logged_and_skipped(string? ip, double? port)
    {
        var printed = await Adapter().PrintAsync(new PrinterSettings("p", ip, port, "producao", 0), Producao, Cancellation);

        printed.Should().BeFalse();
        _logger.Entries.Should().ContainSingle().Which.EventId.Should().Be(163);
    }

    [Fact]
    public async Task The_no_op_printer_logs_the_label_and_sends_nothing()
    {
        var logger = new ListLogger<LoggingLabelPrinter>();
        var off = new LoggingLabelPrinter("environment Development", logger);

        var printed = await off.PrintAsync(Printer(_printer.Port), Producao, Cancellation);

        (printed, off.IsEnabled).Should().Be((true, false));
        logger.Entries.Should().ContainSingle().Which.Message.Should().Be(
            $"Printer off (environment Development), would send to 127.0.0.1:{_printer.Port} label producao, {Encoding.UTF8.GetByteCount(Producao + "\n")} bytes: "
            + "\\n      \\u001bia3,^TS00126-0007,15/3/2026,Máquina de corte,Metalúrgica Exemplo, Lda.,26-0007,\\n      ");
        await Task.Delay(200, Cancellation);
        _printer.Received.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Development", null, false)]
    [InlineData("Development", "true", false)]
    [InlineData("Staging", "true", false)]
    [InlineData("Production", null, true)]
    [InlineData("Production", "false", false)]
    public void Tcp_runs_only_in_production_unless_it_is_switched_off(string environment, string? enabled, bool tcp)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["LabelPrinter:Enabled"] = enabled,
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = environment });
        services.AddLabelPrinter(configuration);
        using var provider = services.BuildServiceProvider();

        var printer = provider.GetRequiredService<ILabelPrinter>();

        (printer.IsEnabled, printer.GetType()).Should().Be((tcp, tcp ? typeof(TcpLabelPrinter) : typeof(LoggingLabelPrinter)));
        provider.GetRequiredService<IOptions<LabelPrinterOptions>>().Value.ConnectTimeoutMilliseconds.Should().Be(1500);
    }

    private TcpLabelPrinter Adapter(int connectMs = 1500) =>
        new(Options.Create(new LabelPrinterOptions { ConnectTimeoutMilliseconds = connectMs }), _logger);

    private static PrinterSettings Printer(int port, string ip = "127.0.0.1") => new("p", ip, port, "producao", 0);
}
