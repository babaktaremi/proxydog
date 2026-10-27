using System.Diagnostics;
using System.IO.Compression;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ProxyDog.Core;

namespace ProxyDog.Engine;

/// <summary>Finds an Xray / V2Ray executable: next to the app, in ProxyDog's own folder, or on the PATH.</summary>
public static class V2RayCoreLocator
{
    public static string UserCoreDirectory => Path.Combine(Path.GetDirectoryName(RuleStore.DefaultPath)!, "core");

    public static string? Find() => Find(
        File.Exists,
        Environment.GetEnvironmentVariable,
        AppContext.BaseDirectory,
        UserCoreDirectory,
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));

    public static string? Find(Func<string, bool> exists, Func<string, string?> env, string baseDirectory, string userCoreDirectory, IEnumerable<string> pathDirectories)
    {
        // An explicit choice wins.
        if (env("PROXYDOG_V2RAY_CORE") is { Length: > 0 } explicitPath && exists(explicitPath))
            return explicitPath;

        string[] names = ["xray", "v2ray"];
        IEnumerable<string> directories = new[] { Path.Combine(baseDirectory, "core"), userCoreDirectory }
            .Concat(pathDirectories.Where(d => d.Length > 0))
            .Concat(["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"]);

        foreach (var directory in directories)
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory, name);
                if (exists(candidate)) return candidate;
            }
        return null;
    }
}

/// <summary>An Xray core process serving a local SOCKS5 port that tunnels through a V2Ray profile.</summary>
public sealed class V2RayCore
{
    readonly Process process;
    readonly Queue<string> tail = new();
    bool stopping;

    V2RayCore(Process process, int port)
    {
        this.process = process;
        Port = port;
    }

    /// <summary>The loopback port the core listens on (SOCKS5, no authentication).</summary>
    public int Port { get; }

    public string RecentLog
    {
        get { lock (tail) return string.Join(Environment.NewLine, tail); }
    }

    /// <summary>The core stopped on its own, with its last log lines.</summary>
    public event Action<string>? Exited;

    public static async Task<V2RayCore> StartAsync(string corePath, V2RayEndpoint endpoint, TimeSpan? readyTimeout = null, CancellationToken ct = default)
    {
        var port = FreePort();
        var configPath = Path.Combine(Path.GetTempPath(), $"proxydog-core-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(configPath, XrayConfig.Build(endpoint, port), ct);
        // The config holds the server's UUID and keys.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(configPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        try
        {
            var psi = new ProcessStartInfo(corePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(configPath);

            var p = Process.Start(psi) ?? throw new InvalidOperationException("The V2Ray core did not start.");
            var core = new V2RayCore(p, port);
            p.OutputDataReceived += (_, e) => core.Remember(e.Data);
            p.ErrorDataReceived += (_, e) => core.Remember(e.Data);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) =>
            {
                if (core.stopping) return;
                core.DrainOutput();
                core.Exited?.Invoke(core.Describe("The V2Ray core stopped."));
            };

            await core.WaitUntilListeningAsync(readyTimeout ?? TimeSpan.FromSeconds(10), ct);
            return core;
        }
        finally
        {
            try { File.Delete(configPath); } catch (IOException) { /* it only held secrets; best effort */ }
        }
    }

    async Task WaitUntilListeningAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                DrainOutput();
                throw new InvalidOperationException(Describe($"The V2Ray core exited with code {process.ExitCode}."));
            }
            try
            {
                using var probe = new TcpClient();
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(500);
                await probe.ConnectAsync("127.0.0.1", Port, limit.Token);
                return;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct);
            }
        }
        await StopAsync();
        throw new InvalidOperationException(Describe("The V2Ray core did not start listening in time."));
    }

    public async Task StopAsync()
    {
        stopping = true;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            // Already gone.
        }
        process.Dispose();
    }

    // The core's last words arrive on the output pipes a moment after it exits. The parameterless WaitForExit
    // returns only once the redirected streams have been read to the end, so its explanation is not lost.
    void DrainOutput()
    {
        try { process.WaitForExit(); }
        catch (InvalidOperationException) { /* never started or already disposed */ }
    }

    void Remember(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (tail)
        {
            tail.Enqueue(line);
            while (tail.Count > 25) tail.Dequeue();
        }
    }

    string Describe(string headline) =>
        RecentLog is { Length: > 0 } log ? $"{headline} {LastUsefulLine(log)}" : headline;

    static string LastUsefulLine(string log) =>
        log.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";

    static int FreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>
/// Downloads the Xray core from its official GitHub release into ProxyDog's own folder, checking the published
/// SHA-256 first. Only runs when the user asks for it.
/// </summary>
public sealed class V2RayCoreInstaller(HttpClient http, string repository = "XTLS/Xray-core")
{
    public static string? AssetName(OSPlatform os, Architecture arch)
    {
        if (os == OSPlatform.OSX)
            return arch switch { Architecture.Arm64 => "Xray-macos-arm64-v8a.zip", Architecture.X64 => "Xray-macos-64.zip", _ => null };
        if (os == OSPlatform.Linux)
            return arch switch { Architecture.Arm64 => "Xray-linux-arm64-v8a.zip", Architecture.X64 => "Xray-linux-64.zip", _ => null };
        return null;
    }

    public static string? CurrentAssetName() =>
        OperatingSystem.IsMacOS() ? AssetName(OSPlatform.OSX, RuntimeInformation.OSArchitecture)
        : OperatingSystem.IsLinux() ? AssetName(OSPlatform.Linux, RuntimeInformation.OSArchitecture)
        : null;

    /// <summary>Installs the newest release and returns the path of the executable.</summary>
    public async Task<string> InstallAsync(string destination, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var asset = CurrentAssetName()
            ?? throw new PlatformNotSupportedException("There is no Xray build for this computer. Install xray yourself and ProxyDog will find it.");

        progress?.Report("Looking up the latest Xray release…");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("ProxyDog");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var release = await http.SendAsync(request, ct);
        release.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await release.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var tag = doc.RootElement.GetProperty("tag_name").GetString()
                  ?? throw new InvalidOperationException("The Xray release has no version.");

        var baseUrl = $"https://github.com/{repository}/releases/download/{tag}/{asset}";
        progress?.Report($"Downloading Xray {tag}…");
        var zip = await http.GetByteArrayAsync(baseUrl, ct);

        progress?.Report("Checking the download…");
        var digest = await http.GetStringAsync(baseUrl + ".dgst", ct);
        var expected = ExpectedSha256(digest)
                       ?? throw new InvalidOperationException("The release has no SHA-256 checksum to verify against.");
        var actual = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The downloaded file does not match its published checksum, so it was not installed.");

        progress?.Report("Installing…");
        Directory.CreateDirectory(destination);
        var target = Path.Combine(destination, "xray");
        using var archive = new ZipArchive(new MemoryStream(zip));
        var entry = archive.GetEntry("xray") ?? throw new InvalidOperationException("The download does not contain the xray program.");
        var temp = target + ".download";
        await using (var source = entry.Open())
        await using (var file = File.Create(temp))
            await source.CopyToAsync(file, ct);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.Move(temp, target, overwrite: true);
        File.WriteAllText(Path.Combine(destination, "VERSION"), tag);

        progress?.Report($"Xray {tag} is installed.");
        return target;
    }

    /// <summary>The .dgst file lists "SHA2-256= &lt;hex&gt;" among other hashes.</summary>
    public static string? ExpectedSha256(string digestFile)
    {
        foreach (var line in digestFile.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("SHA2-256", StringComparison.OrdinalIgnoreCase)) continue;
            var hex = trimmed[(trimmed.IndexOf('=') + 1)..].Trim();
            if (hex.Length == 64 && hex.All(Uri.IsHexDigit)) return hex;
        }
        return null;
    }
}
