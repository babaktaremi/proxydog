using System.Net;
using System.Net.Sockets;
using System.Text;
using ProxyDog.Core;
using ProxyDog.Engine;

namespace ProxyDog.Tests;

/// <summary>A tiny in-process upstream proxy plus an echo target, so the whole path runs without the network.</summary>
sealed class FakeUpstream : IDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly TcpListener echo = new(IPAddress.Loopback, 0);
    readonly ProxyKind kind;
    readonly string? user, pass;

    public int ProxyPort => ((IPEndPoint)listener.LocalEndpoint).Port;
    public int EchoPort => ((IPEndPoint)echo.LocalEndpoint).Port;
    public string? RequestedTarget { get; private set; }

    public FakeUpstream(ProxyKind kind, string? user = null, string? pass = null)
    {
        this.kind = kind;
        this.user = user;
        this.pass = pass;
        listener.Start();
        echo.Start();
        _ = Task.Run(EchoLoop);
        _ = Task.Run(ProxyLoop);
    }

    async Task EchoLoop()
    {
        while (true)
        {
            var c = await echo.AcceptTcpClientAsync();
            _ = Task.Run(async () =>
            {
                using var _ = c;
                await c.GetStream().CopyToAsync(c.GetStream());
            });
        }
    }

    async Task ProxyLoop()
    {
        while (true)
        {
            var c = await listener.AcceptTcpClientAsync();
            _ = Task.Run(() => Serve(c));
        }
    }

    async Task Serve(TcpClient c)
    {
        using var client = c;
        var s = client.GetStream();
        var ok = kind == ProxyKind.Socks5 ? await Socks(s) : await Http(s);
        if (!ok) return;

        using var target = new TcpClient();
        await target.ConnectAsync(IPAddress.Loopback, EchoPort);
        var t = target.GetStream();
        await Task.WhenAny(s.CopyToAsync(t), t.CopyToAsync(s));
    }

    async Task<bool> Socks(NetworkStream s)
    {
        var hello = new byte[2];
        await s.ReadExactlyAsync(hello);
        await s.ReadExactlyAsync(new byte[hello[1]]);
        if (user is null)
        {
            await s.WriteAsync(new byte[] { 5, 0 });
        }
        else
        {
            await s.WriteAsync(new byte[] { 5, 2 });
            var v = new byte[2];
            await s.ReadExactlyAsync(v);
            var u = new byte[v[1]];
            await s.ReadExactlyAsync(u);
            var pl = new byte[1];
            await s.ReadExactlyAsync(pl);
            var p = new byte[pl[0]];
            await s.ReadExactlyAsync(p);
            var good = Encoding.UTF8.GetString(u) == user && Encoding.UTF8.GetString(p) == pass;
            await s.WriteAsync(new byte[] { 1, (byte)(good ? 0 : 1) });
            if (!good) return false;
        }

        var req = new byte[4];
        await s.ReadExactlyAsync(req);
        string host;
        if (req[3] == 3)
        {
            var len = new byte[1];
            await s.ReadExactlyAsync(len);
            var name = new byte[len[0]];
            await s.ReadExactlyAsync(name);
            host = Encoding.ASCII.GetString(name);
        }
        else
        {
            var addr = new byte[req[3] == 1 ? 4 : 16];
            await s.ReadExactlyAsync(addr);
            host = new IPAddress(addr).ToString();
        }
        var port = new byte[2];
        await s.ReadExactlyAsync(port);
        RequestedTarget = $"{host}:{(port[0] << 8) | port[1]}";
        await s.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
        return true;
    }

    async Task<bool> Http(NetworkStream s)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (!sb.ToString().EndsWith("\r\n\r\n"))
        {
            await s.ReadExactlyAsync(one);
            sb.Append((char)one[0]);
        }
        var text = sb.ToString();
        RequestedTarget = text.Split(' ')[1];
        if (user is not null)
        {
            var expect = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
            if (!text.Contains($"Proxy-Authorization: Basic {expect}"))
            {
                await s.WriteAsync("HTTP/1.1 407 Proxy Authentication Required\r\n\r\n"u8.ToArray());
                return false;
            }
        }
        await s.WriteAsync("HTTP/1.1 200 Connection established\r\n\r\n"u8.ToArray());
        return true;
    }

    public void Dispose()
    {
        listener.Stop();
        echo.Stop();
    }
}

public class EngineTests
{
    static ProxyRule RuleFor(FakeUpstream up, ProxyKind kind, string? user = null, string? pass = null) => new()
    {
        Kind = kind,
        Host = "127.0.0.1",
        Port = up.ProxyPort,
        Username = user,
        Password = pass,
    };

    /// <summary>Plays the part of the injected hook: connect to the engine, send the destination, read the ack.</summary>
    static async Task<(TcpClient Client, byte Ack)> ThroughEngine(ProxyEngine engine, Destination dest)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, engine.Port);
        var s = client.GetStream();
        await s.WriteAsync(HookProtocol.Encode(dest));
        var ack = new byte[1];
        await s.ReadExactlyAsync(ack);
        return (client, ack[0]);
    }

    [Theory]
    [InlineData(ProxyKind.Socks5)]
    [InlineData(ProxyKind.Http)]
    public async Task Tunnels_data_and_reports_bytes(ProxyKind kind)
    {
        using var up = new FakeUpstream(kind);
        var engine = new ProxyEngine(RuleFor(up, kind));
        var events = new List<ConnectionEvent>();
        engine.ConnectionEvent += e => { lock (events) events.Add(e); };
        engine.Start();

        var dest = new Destination(IPAddress.Parse("93.184.216.34"), 443);
        var (client, ack) = await ThroughEngine(engine, dest);
        Assert.Equal(0, ack);

        var s = client.GetStream();
        await s.WriteAsync("hello"u8.ToArray());
        var back = new byte[5];
        await s.ReadExactlyAsync(back);
        Assert.Equal("hello", Encoding.ASCII.GetString(back));
        Assert.Equal("93.184.216.34:443", up.RequestedTarget);

        client.Dispose();
        await WaitFor(() => { lock (events) return events.Any(e => e.Kind == ConnectionEventKind.Closed); });
        await engine.StopAsync();

        lock (events)
        {
            Assert.Equal(
                [ConnectionEventKind.Opened, ConnectionEventKind.Connected, ConnectionEventKind.Closed],
                events.Select(e => e.Kind));
            var closed = events.Last();
            Assert.Equal(5, closed.BytesUp);
            Assert.Equal(5, closed.BytesDown);
        }
    }

    [Theory]
    [InlineData(ProxyKind.Socks5)]
    [InlineData(ProxyKind.Http)]
    public async Task Sends_credentials(ProxyKind kind)
    {
        using var up = new FakeUpstream(kind, "dev", "s3cret");
        var engine = new ProxyEngine(RuleFor(up, kind, "dev", "s3cret"));
        engine.Start();
        var (client, ack) = await ThroughEngine(engine, new Destination(IPAddress.Parse("10.1.2.3"), 80));
        Assert.Equal(0, ack);
        client.Dispose();
        await engine.StopAsync();
    }

    [Theory]
    [InlineData(ProxyKind.Socks5)]
    [InlineData(ProxyKind.Http)]
    public async Task Wrong_credentials_fail_and_are_logged(ProxyKind kind)
    {
        using var up = new FakeUpstream(kind, "dev", "s3cret");
        var engine = new ProxyEngine(RuleFor(up, kind, "dev", "wrong"));
        var events = new List<ConnectionEvent>();
        engine.ConnectionEvent += e => { lock (events) events.Add(e); };
        engine.Start();

        var (client, ack) = await ThroughEngine(engine, new Destination(IPAddress.Parse("10.1.2.3"), 80));
        Assert.NotEqual(0, ack);
        client.Dispose();

        await WaitFor(() => { lock (events) return events.Any(e => e.Kind == ConnectionEventKind.Failed); });
        await engine.StopAsync();
        lock (events)
        {
            var error = events.First(e => e.Kind == ConnectionEventKind.Failed).Error!;
            Assert.True(
                error.Contains("password", StringComparison.OrdinalIgnoreCase)
                || error.Contains("credentials", StringComparison.OrdinalIgnoreCase),
                error);
        }
    }

    [Theory]
    [InlineData(ProxyKind.Socks5)]
    [InlineData(ProxyKind.Http)]
    public async Task Accepts_socks5_clients_with_host_names(ProxyKind kind)
    {
        using var up = new FakeUpstream(kind);
        var engine = new ProxyEngine(RuleFor(up, kind));
        var events = new List<ConnectionEvent>();
        engine.ConnectionEvent += e => { lock (events) events.Add(e); };
        engine.Start();

        // What Chromium does with --proxy-server=socks5://127.0.0.1:<port>.
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, engine.Port);
        var s = client.GetStream();
        await Socks5Client.ConnectAsync(s, Destination.ForDomain("example.com", 443), null, null, CancellationToken.None);
        await s.WriteAsync("hi"u8.ToArray());
        var back = new byte[2];
        await s.ReadExactlyAsync(back);

        Assert.Equal("hi", Encoding.ASCII.GetString(back));
        Assert.Equal("example.com:443", up.RequestedTarget);
        lock (events)
            Assert.Contains(events, e => e.Kind == ConnectionEventKind.Connected && e.Destination == "example.com:443");
        await engine.StopAsync();
    }

    [Fact]
    public async Task Unreachable_proxy_fails_closed()
    {
        var dead = new TcpListener(IPAddress.Loopback, 0);
        dead.Start();
        var port = ((IPEndPoint)dead.LocalEndpoint).Port;
        dead.Stop();

        var engine = new ProxyEngine(new ProxyRule { Kind = ProxyKind.Socks5, Host = "127.0.0.1", Port = port });
        engine.Start();
        var (client, ack) = await ThroughEngine(engine, new Destination(IPAddress.Parse("10.1.2.3"), 80));
        Assert.NotEqual(0, ack);
        client.Dispose();
        await engine.StopAsync();
    }

    [Fact]
    public async Task Stopped_engine_refuses_connections()
    {
        var engine = new ProxyEngine(new ProxyRule { Host = "127.0.0.1", Port = 1 });
        engine.Start();
        var port = engine.Port;
        await engine.StopAsync();
        await Assert.ThrowsAsync<SocketException>(async () =>
        {
            using var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, port);
        });
    }

    [Fact]
    public async Task Hook_protocol_round_trips_ipv4_and_ipv6()
    {
        foreach (var ip in new[] { "1.2.3.4", "2001:db8::1" })
        {
            var d = new Destination(IPAddress.Parse(ip), 8443);
            using var ms = new MemoryStream(HookProtocol.Encode(d));
            Assert.Equal(d, await HookProtocol.ReadAsync(ms, CancellationToken.None));
        }
    }

    static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);
        Assert.True(condition(), "Timed out waiting for condition.");
    }
}

public class RecentProxyTests
{
    [Fact]
    public void Keeps_newest_first_without_duplicates_and_caps_the_list()
    {
        var dir = Directory.CreateTempSubdirectory();
        var store = new RecentProxyStore(Path.Combine(dir.FullName, "r.json"), limit: 3);
        store.Add(new RecentProxy(ProxyKind.Socks5, "a.example", 1080, null, null));
        store.Add(new RecentProxy(ProxyKind.Http, "b.example", 8080, "u", "p"));
        store.Add(new RecentProxy(ProxyKind.Socks5, "A.example", 1080, null, null)); // same proxy, different case
        store.Add(new RecentProxy(ProxyKind.Socks5, "c.example", 1080, null, null));
        store.Add(new RecentProxy(ProxyKind.Socks5, "d.example", 1080, null, null));

        var list = store.Load();
        Assert.Equal(["d.example", "c.example", "A.example"], list.Select(p => p.Host));
        Assert.Equal("SOCKS5  d.example:1080", list[0].Label);
        dir.Delete(true);
    }

    [Fact]
    public void Round_trips_credentials_and_kind()
    {
        var dir = Directory.CreateTempSubdirectory();
        var store = new RecentProxyStore(Path.Combine(dir.FullName, "r.json"));
        store.Add(new RecentProxy(ProxyKind.Http, "h", 3128, "dev", "pw"));
        var p = Assert.Single(store.Load());
        Assert.Equal((ProxyKind.Http, "dev", "pw"), (p.Kind, p.Username, p.Password));
        dir.Delete(true);
    }
}

public class LauncherTests
{
    [Fact]
    public void Splits_quoted_arguments() =>
        Assert.Equal(["--name", "my app", "x"], CommandLine.Split("--name \"my app\" x"));

    [Fact]
    public void Parses_desktop_exec_and_drops_field_codes()
    {
        var dir = Directory.CreateTempSubdirectory();
        var file = Path.Combine(dir.FullName, "app.desktop");
        File.WriteAllText(file, "[Desktop Entry]\nName=App\nExec=/usr/bin/app --flag %U\n[Desktop Action x]\nExec=/bin/other\n");
        var (exe, args) = AppLauncher.ResolveTarget(file);
        Assert.Equal("/usr/bin/app", exe);
        Assert.Equal(["--flag"], args);
        dir.Delete(true);
    }

    [Fact]
    public void Resolves_app_bundle_executable()
    {
        var dir = Directory.CreateTempSubdirectory();
        var bundle = Path.Combine(dir.FullName, "Thing.app");
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(bundle, "Contents", "MacOS", "thing-bin"), "");
        File.WriteAllText(Path.Combine(bundle, "Contents", "Info.plist"),
            "<plist><dict><key>CFBundleExecutable</key>\n<string>thing-bin</string></dict></plist>");
        var (exe, _) = AppLauncher.ResolveTarget(bundle);
        Assert.EndsWith("Contents/MacOS/thing-bin", exe);
        dir.Delete(true);
    }

    [Fact]
    public void Start_info_carries_hook_and_port()
    {
        var dir = Directory.CreateTempSubdirectory();
        var exe = Path.Combine(dir.FullName, "prog");
        File.WriteAllText(exe, "");
        var psi = AppLauncher.BuildStartInfo(new ProxyRule { AppPath = exe, Arguments = "-a 'b c'" }, 4242, "/h/hook.so");
        Assert.Equal("4242", psi.Environment["PROXYDOG_PORT"]);
        Assert.Contains("/h/hook.so", psi.Environment[OperatingSystem.IsMacOS() ? "DYLD_INSERT_LIBRARIES" : "LD_PRELOAD"]);
        Assert.Equal(["-a", "b c"], psi.ArgumentList);
        dir.Delete(true);
    }

    [Fact]
    public void Rule_store_round_trips_and_survives_corruption()
    {
        var dir = Directory.CreateTempSubdirectory();
        var path = Path.Combine(dir.FullName, "sub", "rules.json");
        var store = new RuleStore(path);
        store.Save([new ProxyRule { Name = "n", Kind = ProxyKind.Http, Host = "h", Port = 8080, AppPath = "/a" }]);
        var loaded = Assert.Single(store.Load());
        Assert.Equal(ProxyKind.Http, loaded.Kind);
        Assert.Contains("\"Http\"", File.ReadAllText(path));

        File.WriteAllText(path, "{ not json");
        Assert.Empty(store.Load());
        Assert.True(File.Exists(path + ".corrupt"));
        dir.Delete(true);
    }
}

/// <summary>Runs a real program with the native hook injected and checks its connect() goes through the engine.</summary>
public class HookIntegrationTests
{
    const string ClientSource = """
        #include <arpa/inet.h>
        #include <netinet/in.h>
        #include <stdio.h>
        #include <string.h>
        #include <sys/socket.h>
        #include <unistd.h>
        int main(int argc, char **argv) {
            int fd = socket(AF_INET, SOCK_STREAM, 0);
            struct sockaddr_in a; memset(&a, 0, sizeof a);
            a.sin_family = AF_INET; a.sin_port = htons(atoi(argv[2]));
            inet_pton(AF_INET, argv[1], &a.sin_addr);
            if (connect(fd, (struct sockaddr *)&a, sizeof a) != 0) { perror("connect"); return 2; }
            write(fd, "ping", 4);
            char buf[16] = {0};
            int n = read(fd, buf, sizeof buf - 1);
            printf("%.*s", n > 0 ? n : 0, buf);
            return 0;
        }
        """;

    [Fact]
    public async Task Injected_program_connects_through_the_engine()
    {
        var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../ProxyDog.Hook/build.sh"));
        if (OperatingSystem.IsWindows() || !File.Exists(script))
            return;

        var work = Directory.CreateTempSubdirectory();
        try
        {
            await Run("sh", script, work.FullName);

            var src = Path.Combine(work.FullName, "client.c");
            var program = Path.Combine(work.FullName, "client");
            File.WriteAllText(src, "#include <stdlib.h>\n" + ClientSource);
            await Run("cc", src, "-o", program);

            using var up = new FakeUpstream(ProxyKind.Socks5);
            var engine = new ProxyEngine(new ProxyRule { Kind = ProxyKind.Socks5, Host = "127.0.0.1", Port = up.ProxyPort });
            var events = new List<ConnectionEvent>();
            engine.ConnectionEvent += e => { lock (events) events.Add(e); };
            engine.Start();

            var hook = Path.Combine(work.FullName, AppLauncher.HookFileName);
            var psi = AppLauncher.BuildStartInfo(
                new ProxyRule { AppPath = program, Arguments = "93.184.216.34 8081" }, engine.Port, hook);
            psi.RedirectStandardOutput = true;
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            await engine.StopAsync();

            // The fake upstream's echo target answers "ping", proving the bytes took the proxy path.
            Assert.Equal(0, p.ExitCode);
            Assert.Equal("ping", output);
            Assert.Equal("93.184.216.34:8081", up.RequestedTarget);
            lock (events)
                Assert.Contains(events, e => e.Kind == ConnectionEventKind.Connected && e.Destination == "93.184.216.34:8081");

            // With the engine gone, the hooked program must fail closed instead of going direct.
            var closed = System.Diagnostics.Process.Start(psi)!;
            await closed.WaitForExitAsync();
            Assert.NotEqual(0, closed.ExitCode);
        }
        finally
        {
            work.Delete(true);
        }
    }

    [Fact]
    public async Task With_the_rule_off_a_hooked_program_cannot_reach_the_network_directly()
    {
        if (OperatingSystem.IsWindows()) return;
        var lan = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                        && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        if (lan is null) return; // needs a non-loopback address to stand in for "the internet"

        var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../ProxyDog.Hook/build.sh"));
        var work = Directory.CreateTempSubdirectory();
        try
        {
            await Run("sh", script, work.FullName);
            var src = Path.Combine(work.FullName, "client.c");
            var program = Path.Combine(work.FullName, "client");
            File.WriteAllText(src, "#include <stdlib.h>\n" + ClientSource);
            await Run("cc", src, "-o", program);

            // The rule was on (engine started), then switched off (engine stopped) while the program lives on.
            var engine = new ProxyEngine(new ProxyRule { Kind = ProxyKind.Socks5, Host = "127.0.0.1", Port = 9 });
            engine.Start();
            await engine.StopAsync();

            var target = new TcpListener(lan, 0);
            target.Start();
            var reached = 0;
            _ = Task.Run(async () =>
            {
                try { while (true) { using var _ = await target.AcceptTcpClientAsync(); Interlocked.Increment(ref reached); } }
                catch { /* listener stopped */ }
            });

            var hook = Path.Combine(work.FullName, AppLauncher.HookFileName);
            var psi = AppLauncher.BuildStartInfo(
                new ProxyRule { AppPath = program, Arguments = $"{lan} {((IPEndPoint)target.LocalEndpoint).Port}" }, engine.Port, hook);
            using var p = System.Diagnostics.Process.Start(psi)!;
            await p.WaitForExitAsync();
            await Task.Delay(200);
            target.Stop();

            Assert.NotEqual(0, p.ExitCode);
            Assert.Equal(0, reached);
        }
        finally
        {
            work.Delete(true);
        }
    }

    // Network.framework and NSURLSession connect with connectx(), carrying the first bytes with the connection.
    const string ConnectxSource = """
        #include <arpa/inet.h>
        #include <netinet/in.h>
        #include <stdio.h>
        #include <stdlib.h>
        #include <string.h>
        #include <sys/socket.h>
        #include <sys/uio.h>
        #include <unistd.h>
        int main(int argc, char **argv) {
            int fd = socket(AF_INET, SOCK_STREAM, 0);
            struct sockaddr_in a; memset(&a, 0, sizeof a);
            a.sin_len = sizeof a; a.sin_family = AF_INET; a.sin_port = htons(atoi(argv[2]));
            inet_pton(AF_INET, argv[1], &a.sin_addr);
            sa_endpoints_t ep; memset(&ep, 0, sizeof ep);
            ep.sae_dstaddr = (struct sockaddr *)&a; ep.sae_dstaddrlen = sizeof a;
            struct iovec iov = { "ping", 4 };
            size_t sent = 0;
            if (connectx(fd, &ep, SAE_ASSOCID_ANY, 0, &iov, 1, &sent, NULL) != 0) { perror("connectx"); return 2; }
            if (sent < 4) write(fd, "ping" + sent, 4 - sent);
            char buf[16] = {0};
            int n = read(fd, buf, sizeof buf - 1);
            printf("%.*s", n > 0 ? n : 0, buf);
            return 0;
        }
        """;

    [Fact]
    public async Task Program_that_connects_with_connectx_goes_through_the_engine()
    {
        if (!OperatingSystem.IsMacOS()) return; // connectx is the macOS/BSD call
        var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../ProxyDog.Hook/build.sh"));
        var work = Directory.CreateTempSubdirectory();
        try
        {
            await Run("sh", script, work.FullName);
            var src = Path.Combine(work.FullName, "client.c");
            var program = Path.Combine(work.FullName, "client");
            File.WriteAllText(src, ConnectxSource);
            await Run("cc", src, "-o", program);

            using var up = new FakeUpstream(ProxyKind.Socks5);
            var engine = new ProxyEngine(new ProxyRule { Kind = ProxyKind.Socks5, Host = "127.0.0.1", Port = up.ProxyPort });
            var events = new List<ConnectionEvent>();
            engine.ConnectionEvent += e => { lock (events) events.Add(e); };
            engine.Start();

            var hook = Path.Combine(work.FullName, AppLauncher.HookFileName);
            var psi = AppLauncher.BuildStartInfo(
                new ProxyRule { AppPath = program, Arguments = "93.184.216.34 8081" }, engine.Port, hook);
            psi.RedirectStandardOutput = true;
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            await engine.StopAsync();

            Assert.Equal(0, p.ExitCode);
            Assert.Equal("ping", output); // the data sent with connectx arrived through the tunnel
            Assert.Equal("93.184.216.34:8081", up.RequestedTarget);
            lock (events)
                Assert.Contains(events, e => e.Kind == ConnectionEventKind.Connected && e.Destination == "93.184.216.34:8081");
        }
        finally
        {
            work.Delete(true);
        }
    }

    // NWConnection (and URLSession above it) uses a user-space network stack with no BSD socket, so only the
    // Network.framework proxy registration in the hook can catch it. Needs macOS 14+ and the Swift compiler.
    const string NetworkFrameworkSource = """
        import Foundation
        import Network
        let sem = DispatchSemaphore(value: 0)
        let c = NWConnection(host: "93.184.216.34", port: 8081, using: .tcp)
        c.stateUpdateHandler = { st in
            if case .ready = st { print("ready"); sem.signal() }
            if case .failed = st { print("failed"); sem.signal() }
        }
        c.start(queue: .global())
        _ = sem.wait(timeout: .now() + 10)
        """;

    [Fact]
    public async Task Network_framework_connections_go_through_the_engine()
    {
        if (!OperatingSystem.IsMacOS() || !OperatingSystem.IsMacOSVersionAtLeast(14) || !File.Exists("/usr/bin/swiftc"))
            return;
        var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../ProxyDog.Hook/build.sh"));
        var work = Directory.CreateTempSubdirectory();
        try
        {
            await Run("sh", script, work.FullName);
            var src = Path.Combine(work.FullName, "nw.swift");
            var program = Path.Combine(work.FullName, "nw");
            File.WriteAllText(src, NetworkFrameworkSource);
            await Run("swiftc", "-O", src, "-o", program);

            using var up = new FakeUpstream(ProxyKind.Socks5);
            var engine = new ProxyEngine(new ProxyRule { Kind = ProxyKind.Socks5, Host = "127.0.0.1", Port = up.ProxyPort });
            var events = new List<ConnectionEvent>();
            engine.ConnectionEvent += e => { lock (events) events.Add(e); };
            engine.Start();

            var hook = Path.Combine(work.FullName, AppLauncher.HookFileName);
            var psi = AppLauncher.BuildStartInfo(new ProxyRule { AppPath = program }, engine.Port, hook);
            psi.RedirectStandardOutput = true;
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            await engine.StopAsync();

            Assert.Equal("ready", output.Trim());
            Assert.Equal("93.184.216.34:8081", up.RequestedTarget);
            lock (events)
                Assert.Contains(events, e => e.Kind == ConnectionEventKind.Connected && e.Destination == "93.184.216.34:8081");
        }
        finally
        {
            work.Delete(true);
        }
    }

    static async Task Run(string file, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(file) { RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, $"{file} failed: {err}");
    }
}

public class LaunchPlanTests
{
    [Fact]
    public void Mac_bundles_open_through_launch_services_and_chromium_gets_proxy_flag()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var bundle = Path.Combine(dir.FullName, "Browser.app");
            Directory.CreateDirectory(Path.Combine(bundle, "Contents", "MacOS"));
            File.WriteAllText(Path.Combine(bundle, "Contents", "MacOS", "Browser"), "");
            var res = Path.Combine(bundle, "Contents", "Frameworks", "Browser Framework.framework", "Resources");
            Directory.CreateDirectory(res);
            File.WriteAllText(Path.Combine(res, "icudtl.dat"), "");

            var plan = AppLauncher.Plan(new ProxyRule { AppPath = bundle }, 5555, "/h/hook.dylib");
            Assert.True(plan.ViaLaunchServices);
            Assert.True(plan.ChromiumProxyFlag);
            Assert.Equal("/usr/bin/open", plan.StartInfo.FileName);
            var args = plan.StartInfo.ArgumentList.ToList();
            Assert.Contains("PROXYDOG_PORT=5555", args);
            Assert.Contains("--proxy-server=socks5://127.0.0.1:5555", args);
            Assert.True(args.IndexOf("--args") > args.IndexOf("--env"));

            var own = AppLauncher.Plan(new ProxyRule { AppPath = bundle, Arguments = "--proxy-server=x" }, 5555, "/h/hook.dylib");
            Assert.False(own.ChromiumProxyFlag);
        }
        finally
        {
            dir.Delete(true);
        }
    }
}

public class TelegramTests
{
    static string FakeBundle(string dir, string id)
    {
        var bundle = Path.Combine(dir, "Telegram.app");
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(bundle, "Contents", "MacOS", "Telegram"), "");
        File.WriteAllText(Path.Combine(bundle, "Contents", "Info.plist"),
            $"<plist><dict><key>CFBundleExecutable</key><string>Telegram</string>\n<key>CFBundleIdentifier</key>\n<string>{id}</string></dict></plist>");
        return bundle;
    }

    [Fact]
    public void Recognises_telegram_and_builds_the_proxy_link()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var tg = FakeBundle(dir.FullName, "ru.keepcoder.Telegram");
            Assert.True(AppLauncher.IsTelegram(tg));

            var plan = AppLauncher.Plan(new ProxyRule { AppPath = tg }, 4711, "/h/hook.dylib");
            Assert.True(plan.TelegramProxyLink);

            var link = AppLauncher.TelegramLinkStartInfo(tg, 4711).ArgumentList.ToList();
            Assert.Equal(["-a", tg, "tg://socks?server=127.0.0.1&port=4711"], link);

            var other = Directory.CreateDirectory(Path.Combine(dir.FullName, "x")).FullName;
            Assert.False(AppLauncher.IsTelegram(FakeBundle(other, "com.example.app")));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public async Task A_rule_keeps_the_same_local_port_between_runs()
    {
        if (OperatingSystem.IsWindows()) return;
        var (exe, dir) = await KillSwitchTests.PrepareAppPublicAsync();
        try
        {
            using var up = new FakeUpstream(ProxyKind.Socks5);
            var rule = new ProxyRule { Kind = ProxyKind.Socks5, Host = "127.0.0.1", Port = up.ProxyPort, AppPath = exe, KillSwitch = true };

            var first = new RuleSession(rule);
            first.Start();
            var port = rule.LocalPort;
            Assert.NotEqual(0, port);
            await first.StopAsync();

            var second = new RuleSession(rule);
            second.Start();
            Assert.Equal(port, rule.LocalPort);
            await second.StopAsync();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class RunningAppTests
{
    [Fact]
    public void Finds_and_quits_a_running_copy()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            // A private copy gives the test a unique executable path that nothing else is running.
            var exe = Path.Combine(dir.FullName, "napper");
            File.Copy(File.Exists("/bin/sleep") ? "/bin/sleep" : "/usr/bin/sleep", exe);
            File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("codesign", ["--force", "-s", "-", exe])!.WaitForExit();

            using var child = System.Diagnostics.Process.Start(exe, ["60"])!;
            var found = AppLauncher.FindRunning(Path.GetFullPath(exe));
            Assert.Contains(found, p => p.Id == child.Id);

            Assert.True(AppLauncher.Quit(found, TimeSpan.FromSeconds(5)));
            Assert.True(child.WaitForExit(2000));
            Assert.Empty(AppLauncher.FindRunning(Path.GetFullPath(exe)));
        }
        finally
        {
            dir.Delete(true);
        }
    }
}

public class KillSwitchTests
{
    [Fact]
    public void Terminate_closes_the_process_and_everything_it_started()
    {
        if (OperatingSystem.IsWindows()) return;
        using var root = System.Diagnostics.Process.Start("sh", ["-c", "sleep 60 & sleep 60 & wait"])!;
        var tree = SpinUntil(() => AppLauncher.WithDescendants([root.Id]).Count >= 3
            ? AppLauncher.WithDescendants([root.Id]) : null);
        Assert.True(tree.Count >= 3, "expected the shell plus two sleeps");

        AppLauncher.Terminate([root.Id], TimeSpan.FromSeconds(3));

        Assert.True(root.WaitForExit(3000));
        foreach (var pid in tree.Where(p => p != root.Id))
            Assert.True(SpinUntil(() => !Alive(pid) ? "gone" : null) is not null, $"child {pid} survived");
    }

    static bool Alive(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
    }

    static T SpinUntil<T>(Func<T?> get) where T : class
    {
        for (var i = 0; i < 50; i++)
        {
            if (get() is { } v) return v;
            Thread.Sleep(100);
        }
        return null!;
    }

    [Fact]
    public async Task Watchdog_trips_when_the_proxy_goes_away_but_not_while_it_is_up()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var dog = new UpstreamWatchdog("127.0.0.1", port, TimeSpan.FromMilliseconds(100));
        var tripped = new TaskCompletionSource<string>();
        dog.Tripped += tripped.SetResult;
        dog.Start();

        await Task.Delay(700);
        Assert.False(tripped.Task.IsCompleted, "tripped while the proxy was healthy");

        listener.Stop();
        var reason = await tripped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(port.ToString(), reason);
        dog.Stop();
    }

    [Fact]
    public async Task Engine_reports_an_unreachable_proxy_but_not_a_wrong_password()
    {
        var failures = new List<string>();

        // Wrong credentials: the proxy answers, so it is not "down".
        using (var up = new FakeUpstream(ProxyKind.Socks5, "dev", "s3cret"))
        {
            var engine = new ProxyEngine(new ProxyRule { Kind = ProxyKind.Socks5, Host = "127.0.0.1", Port = up.ProxyPort, Username = "dev", Password = "bad" });
            engine.UpstreamFailure += m => { lock (failures) failures.Add(m); };
            engine.Start();
            using var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, engine.Port);
            await c.GetStream().WriteAsync(HookProtocol.Encode(new Destination(IPAddress.Parse("10.0.0.1"), 80)));
            await c.GetStream().ReadExactlyAsync(new byte[1]);
            await Task.Delay(200);
            await engine.StopAsync();
        }
        lock (failures) Assert.Empty(failures);

        // Nothing listening: that is a down proxy.
        var dead = new TcpListener(IPAddress.Loopback, 0);
        dead.Start();
        var deadPort = ((IPEndPoint)dead.LocalEndpoint).Port;
        dead.Stop();
        var e2 = new ProxyEngine(new ProxyRule { Kind = ProxyKind.Socks5, Host = "127.0.0.1", Port = deadPort });
        e2.UpstreamFailure += m => { lock (failures) failures.Add(m); };
        e2.Start();
        using (var c = new TcpClient())
        {
            await c.ConnectAsync(IPAddress.Loopback, e2.Port);
            await c.GetStream().WriteAsync(HookProtocol.Encode(new Destination(IPAddress.Parse("10.0.0.1"), 80)));
            await c.GetStream().ReadExactlyAsync(new byte[1]);
        }
        await Task.Delay(200);
        await e2.StopAsync();
        lock (failures) Assert.Single(failures);
    }

    // A real session with a real process standing in for the application.
    public static Task<(string Exe, string Dir)> PrepareAppPublicAsync() => PrepareAppAsync();

    // Test classes run in parallel and all want the hook in the same folder, so build it once, under a lock.
    static readonly SemaphoreSlim HookBuildLock = new(1, 1);
    static bool hookBuilt;

    static async Task EnsureHookBuiltAsync()
    {
        await HookBuildLock.WaitAsync();
        try
        {
            if (hookBuilt) return;
            var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../ProxyDog.Hook/build.sh"));
            var native = Path.Combine(AppContext.BaseDirectory, "native");
            using var build = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("sh", [script, native]) { RedirectStandardError = true })!;
            var err = await build.StandardError.ReadToEndAsync();
            await build.WaitForExitAsync();
            Assert.True(build.ExitCode == 0, $"hook build failed: {err}");
            hookBuilt = true;
        }
        finally
        {
            HookBuildLock.Release();
        }
    }

    static async Task<(string Exe, string Dir)> PrepareAppAsync()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        await EnsureHookBuiltAsync();

        // A compiled stand-in: Apple's own sleep is arm64e, which the hook (plain arm64) cannot be injected into.
        var dir = Directory.CreateTempSubdirectory().FullName;
        var exe = Path.Combine(dir, "fake-app");
        var src = Path.Combine(dir, "fake-app.c");
        File.WriteAllText(src, "#include <unistd.h>\nint main(void) { sleep(60); return 0; }\n");
        using var cc = System.Diagnostics.Process.Start("cc", [src, "-o", exe])!;
        await cc.WaitForExitAsync();
        Assert.Equal(0, cc.ExitCode);
        return (exe, dir);
    }

    static bool WaitFor(Func<bool> cond, int ms = 8000)
    {
        for (var i = 0; i < ms / 50 && !cond(); i++) Thread.Sleep(50);
        return cond();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Turning_the_rule_off_closes_the_app_only_when_the_kill_switch_is_armed(bool killSwitch)
    {
        if (OperatingSystem.IsWindows()) return;
        var (exe, dir) = await PrepareAppAsync();
        try
        {
            using var up = new FakeUpstream(ProxyKind.Socks5);
            var rule = new ProxyRule { Kind = ProxyKind.Socks5, Host = "127.0.0.1", Port = up.ProxyPort, AppPath = exe, KillSwitch = killSwitch };
            var session = new RuleSession(rule);
            session.Start();
            Assert.True(WaitFor(() => session.IsAppRunning));
            Assert.NotEmpty(AppLauncher.FindRunning(exe));

            await session.StopAsync();

            if (killSwitch)
                Assert.True(WaitFor(() => AppLauncher.FindRunning(exe).Count == 0), "app should have been closed");
            else
            {
                await Task.Delay(300);
                Assert.True(session.IsAppRunning, "without the kill switch the app is left alone");
                AppLauncher.Terminate(AppLauncher.FindRunning(exe).Select(p => p.Id), TimeSpan.FromSeconds(2));
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Arming_the_kill_switch_on_a_running_rule_takes_effect_immediately()
    {
        if (OperatingSystem.IsWindows()) return;
        var (exe, dir) = await PrepareAppAsync();
        try
        {
            var up = new FakeUpstream(ProxyKind.Socks5);
            // An existing rule: started without the kill switch.
            var rule = new ProxyRule { Kind = ProxyKind.Socks5, Host = "127.0.0.1", Port = up.ProxyPort, AppPath = exe };
            var session = new RuleSession(rule) { WatchInterval = TimeSpan.FromMilliseconds(150) };
            var tripped = new TaskCompletionSource<string>();
            session.KillSwitchTripped += tripped.SetResult;
            session.Start();
            Assert.True(WaitFor(() => session.IsAppRunning));

            // Without the switch a dead proxy leaves the app alone.
            up.Dispose();
            await Task.Delay(800);
            Assert.False(tripped.Task.IsCompleted);
            Assert.True(session.IsAppRunning);

            // Arm it while the rule is on: the broken proxy is noticed straight away.
            session.SetKillSwitch(true);
            Assert.True(rule.KillSwitch);
            await tripped.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(WaitFor(() => AppLauncher.FindRunning(exe).Count == 0), "app should have been closed");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Disarming_stops_the_watchdog_so_a_dead_proxy_no_longer_closes_the_app()
    {
        if (OperatingSystem.IsWindows()) return;
        var (exe, dir) = await PrepareAppAsync();
        try
        {
            var up = new FakeUpstream(ProxyKind.Socks5);
            var rule = new ProxyRule { Kind = ProxyKind.Socks5, Host = "127.0.0.1", Port = up.ProxyPort, AppPath = exe, KillSwitch = true };
            var session = new RuleSession(rule) { WatchInterval = TimeSpan.FromMilliseconds(150) };
            var tripped = new TaskCompletionSource<string>();
            session.KillSwitchTripped += tripped.SetResult;
            session.Start();
            Assert.True(WaitFor(() => session.IsAppRunning));

            session.SetKillSwitch(false);
            up.Dispose();
            await Task.Delay(1200);

            Assert.False(tripped.Task.IsCompleted);
            Assert.True(session.IsAppRunning);
            await session.StopAsync(); // off, and with the switch disarmed the app stays
            await Task.Delay(300);
            Assert.True(session.IsAppRunning);
            AppLauncher.Terminate(AppLauncher.FindRunning(exe).Select(p => p.Id), TimeSpan.FromSeconds(2));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Proxy_dying_closes_the_app_and_stops_the_rule()
    {
        if (OperatingSystem.IsWindows()) return;
        var (exe, dir) = await PrepareAppAsync();
        try
        {
            var up = new FakeUpstream(ProxyKind.Socks5);
            var rule = new ProxyRule { Kind = ProxyKind.Socks5, Host = "127.0.0.1", Port = up.ProxyPort, AppPath = exe, KillSwitch = true };
            var session = new RuleSession(rule) { WatchInterval = TimeSpan.FromMilliseconds(150) };
            var tripped = new TaskCompletionSource<string>();
            session.KillSwitchTripped += tripped.SetResult;
            session.Start();
            Assert.True(WaitFor(() => session.IsAppRunning));

            up.Dispose(); // the proxy goes away

            var reason = await tripped.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Contains("not reachable", reason);
            Assert.True(WaitFor(() => AppLauncher.FindRunning(exe).Count == 0), "app should have been closed");
            Assert.False(session.IsRunning);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
