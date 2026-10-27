using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProxyDog.Core;

public enum ProxyKind
{
    Socks5,
    Http,
}

/// <summary>What to do when the chosen application is already running as the rule is switched on.</summary>
public enum RunningAppPolicy
{
    /// <summary>Start another copy and warn that the running one is not proxied.</summary>
    LeaveRunning,

    /// <summary>Quit the running copy, then start it again through the proxy.</summary>
    Restart,
}

public sealed class ProxyRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public ProxyKind Kind { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string AppPath { get; set; } = "";
    public string? Arguments { get; set; }
    // Renamed from WhenRunning (default was Leave): saved rules written before this fix pick up the new default.
    /// <summary>
    /// The loopback port the engine listens on, kept between runs so an app that stores it (Telegram's proxy
    /// setting) still points at the right place. 0 until the rule has run once.
    /// </summary>
    public int LocalPort { get; set; }

    /// <summary>The engine port last handed to the app's own proxy setting (Telegram), so the prompt is not repeated.</summary>
    public int ProxyLinkPort { get; set; }

    /// <summary>Close the application when the rule is turned off or the proxy stops working.</summary>
    public bool KillSwitch { get; set; }
    public RunningAppPolicy IfRunning { get; set; } = RunningAppPolicy.Restart;

    public ProxyRule Clone() => (ProxyRule)MemberwiseClone();
}

public enum ConnectionEventKind
{
    Opened,
    Connected,
    Closed,
    Failed,
}

public sealed record ConnectionEvent(
    Guid Id,
    ConnectionEventKind Kind,
    DateTimeOffset Time,
    string Destination,
    long BytesUp = 0,
    long BytesDown = 0,
    string? Error = null);

/// <summary>Where a connection is headed: a literal IP (from the hook) or a host name (from SOCKS5 clients).</summary>
public readonly record struct Destination(IPAddress? Address, ushort Port, string? Domain = null)
{
    public static Destination ForDomain(string host, ushort port) => new(null, port, host);

    public override string ToString() =>
        Domain is not null ? $"{Domain}:{Port}"
        : Address!.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{Address}]:{Port}"
        : $"{Address}:{Port}";
}

/// <summary>Persists rules as JSON in the per-user application data folder.</summary>
public sealed class RuleStore(string path)
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ProxyDog",
        "rules.json");

    public IReadOnlyList<ProxyRule> Load()
    {
        if (!File.Exists(path))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<ProxyRule>>(File.ReadAllText(path), Options) ?? [];
        }
        catch (JsonException)
        {
            // Keep the unreadable file around instead of silently overwriting it on the next save.
            File.Move(path, path + ".corrupt", overwrite: true);
            return [];
        }
    }

    public void Save(IEnumerable<ProxyRule> rules)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(rules, Options));
        // Proxy passwords live in this file, so keep it private to the user.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>A proxy the user configured before, offered again when setting up a new rule.</summary>
public sealed record RecentProxy(ProxyKind Kind, string Host, int Port, string? Username, string? Password)
{
    public string Label => $"{(Kind == ProxyKind.Socks5 ? "SOCKS5" : "HTTP")}  {Host}:{Port}"
        + (string.IsNullOrEmpty(Username) ? "" : $"  ({Username})");

    public string Key => $"{Kind}|{Host.ToLowerInvariant()}|{Port}|{Username}";

    public static RecentProxy From(ProxyRule r) => new(r.Kind, r.Host, r.Port, r.Username, r.Password);
}

/// <summary>Most recently saved proxies, newest first, kept next to the rules file.</summary>
public sealed class RecentProxyStore(string path, int limit = 8)
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath => Path.Combine(Path.GetDirectoryName(RuleStore.DefaultPath)!, "recent-proxies.json");

    public IReadOnlyList<RecentProxy> Load()
    {
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<RecentProxy>>(File.ReadAllText(path), Options) ?? [];
        }
        catch (JsonException)
        {
            return []; // only a convenience list; start over
        }
    }

    public void Add(RecentProxy proxy)
    {
        var list = Load().Where(p => p.Key != proxy.Key).Prepend(proxy).Take(limit).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list, Options));
        // Contains proxy passwords, like rules.json.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>Minimal shell-style splitting: whitespace separated, single/double quotes group.</summary>
public static class CommandLine
{
    public static List<string> Split(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return result;

        var current = new System.Text.StringBuilder();
        char quote = '\0';
        var has = false;
        foreach (var c in text)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else current.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                has = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (has || current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    has = false;
                }
            }
            else
            {
                current.Append(c);
            }
        }
        if (has || current.Length > 0)
            result.Add(current.ToString());
        return result;
    }
}
