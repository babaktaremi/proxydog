using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ProxyDog.Core;
using ProxyDog.Engine;

namespace ProxyDog.Tests;

public class V2RayLinkTests
{
    const string Uuid = "6f1c2b1e-7f3a-4c52-9b3e-2d4a8e9c1a10";

    static V2RayEndpoint Parse(string link)
    {
        Assert.True(V2RayLink.TryParse(link, out var e, out var error), error);
        return e;
    }

    static string Fail(string? link)
    {
        Assert.False(V2RayLink.TryParse(link, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        return error;
    }

    [Fact]
    public void Reads_a_vless_reality_link()
    {
        var e = Parse($"vless://{Uuid}@edge.example.com:443?encryption=none&security=reality&sni=www.microsoft.com&fp=chrome" +
                      "&pbk=PUBKEY&sid=ab12&spx=%2F&type=tcp&flow=xtls-rprx-vision#Frankfurt%20Reality");
        Assert.Equal("vless", e.Protocol);
        Assert.Equal("Frankfurt Reality", e.Name);
        Assert.Equal("edge.example.com", e.Address);
        Assert.Equal(443, e.Port);
        Assert.Equal(Uuid, e.UserId);
        Assert.Equal("reality", e.Security);
        Assert.Equal("www.microsoft.com", e.Sni);
        Assert.Equal("PUBKEY", e.PublicKey);
        Assert.Equal("ab12", e.ShortId);
        Assert.Equal("/", e.SpiderX);
        Assert.Equal("xtls-rprx-vision", e.Flow);
        Assert.Equal("tcp", e.Network);
        Assert.Equal("VLESS edge.example.com:443  tcp/reality", e.Summary);
    }

    [Fact]
    public void Reads_a_vless_websocket_tls_link_and_an_ipv6_host()
    {
        var e = Parse($"vless://{Uuid}@[2001:db8::1]:8443?type=ws&security=tls&sni=cdn.example.com&host=cdn.example.com&path=%2Fray%3Fed%3D2048&alpn=h2%2Chttp%2F1.1&allowInsecure=1");
        Assert.Equal("2001:db8::1", e.Address);
        Assert.Equal(8443, e.Port);
        Assert.Equal("ws", e.Network);
        Assert.Equal("/ray?ed=2048", e.Path);
        Assert.Equal("h2,http/1.1", e.Alpn);
        Assert.True(e.AllowInsecure);
        Assert.Equal("2001:db8::1:8443", e.Name); // no #name in the link
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reads_a_vmess_base64_json_link(bool urlSafeWithoutPadding)
    {
        var json = """{"v":"2","ps":"Tokyo WS+TLS","add":"jp.example.net","port":443,"id":"11111111-2222-3333-4444-555555555555","aid":"0","scy":"auto","net":"ws","type":"none","host":"jp.example.net","path":"/ray","tls":"tls","sni":"jp.example.net"}""";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        if (urlSafeWithoutPadding) b64 = b64.TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var e = Parse("vmess://" + b64);
        Assert.Equal("vmess", e.Protocol);
        Assert.Equal("Tokyo WS+TLS", e.Name);
        Assert.Equal("jp.example.net", e.Address);
        Assert.Equal(443, e.Port);         // a number in the JSON, not a string, is fine
        Assert.Equal("ws", e.Network);
        Assert.Equal("/ray", e.Path);
        Assert.Equal("tls", e.Security);
        Assert.Equal("auto", e.Cipher);
        Assert.Equal(0, e.AlterId);
    }

    [Fact]
    public void Reads_the_url_style_vmess_link()
    {
        var e = Parse($"vmess://auto:{Uuid}@vm.example.org:10086?type=ws&security=tls&path=%2Fx#Name%20One");
        Assert.Equal("vmess", e.Protocol);
        Assert.Equal("auto", e.Cipher);
        Assert.Equal(Uuid, e.UserId);
        Assert.Equal("Name One", e.Name);
        Assert.Equal("/x", e.Path);
    }

    [Theory]
    [InlineData("http", "h2")]
    [InlineData("splithttp", "xhttp")]
    [InlineData("raw", "tcp")]
    [InlineData("mkcp", "kcp")]
    public void Normalises_transport_aliases(string given, string expected) =>
        Assert.Equal(expected, Parse($"vless://{Uuid}@h.example.com:443?type={given}").Network);

    [Fact]
    public void Explains_what_is_wrong()
    {
        Assert.Contains("Paste", Fail(""));
        Assert.Contains("Paste", Fail(null));
        Assert.Contains("not a vless:// or vmess://", Fail("https://example.com"));
        Assert.Contains("not supported yet", Fail("trojan://pw@h.example.com:443"));
        Assert.Contains("not supported yet", Fail("ss://abc@h.example.com:443"));
        Assert.Contains("port", Fail($"vless://{Uuid}@h.example.com?type=tcp"));
        Assert.Contains("user ID", Fail("vless://@h.example.com:443"));
        Assert.Contains("public key", Fail($"vless://{Uuid}@h.example.com:443?security=reality"));
        Assert.Contains("transport", Fail($"vless://{Uuid}@h.example.com:443?type=carrierpigeon"));
        Assert.Contains("security", Fail($"vless://{Uuid}@h.example.com:443?security=magic"));
        Assert.Contains("damaged", Fail("vmess://%%%not-base64%%%"));
        Assert.Contains("damaged", Fail("vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("this is not json"))));
    }

    [Fact]
    public void Trims_whitespace_around_a_pasted_link() =>
        Assert.Equal("h.example.com", Parse($"\n  vless://{Uuid}@h.example.com:443?type=tcp  \n").Address);
}

public class XrayConfigTests
{
    const string Uuid = "6f1c2b1e-7f3a-4c52-9b3e-2d4a8e9c1a10";

    static JsonObject Config(string link, int port = 41234)
    {
        Assert.True(V2RayLink.TryParse(link, out var e, out var error), error);
        return JsonNode.Parse(XrayConfig.Build(e, port))!.AsObject();
    }

    [Fact]
    public void Listens_on_loopback_only_with_no_login()
    {
        var c = Config($"vless://{Uuid}@h.example.com:443?type=tcp");
        var inbound = c["inbounds"]![0]!;
        Assert.Equal("127.0.0.1", (string)inbound["listen"]!);
        Assert.Equal(41234, (int)inbound["port"]!);
        Assert.Equal("socks", (string)inbound["protocol"]!);
        Assert.Equal("noauth", (string)inbound["settings"]!["auth"]!);
        Assert.Single(c["outbounds"]!.AsArray());
    }

    [Fact]
    public void Writes_a_vless_reality_outbound()
    {
        var c = Config($"vless://{Uuid}@edge.example.com:443?security=reality&sni=www.microsoft.com&fp=firefox&pbk=PUB&sid=ab&spx=%2F&flow=xtls-rprx-vision");
        var o = c["outbounds"]![0]!;
        Assert.Equal("vless", (string)o["protocol"]!);
        var vnext = o["settings"]!["vnext"]![0]!;
        Assert.Equal("edge.example.com", (string)vnext["address"]!);
        Assert.Equal(443, (int)vnext["port"]!);
        Assert.Equal(Uuid, (string)vnext["users"]![0]!["id"]!);
        Assert.Equal("none", (string)vnext["users"]![0]!["encryption"]!);
        Assert.Equal("xtls-rprx-vision", (string)vnext["users"]![0]!["flow"]!);
        var s = o["streamSettings"]!;
        Assert.Equal("reality", (string)s["security"]!);
        Assert.Equal("PUB", (string)s["realitySettings"]!["publicKey"]!);
        Assert.Equal("www.microsoft.com", (string)s["realitySettings"]!["serverName"]!);
        Assert.Equal("firefox", (string)s["realitySettings"]!["fingerprint"]!);
        Assert.Equal("ab", (string)s["realitySettings"]!["shortId"]!);
        Assert.Null(s["tlsSettings"]);
    }

    [Fact]
    public void Writes_a_vmess_websocket_tls_outbound()
    {
        var json = """{"ps":"x","add":"jp.example.net","port":"443","id":"11111111-2222-3333-4444-555555555555","aid":"2","scy":"aes-128-gcm","net":"ws","host":"front.example.net","path":"/ray","tls":"tls","sni":"jp.example.net","alpn":"h2,http/1.1"}""";
        var c = Config("vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)));
        var o = c["outbounds"]![0]!;
        Assert.Equal("vmess", (string)o["protocol"]!);
        var user = o["settings"]!["vnext"]![0]!["users"]![0]!;
        Assert.Equal(2, (int)user["alterId"]!);
        Assert.Equal("aes-128-gcm", (string)user["security"]!);
        var s = o["streamSettings"]!;
        Assert.Equal("ws", (string)s["network"]!);
        Assert.Equal("/ray", (string)s["wsSettings"]!["path"]!);
        Assert.Equal("front.example.net", (string)s["wsSettings"]!["headers"]!["Host"]!);
        Assert.Equal("jp.example.net", (string)s["tlsSettings"]!["serverName"]!);
        Assert.Equal(2, s["tlsSettings"]!["alpn"]!.AsArray().Count);
    }

    [Theory]
    [InlineData("grpc", "grpcSettings")]
    [InlineData("h2", "httpSettings")]
    [InlineData("kcp", "kcpSettings")]
    [InlineData("httpupgrade", "httpupgradeSettings")]
    [InlineData("xhttp", "xhttpSettings")]
    [InlineData("quic", "quicSettings")]
    public void Each_transport_gets_its_own_settings_block(string network, string block)
    {
        var s = Config($"vless://{Uuid}@h.example.com:443?type={network}&path=svc")["outbounds"]![0]!["streamSettings"]!;
        Assert.Equal(network, (string)s["network"]!);
        Assert.NotNull(s[block]);
    }

    [Fact]
    public void Grpc_uses_the_service_name_and_tcp_http_header_is_honoured()
    {
        var grpc = Config($"vless://{Uuid}@h.example.com:443?type=grpc&path=myService&mode=multi")["outbounds"]![0]!["streamSettings"]!["grpcSettings"]!;
        Assert.Equal("myService", (string)grpc["serviceName"]!);
        Assert.True((bool)grpc["multiMode"]!);

        var tcp = Config($"vless://{Uuid}@h.example.com:80?type=tcp&headerType=http&host=a.example.com&path=%2Fp")["outbounds"]![0]!["streamSettings"]!["tcpSettings"]!["header"]!;
        Assert.Equal("http", (string)tcp["type"]!);
        Assert.Equal("a.example.com", (string)tcp["request"]!["headers"]!["Host"]![0]!);
    }

    [Fact]
    public void Plain_tcp_has_no_extra_settings()
    {
        var s = Config($"vless://{Uuid}@h.example.com:443?type=tcp")["outbounds"]![0]!["streamSettings"]!;
        Assert.Equal("none", (string)s["security"]!);
        Assert.Null(s["tlsSettings"]);
        Assert.Null(s["wsSettings"]);
    }
}

public class V2RayProfileStoreTests
{
    [Fact]
    public void Round_trips_and_keeps_a_corrupt_file()
    {
        var dir = Directory.CreateTempSubdirectory();
        var path = Path.Combine(dir.FullName, "p", "v2ray-profiles.json");
        var store = new V2RayProfileStore(path);
        Assert.Empty(store.Load());

        store.Save([new V2RayProfile { Name = "One", Link = "vless://x" }, new V2RayProfile { Name = "Two", Link = "vmess://y" }]);
        var loaded = store.Load();
        Assert.Equal(["One", "Two"], loaded.Select(p => p.Name));
        Assert.All(loaded, p => Assert.NotEqual(Guid.Empty, p.Id));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

        File.WriteAllText(path, "{ nope");
        Assert.Empty(store.Load());
        Assert.True(File.Exists(path + ".corrupt"));
        dir.Delete(true);
    }
}

public class V2RayCoreLocatorTests
{
    static string? Find(string[] existing, string? envPath = null, string[]? pathDirs = null) =>
        V2RayCoreLocator.Find(existing.Contains, k => k == "PROXYDOG_V2RAY_CORE" ? envPath : null,
            "/app/", "/home/u/.config/ProxyDog/core", pathDirs ?? ["/usr/sbin", "/custom/bin"]);

    [Fact]
    public void Finds_nothing_when_nothing_is_installed() => Assert.Null(Find([]));

    [Fact]
    public void An_explicit_path_wins_over_everything() =>
        Assert.Equal("/mine/xray", Find(["/mine/xray", "/app/core/xray", "/custom/bin/xray"], "/mine/xray"));

    [Fact]
    public void Prefers_the_bundled_core_then_the_downloaded_one_then_the_path()
    {
        Assert.Equal("/app/core/xray", Find(["/app/core/xray", "/home/u/.config/ProxyDog/core/xray", "/custom/bin/xray"]));
        Assert.Equal("/home/u/.config/ProxyDog/core/xray", Find(["/home/u/.config/ProxyDog/core/xray", "/custom/bin/xray"]));
        Assert.Equal("/custom/bin/xray", Find(["/custom/bin/xray"]));
    }

    [Fact]
    public void Prefers_xray_over_v2ray_and_knows_the_usual_install_folders()
    {
        Assert.Equal("/custom/bin/xray", Find(["/custom/bin/v2ray", "/custom/bin/xray"]));
        Assert.Equal("/custom/bin/v2ray", Find(["/custom/bin/v2ray"]));
        Assert.Equal("/opt/homebrew/bin/xray", Find(["/opt/homebrew/bin/xray"]));
    }
}

public class V2RayCoreInstallerTests
{
    sealed class Fake(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request.RequestUri!.ToString()));
        }
    }

    static byte[] ZipWith(string name, string content)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        using (var w = new StreamWriter(zip.CreateEntry(name).Open()))
            w.Write(content);
        return ms.ToArray();
    }

    static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    static (V2RayCoreInstaller Installer, Fake Handler) Make(byte[] zip, string digest)
    {
        var asset = V2RayCoreInstaller.CurrentAssetName();
        var handler = new Fake(url => url switch
        {
            "https://api.github.com/repos/XTLS/Xray-core/releases/latest" => Ok("""{"tag_name":"v9.9.9"}"""),
            var u when u == $"https://github.com/XTLS/Xray-core/releases/download/v9.9.9/{asset}" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) },
            var u when u == $"https://github.com/XTLS/Xray-core/releases/download/v9.9.9/{asset}.dgst" => Ok(digest),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        return (new V2RayCoreInstaller(new HttpClient(handler)), handler);
    }

    static string Digest(byte[] zip) =>
        $"MD5= 00\nSHA1= 11\nSHA2-256= {Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant()}\nSHA2-512= 22\n";

    [Fact]
    public void Picks_the_right_asset_for_each_platform()
    {
        using var _ = new AssertionScope();
        Assert.Equal("Xray-macos-arm64-v8a.zip", V2RayCoreInstaller.AssetName(System.Runtime.InteropServices.OSPlatform.OSX, System.Runtime.InteropServices.Architecture.Arm64));
        Assert.Equal("Xray-macos-64.zip", V2RayCoreInstaller.AssetName(System.Runtime.InteropServices.OSPlatform.OSX, System.Runtime.InteropServices.Architecture.X64));
        Assert.Equal("Xray-linux-64.zip", V2RayCoreInstaller.AssetName(System.Runtime.InteropServices.OSPlatform.Linux, System.Runtime.InteropServices.Architecture.X64));
        Assert.Equal("Xray-linux-arm64-v8a.zip", V2RayCoreInstaller.AssetName(System.Runtime.InteropServices.OSPlatform.Linux, System.Runtime.InteropServices.Architecture.Arm64));
        Assert.Null(V2RayCoreInstaller.AssetName(System.Runtime.InteropServices.OSPlatform.Windows, System.Runtime.InteropServices.Architecture.X64));
    }

    // minimal stand-in so the assertions above read as one group
    sealed class AssertionScope : IDisposable { public void Dispose() { } }

    [Fact]
    public void Reads_the_sha256_line_from_the_published_digest()
    {
        var hex = new string('a', 64);
        Assert.Equal(hex, V2RayCoreInstaller.ExpectedSha256($"MD5= 0e\nSHA1= d4\nSHA2-256= {hex}\nSHA2-512= b5"));
        Assert.Null(V2RayCoreInstaller.ExpectedSha256("MD5= 0e\nSHA1= d4"));
        Assert.Null(V2RayCoreInstaller.ExpectedSha256("SHA2-256= nothex"));
    }

    [Fact]
    public async Task Downloads_verifies_and_installs_an_executable()
    {
        if (V2RayCoreInstaller.CurrentAssetName() is null) return;
        var zip = ZipWith("xray", "#!/bin/sh\n");
        var (installer, handler) = Make(zip, Digest(zip));
        var dir = Directory.CreateTempSubdirectory();
        var messages = new List<string>();

        var path = await installer.InstallAsync(Path.Combine(dir.FullName, "core"), new Progress<string>(messages.Add));

        Assert.Equal(Path.Combine(dir.FullName, "core", "xray"), path);
        Assert.Equal("#!/bin/sh\n", File.ReadAllText(path));
        Assert.Equal("v9.9.9", File.ReadAllText(Path.Combine(dir.FullName, "core", "VERSION")));
        if (!OperatingSystem.IsWindows())
            Assert.True((File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0);
        Assert.Equal(3, handler.Requested.Count);
        dir.Delete(true);
    }

    [Fact]
    public async Task A_download_that_does_not_match_its_checksum_is_not_installed()
    {
        if (V2RayCoreInstaller.CurrentAssetName() is null) return;
        var zip = ZipWith("xray", "tampered");
        var (installer, _) = Make(zip, $"SHA2-256= {new string('0', 64)}\n");
        var dir = Directory.CreateTempSubdirectory();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(Path.Combine(dir.FullName, "core")));

        Assert.Contains("checksum", ex.Message);
        Assert.False(File.Exists(Path.Combine(dir.FullName, "core", "xray")));
        dir.Delete(true);
    }

    [Fact]
    public async Task A_zip_without_the_program_is_rejected()
    {
        if (V2RayCoreInstaller.CurrentAssetName() is null) return;
        var zip = ZipWith("README.md", "no binary here");
        var (installer, _) = Make(zip, Digest(zip));
        var dir = Directory.CreateTempSubdirectory();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(Path.Combine(dir.FullName, "core")));
        Assert.Contains("xray", ex.Message);
        dir.Delete(true);
    }
}

/// <summary>A compiled stand-in for Xray: reads the config, listens on its SOCKS port, and can fail or die on cue.</summary>
static class FakeCore
{
    const string Source = """
        #include <arpa/inet.h>
        #include <netinet/in.h>
        #include <stdio.h>
        #include <stdlib.h>
        #include <string.h>
        #include <sys/socket.h>
        #include <unistd.h>
        int main(int argc, char **argv) {
        #ifdef FAIL
            fprintf(stderr, "Failed to start: bad config near line 3\n");
            return 23;
        #endif
            if (argc < 4 || strcmp(argv[1], "run") != 0 || strcmp(argv[2], "-c") != 0) { fprintf(stderr, "usage\n"); return 2; }
            FILE *f = fopen(argv[3], "r");
            if (!f) { fprintf(stderr, "cannot read config\n"); return 3; }
            char buf[16384]; size_t n = fread(buf, 1, sizeof buf - 1, f); buf[n] = 0; fclose(f);
            char *p = strstr(buf, "\"port\":");
            if (!p) { fprintf(stderr, "no port in config\n"); return 4; }
            int port = atoi(p + 7);
        #ifdef NOLISTEN
            sleep(60); return 0;
        #endif
            int s = socket(AF_INET, SOCK_STREAM, 0), one = 1;
            setsockopt(s, SOL_SOCKET, SO_REUSEADDR, &one, sizeof one);
            struct sockaddr_in a; memset(&a, 0, sizeof a);
            a.sin_family = AF_INET; a.sin_port = htons(port); inet_pton(AF_INET, "127.0.0.1", &a.sin_addr);
            if (bind(s, (struct sockaddr *)&a, sizeof a) != 0 || listen(s, 8) != 0) { perror("bind"); return 5; }
        #ifdef DIE_AFTER
            alarm(DIE_AFTER);   /* SIGALRM ends the process: a core that crashes later */
        #endif
            for (;;) { int c = accept(s, 0, 0); if (c >= 0) close(c); }
        }
        """;

    public static async Task<(string Exe, string Dir)> BuildAsync(string defines = "")
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var src = Path.Combine(dir, "fake-core.c");
        var exe = Path.Combine(dir, "xray");
        File.WriteAllText(src, Source);
        var psi = new System.Diagnostics.ProcessStartInfo("cc") { RedirectStandardError = true };
        foreach (var a in defines.Split(' ', StringSplitOptions.RemoveEmptyEntries)) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(src);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(exe);
        using var cc = System.Diagnostics.Process.Start(psi)!;
        var err = await cc.StandardError.ReadToEndAsync();
        await cc.WaitForExitAsync();
        Assert.True(cc.ExitCode == 0, err);
        return (exe, dir);
    }

    /// <summary>A profile whose "server" is the given local port (the default, 9, has nothing listening).</summary>
    public static V2RayEndpoint Endpoint(int serverPort = 9)
    {
        Assert.True(V2RayLink.TryParse($"vless://6f1c2b1e-7f3a-4c52-9b3e-2d4a8e9c1a10@127.0.0.1:{serverPort}?type=tcp#Fake", out var e, out var error), error);
        return e;
    }

    public static bool Listening(int port)
    {
        try { using var c = new TcpClient(); c.Connect(IPAddress.Loopback, port); return true; }
        catch (SocketException) { return false; }
    }

    public static bool WaitFor(Func<bool> condition, int ms = 8000)
    {
        for (var i = 0; i < ms / 50 && !condition(); i++) Thread.Sleep(50);
        return condition();
    }
}

public class V2RayCoreTests
{
    [Fact]
    public async Task Starts_listening_and_stops_cleanly()
    {
        if (OperatingSystem.IsWindows()) return;
        var (exe, dir) = await FakeCore.BuildAsync();
        try
        {
            var core = await V2RayCore.StartAsync(exe, FakeCore.Endpoint());
            Assert.True(FakeCore.Listening(core.Port));

            await core.StopAsync();
            Assert.True(FakeCore.WaitFor(() => !FakeCore.Listening(core.Port)), "the core should be gone");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task A_core_that_fails_to_start_reports_why()
    {
        if (OperatingSystem.IsWindows()) return;
        var (exe, dir) = await FakeCore.BuildAsync("-DFAIL");
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => V2RayCore.StartAsync(exe, FakeCore.Endpoint()));
            Assert.Contains("code 23", ex.Message);
            Assert.Contains("Failed to start: bad config", ex.Message); // the core's own last line, so the user can act on it
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task A_core_that_never_listens_times_out_and_is_cleaned_up()
    {
        if (OperatingSystem.IsWindows()) return;
        var (exe, dir) = await FakeCore.BuildAsync("-DNOLISTEN");
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                V2RayCore.StartAsync(exe, FakeCore.Endpoint(), TimeSpan.FromMilliseconds(600)));
            Assert.Contains("did not start listening", ex.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Tells_us_when_the_core_dies_later_but_not_when_we_stop_it()
    {
        if (OperatingSystem.IsWindows()) return;
        var (dying, dir1) = await FakeCore.BuildAsync("-DDIE_AFTER=1");
        var (steady, dir2) = await FakeCore.BuildAsync();
        try
        {
            var core = await V2RayCore.StartAsync(dying, FakeCore.Endpoint());
            var died = new TaskCompletionSource<string>();
            core.Exited += died.SetResult;
            Assert.Contains("stopped", await died.Task.WaitAsync(TimeSpan.FromSeconds(10)));

            var calm = await V2RayCore.StartAsync(steady, FakeCore.Endpoint());
            var fired = false;
            calm.Exited += _ => fired = true;
            await calm.StopAsync();
            await Task.Delay(400);
            Assert.False(fired, "stopping the core ourselves is not a failure");
        }
        finally { Directory.Delete(dir1, true); Directory.Delete(dir2, true); }
    }

    [Fact]
    public async Task Removes_the_config_file_that_held_the_keys()
    {
        if (OperatingSystem.IsWindows()) return;
        var (exe, dir) = await FakeCore.BuildAsync();
        try
        {
            var before = Directory.GetFiles(Path.GetTempPath(), "proxydog-core-*.json").Length;
            var core = await V2RayCore.StartAsync(exe, FakeCore.Endpoint());
            Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "proxydog-core-*.json").Length);
            await core.StopAsync();
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class V2RaySessionTests
{
    static ProxyRule V2RayRule(string app, bool killSwitch) => new()
    {
        Kind = ProxyKind.V2Ray,
        V2RayProfileName = "Fake",
        AppPath = app,
        KillSwitch = killSwitch,
    };

    [Fact]
    public async Task A_v2ray_rule_runs_the_core_with_the_rule_and_stops_it_with_the_rule()
    {
        if (OperatingSystem.IsWindows()) return;
        var (app, appDir) = await KillSwitchTests.PrepareAppPublicAsync();
        var (core, coreDir) = await FakeCore.BuildAsync();
        try
        {
            var rule = V2RayRule(app, killSwitch: true);
            var notices = new List<string>();
            var session = new RuleSession(rule) { V2Ray = new V2RayStart(FakeCore.Endpoint(), core) };
            session.Notice += (text, _) => { lock (notices) notices.Add(text); };

            session.Start();
            Assert.True(session.IsRunning);
            Assert.NotEqual(0, rule.LocalPort);
            lock (notices) Assert.Contains(notices, n => n.StartsWith("Forwarding through V2Ray: Fake"));

            await session.StopAsync();
            Assert.False(session.IsRunning);
        }
        finally { Directory.Delete(appDir, true); Directory.Delete(coreDir, true); }
    }

    [Fact]
    public async Task A_rule_without_a_profile_says_so()
    {
        if (OperatingSystem.IsWindows()) return;
        var (app, appDir) = await KillSwitchTests.PrepareAppPublicAsync();
        try
        {
            var session = new RuleSession(V2RayRule(app, false));
            var ex = Assert.Throws<InvalidOperationException>(session.Start);
            Assert.Contains("no V2Ray profile", ex.Message);
            Assert.False(session.IsRunning);
        }
        finally { Directory.Delete(appDir, true); }
    }

    [Fact]
    public async Task With_the_kill_switch_a_core_that_dies_closes_the_app_and_stops_the_rule()
    {
        if (OperatingSystem.IsWindows()) return;
        var (app, appDir) = await KillSwitchTests.PrepareAppPublicAsync();
        var (core, coreDir) = await FakeCore.BuildAsync("-DDIE_AFTER=2");
        // The V2Ray server itself stays reachable, so only the core dying can trip the switch.
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        try
        {
            var session = new RuleSession(V2RayRule(app, killSwitch: true))
            {
                V2Ray = new V2RayStart(FakeCore.Endpoint(((IPEndPoint)server.LocalEndpoint).Port), core),
                WatchInterval = TimeSpan.FromMilliseconds(200),
            };
            var tripped = new TaskCompletionSource<string>();
            session.KillSwitchTripped += tripped.SetResult;
            session.Start();
            Assert.True(FakeCore.WaitFor(() => session.IsAppRunning));

            // Nothing happens while the core is alive...
            await Task.Delay(1000);
            Assert.False(tripped.Task.IsCompleted, "tripped while the core and the server were both fine");

            // ...then the core dies (after 2s) and the app is closed.
            var reason = await tripped.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Contains("V2Ray core stopped", reason);
            Assert.True(FakeCore.WaitFor(() => AppLauncher.FindRunning(app).Count == 0), "the app should have been closed");
            Assert.False(session.IsRunning);
        }
        finally { server.Stop(); Directory.Delete(appDir, true); Directory.Delete(coreDir, true); }
    }

    [Fact]
    public async Task With_the_kill_switch_an_unreachable_v2ray_server_closes_the_app()
    {
        if (OperatingSystem.IsWindows()) return;
        var (app, appDir) = await KillSwitchTests.PrepareAppPublicAsync();
        var (core, coreDir) = await FakeCore.BuildAsync();   // the core itself stays up
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var serverPort = ((IPEndPoint)server.LocalEndpoint).Port;
        try
        {
            var session = new RuleSession(V2RayRule(app, killSwitch: true))
            {
                V2Ray = new V2RayStart(FakeCore.Endpoint(serverPort), core),
                WatchInterval = TimeSpan.FromMilliseconds(200),
            };
            var tripped = new TaskCompletionSource<string>();
            session.KillSwitchTripped += tripped.SetResult;
            session.Start();
            Assert.True(FakeCore.WaitFor(() => session.IsAppRunning));
            await Task.Delay(800);
            Assert.False(tripped.Task.IsCompleted);

            server.Stop();   // the V2Ray server goes away

            var reason = await tripped.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Contains($"127.0.0.1:{serverPort}", reason);
            Assert.True(FakeCore.WaitFor(() => AppLauncher.FindRunning(app).Count == 0), "the app should have been closed");
        }
        finally { server.Stop(); Directory.Delete(appDir, true); Directory.Delete(coreDir, true); }
    }
}
