using System.Net;
using System.Net.Sockets;

namespace Cameo.Tests;

/// <summary>A controlled peer resets accepted connections. A hard-coded unused port
/// is not deterministic: firewalls and WSL may drop it instead of refusing it.</summary>
internal sealed class ResetServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accept;
    public string Url { get; }

    public ResetServer()
    {
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
        _accept = Accept();
    }

    private async Task Accept()
    {
        try
        {
            while (true)
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                client.Client.LingerState = new LingerOption(true, 0);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _accept; }
        finally { _stop.Dispose(); }
    }
}
