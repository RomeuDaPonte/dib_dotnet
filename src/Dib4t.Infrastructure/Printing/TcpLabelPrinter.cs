using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Dib4t.Application.Printing;
using Dib4t.Domain.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dib4t.Infrastructure.Printing;

/// <summary>
/// A label printer over raw TCP (legacy <c>PrinterSettings.print</c> with <c>telnet-client</c> 1.4.9, PLAN.md §5 "Wire
/// behavior"): connect (1.5 s), write exactly <c>UTF8(payload + "\n")</c> (the client's output record separator),
/// close (FIN). Nothing is written before the payload and no reply is awaited (legacy waits 2 s for a reply the
/// printer never sends, then logs "response not received"; same bytes, same visible result, without the wait).
/// Every step is logged in the category <c>Printer</c> (§3.3); failures are logged and reported as <c>false</c>.
/// </summary>
internal sealed partial class TcpLabelPrinter(IOptions<LabelPrinterOptions> options, ILogger<TcpLabelPrinter> logger) : ILabelPrinter
{
    /// <summary>telnet-client's default <c>ors</c>.</summary>
    public const string RecordSeparator = "\n";

    public bool IsEnabled => true;

    /// <summary>The bytes on the wire for <paramref name="payload"/>.</summary>
    public static byte[] WireBytes(string payload) => Encoding.UTF8.GetBytes(payload + RecordSeparator);

    public async Task<bool> PrintAsync(PrinterSettings printer, string payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(printer);
        ArgumentNullException.ThrowIfNull(payload);
        var settings = options.Value;
        if (!TryEndpoint(printer, out var host, out var port))
        {
            LogInvalidPrinter(printer.Id, printer.Ip, printer.Port, printer.TemplateName);
            return false;
        }

        var bytes = WireBytes(payload);
        var started = Stopwatch.StartNew();
        using var client = new TcpClient();
        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connect.CancelAfter(settings.ConnectTimeoutMilliseconds);
                await client.ConnectAsync(host, port, connect.Token);
            }

            var connectedMs = started.ElapsedMilliseconds;
            using (var send = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                send.CancelAfter(settings.SendTimeoutMilliseconds);
                var stream = client.GetStream();
                await stream.WriteAsync(bytes, send.Token);
                await stream.FlushAsync(send.Token);
                client.Client.Shutdown(SocketShutdown.Send);
            }

            LogPrinted(host, port, printer.TemplateName, bytes.Length, connectedMs, started.ElapsedMilliseconds);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogTimedOut(host, port, printer.TemplateName, started.ElapsedMilliseconds);
            return false;
        }
        catch (Exception exception) when (exception is SocketException or IOException or ObjectDisposedException)
        {
            LogFailed(exception, host, port, printer.TemplateName, started.ElapsedMilliseconds, exception.Message);
            return false;
        }
    }

    /// <summary>
    /// The address and port as Node's <c>net.connect</c> takes them: an IP address (a CIDR suffix, which the
    /// settings form allows, cannot be connected to) and an integer port 0–65535.
    /// </summary>
    internal static bool TryEndpoint(PrinterSettings printer, out string host, out int port)
    {
        host = printer.Ip ?? string.Empty;
        port = 0;
        if (!IPAddress.TryParse(host, out _) || printer.Port is not { } value || value != Math.Floor(value) || value is < 0 or > 65535)
        {
            return false;
        }

        port = (int)value;
        return true;
    }

    [LoggerMessage(EventId = 160, Level = LogLevel.Information,
        Message = "Printer {Host}:{Port}: label {Template} sent, {Bytes} bytes (connected in {ConnectMs} ms, closed after {ElapsedMs} ms)")]
    private partial void LogPrinted(string host, int port, string? template, int bytes, long connectMs, long elapsedMs);

    [LoggerMessage(EventId = 161, Level = LogLevel.Error, Message = "Printer {Host}:{Port}: label {Template} not sent, timed out after {ElapsedMs} ms")]
    private partial void LogTimedOut(string host, int port, string? template, long elapsedMs);

    [LoggerMessage(EventId = 162, Level = LogLevel.Error, Message = "Printer {Host}:{Port}: label {Template} not sent after {ElapsedMs} ms: {Reason}")]
    private partial void LogFailed(Exception exception, string host, int port, string? template, long elapsedMs, string reason);

    [LoggerMessage(EventId = 163, Level = LogLevel.Error, Message = "Printer {PrinterId} ({Ip}:{Port}, {Template}): not an address and port to connect to")]
    private partial void LogInvalidPrinter(string printerId, string? ip, double? port, string? template);
}

/// <summary>
/// The printers when they are off (not Production, or <c>LabelPrinter:Enabled=false</c>; PLAN.md §4.7): logs each label
/// it would send, with the payload's text, and reports success. Never opens a connection.
/// </summary>
internal sealed partial class LoggingLabelPrinter(string reason, ILogger<LoggingLabelPrinter> logger) : ILabelPrinter
{
    public bool IsEnabled => false;

    public Task<bool> PrintAsync(PrinterSettings printer, string payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(printer);
        ArgumentNullException.ThrowIfNull(payload);
        var port = printer.Port is { } value ? value.ToString(CultureInfo.InvariantCulture) : null;
        var bytes = TcpLabelPrinter.WireBytes(payload).Length;
        var visible = Visible(payload);
        LogWouldPrint(reason, printer.Ip, port, printer.TemplateName, bytes, visible);
        return Task.FromResult(true);
    }

    /// <summary>The payload with its control characters written out (<c>\u001b</c>, <c>\n</c>), for the log.</summary>
    internal static string Visible(string payload) =>
        string.Concat(payload.Select(c => c switch
        {
            '\n' => "\\n",
            '\r' => "\\r",
            < ' ' => $"\\u{(int)c:x4}",
            _ => c.ToString(),
        }));

    [LoggerMessage(EventId = 164, Level = LogLevel.Information,
        Message = "Printer off ({Reason}), would send to {Ip}:{Port} label {Template}, {Bytes} bytes: {Payload}")]
    private partial void LogWouldPrint(string reason, string? ip, string? port, string? template, int bytes, string payload);
}
