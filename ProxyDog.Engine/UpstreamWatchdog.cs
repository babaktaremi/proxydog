using System.Net.Sockets;

namespace ProxyDog.Engine;

/// <summary>Polls the upstream proxy's port so a dead proxy is noticed even while the app is idle.</summary>
public sealed class UpstreamWatchdog(string host, int port, TimeSpan interval, int failuresToTrip = 2)
{
    CancellationTokenSource? cts;

    public event Action<string>? Tripped;

    public void Start()
    {
        cts = new CancellationTokenSource();
        var ct = cts.Token;
        _ = Task.Run(() => LoopAsync(ct));
    }

    public void Stop() => cts?.Cancel();

    async Task LoopAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { return; }

            try
            {
                using var client = new TcpClient();
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(TimeSpan.FromSeconds(3));
                await client.ConnectAsync(host, port, limit.Token);
                failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                if (++failures >= failuresToTrip)
                {
                    Tripped?.Invoke($"the proxy {host}:{port} is not reachable");
                    return;
                }
            }
        }
    }
}
