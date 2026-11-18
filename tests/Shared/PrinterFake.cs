using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Dib4t.Tests.Common;

/// <summary>
/// A fake label printer (PLAN.md §5.1, §11 item 4): a TCP listener on <c>127.0.0.1</c> that records the bytes of each
/// connection until the client closes it, and never answers (like the real printers). Tests never reach a real one.
/// </summary>
public sealed class PrinterFake : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<byte[]> _received = new();
    private readonly SemaphoreSlim _arrived = new(0);
    private readonly Task _accepting;
    private bool _disposed;

    public PrinterFake()
    {
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>When set, connections are accepted but never read (the client's send must still finish).</summary>
    public bool Stalled { get; set; }

    /// <summary>The payloads received so far, in arrival order.</summary>
    public IReadOnlyList<byte[]> Received => [.. _received];

    /// <summary>Waits until <paramref name="count"/> connections have been closed by the client (at most 10 s).</summary>
    public async Task<IReadOnlyList<byte[]>> WaitForAsync(int count)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (_received.Count < count)
        {
            await _arrived.WaitAsync(timeout.Token);
        }

        return Received;
    }

    public void Clear() => _received.Clear();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _accepting;
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
        _arrived.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => ReadAsync(client));
        }
    }

    private async Task ReadAsync(TcpClient client)
    {
        using (client)
        {
            if (Stalled)
            {
                await Task.Delay(Timeout.Infinite, _stop.Token).ContinueWith(_ => { }, TaskScheduler.Default);
                return;
            }

            using var bytes = new MemoryStream();
            try
            {
                await client.GetStream().CopyToAsync(bytes, _stop.Token);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException)
            {
                return;
            }

            _received.Enqueue(bytes.ToArray());
            _arrived.Release();
        }
    }
}
