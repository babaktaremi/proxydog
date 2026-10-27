using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ProxyDog.Core;

namespace ProxyDog.Engine;

/// <summary>
/// Local listener the injected hook redirects connect() calls to. For each connection it dials the
/// user's upstream proxy, performs the SOCKS5/HTTP handshake for the original destination, and relays bytes.
/// </summary>
public sealed class ProxyEngine
{
    readonly ProxyRule rule;
    readonly CancellationTokenSource cts = new();
    readonly ConcurrentDictionary<TcpClient, byte> active = new();
    TcpListener? listener;
    Task? acceptLoop;

    public ProxyEngine(ProxyRule rule) => this.rule = rule.Clone();

    public int Port { get; private set; }

    public event Action<ConnectionEvent>? ConnectionEvent;

    /// <summary>The upstream proxy could not be reached or dropped during the handshake (not: the target refused).</summary>
    public event Action<string>? UpstreamFailure;

    /// <summary>The local listener died unexpectedly.</summary>
    public event Action<string>? Faulted;

    /// <summary>Starts listening on loopback. Tries <paramref name="preferredPort"/> first so a still-running app can reconnect.</summary>
    public void Start(int preferredPort = 0)
    {
        try
        {
            listener = new TcpListener(IPAddress.Loopback, preferredPort);
            listener.Start();
        }
        catch (SocketException) when (preferredPort != 0)
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
        }
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public async Task StopAsync()
    {
        cts.Cancel();
        listener?.Stop();
        foreach (var c in active.Keys)
            c.Dispose();
        if (acceptLoop is not null)
            await acceptLoop.ConfigureAwait(false);
    }

    async Task AcceptLoopAsync()
    {
        while (!cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener!.AcceptTcpClientAsync(cts.Token);
            }
            catch (Exception) when (cts.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException ex)
            {
                Faulted?.Invoke($"the local forwarder stopped ({ex.SocketErrorCode})");
                return;
            }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    void Raise(ConnectionEvent e) => ConnectionEvent?.Invoke(e);

    async Task HandleAsync(TcpClient client)
    {
        active[client] = 0;
        var id = Guid.NewGuid();
        var label = "unknown destination";
        var opened = false;
        var dialing = false;
        TcpClient? upstream = null;
        Func<bool, Task>? reply = null;
        try
        {
            client.NoDelay = true;
            var local = client.GetStream();

            Destination dest;
            using (var hs = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
            {
                hs.CancelAfter(TimeSpan.FromSeconds(10));
                // The hook starts with 'P' ("PDG1"); a SOCKS5 client (Chromium's --proxy-server) starts with 5.
                var first = new byte[1];
                await local.ReadExactlyAsync(first, hs.Token);
                if (first[0] == 5)
                {
                    (dest, reply) = await InboundSocks5.AcceptAsync(local, hs.Token);
                }
                else
                {
                    dest = await HookProtocol.ReadAsync(local, first[0], hs.Token);
                    var stream = local;
                    reply = ok => stream.WriteAsync(new byte[] { (byte)(ok ? 0 : 1) }).AsTask();
                }
            }
            label = dest.ToString();
            opened = true;
            Raise(new ConnectionEvent(id, ConnectionEventKind.Opened, DateTimeOffset.Now, label));

            dialing = true;
            upstream = new TcpClient { NoDelay = true };
            active[upstream] = 0;
            Stream remote;
            using (var hs = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
            {
                hs.CancelAfter(TimeSpan.FromSeconds(20));
                await upstream.ConnectAsync(rule.Host, rule.Port, hs.Token);
                remote = upstream.GetStream();
                if (rule.Kind == ProxyKind.Socks5)
                    await Socks5Client.ConnectAsync(remote, dest, rule.Username, rule.Password, hs.Token);
                else
                    await HttpConnectClient.ConnectAsync(remote, dest, rule.Username, rule.Password, hs.Token);
            }

            dialing = false;
            await reply!(true);
            Raise(new ConnectionEvent(id, ConnectionEventKind.Connected, DateTimeOffset.Now, label));

            long up = 0, down = 0;
            var t1 = PumpAsync(local, remote, upstream, n => Interlocked.Add(ref up, n));
            var t2 = PumpAsync(remote, local, client, n => Interlocked.Add(ref down, n));
            await Task.WhenAll(t1, t2);
            Raise(new ConnectionEvent(id, ConnectionEventKind.Closed, DateTimeOffset.Now, label, up, down));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try { if (reply is not null) await reply(false); } catch { /* client already gone */ }
            if (dialing && !cts.IsCancellationRequested
                && ex is SocketException or EndOfStreamException or OperationCanceledException)
                UpstreamFailure?.Invoke(Explain(ex, rule));
            if (opened && !cts.IsCancellationRequested)
                Raise(new ConnectionEvent(id, ConnectionEventKind.Failed, DateTimeOffset.Now, label, Error: Explain(ex, rule)));
        }
        finally
        {
            active.TryRemove(client, out _);
            client.Dispose();
            if (upstream is not null)
            {
                active.TryRemove(upstream, out _);
                upstream.Dispose();
            }
        }
    }

    static string Explain(Exception ex, ProxyRule rule) => ex switch
    {
        OperationCanceledException => $"Timed out waiting for {rule.Host}:{rule.Port}",
        SocketException s => $"Cannot reach proxy {rule.Host}:{rule.Port} ({s.SocketErrorCode})",
        EndOfStreamException => "Proxy closed the connection during the handshake",
        _ => ex.Message,
    };

    static async Task PumpAsync(Stream from, Stream to, TcpClient toSocket, Action<int> count)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var n = await from.ReadAsync(buffer);
                if (n == 0) break;
                await to.WriteAsync(buffer.AsMemory(0, n));
                count(n);
            }
            toSocket.Client.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            // Peer reset or we are shutting down: treat as end of stream.
            try { toSocket.Client.Shutdown(SocketShutdown.Both); } catch { /* already closed */ }
        }
    }
}
