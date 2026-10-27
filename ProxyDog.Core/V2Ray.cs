using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProxyDog.Core;

/// <summary>A V2Ray profile the user added: the share link exactly as pasted, plus a name.</summary>
public sealed class V2RayProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Link { get; set; } = "";
}

/// <summary>What a vless:// or vmess:// share link says, normalised so one config generator can serve both.</summary>
public sealed record V2RayEndpoint
{
    public required string Protocol { get; init; }        // "vless" | "vmess"
    public required string Name { get; init; }
    public required string Address { get; init; }
    public required int Port { get; init; }
    public required string UserId { get; init; }

    /// <summary>VLESS: "none" (the only value in use). VMess: the cipher, e.g. "auto".</summary>
    public string Cipher { get; init; } = "";
    public string? Flow { get; init; }
    public int AlterId { get; init; }

    public string Network { get; init; } = "tcp";         // tcp ws grpc h2 kcp quic httpupgrade xhttp
    public string HeaderType { get; init; } = "none";
    public string? Host { get; init; }
    public string? Path { get; init; }
    public string? GrpcMode { get; init; }
    public string? Seed { get; init; }

    public string Security { get; init; } = "none";       // none | tls | reality
    public string? Sni { get; init; }
    public string? Fingerprint { get; init; }
    public string? Alpn { get; init; }
    public bool AllowInsecure { get; init; }
    public string? PublicKey { get; init; }
    public string? ShortId { get; init; }
    public string? SpiderX { get; init; }

    public string Summary =>
        $"{Protocol.ToUpperInvariant()} {Address}:{Port}  {Network}" + (Security == "none" ? "" : $"/{Security}");
}

public static class V2RayLink
{
    static readonly string[] Networks = ["tcp", "ws", "grpc", "h2", "kcp", "quic", "httpupgrade", "xhttp"];

    /// <summary>Parses a vless:// or vmess:// share link. On failure, <paramref name="error"/> says what to fix.</summary>
    public static bool TryParse(string? link, out V2RayEndpoint endpoint, out string error)
    {
        endpoint = null!;
        error = "";
        link = link?.Trim();
        if (string.IsNullOrEmpty(link))
        {
            error = "Paste a vless:// or vmess:// link.";
            return false;
        }

        var scheme = link.Split("://", 2)[0].ToLowerInvariant();
        try
        {
            switch (scheme)
            {
                case "vless":
                    return ParseUrlForm(link, "vless", out endpoint, out error);
                case "vmess":
                    return ParseVmess(link, out endpoint, out error);
                case "trojan" or "ss" or "ssr" or "hysteria" or "hysteria2" or "hy2" or "tuic" or "wireguard":
                    error = $"{scheme}:// links are not supported yet. Use a vless:// or vmess:// link.";
                    return false;
                default:
                    error = "That is not a vless:// or vmess:// link.";
                    return false;
            }
        }
        catch (Exception ex) when (ex is FormatException or UriFormatException or JsonException or ArgumentException)
        {
            error = "The link is damaged or incomplete: " + ex.Message;
            return false;
        }
    }

    // vless://uuid@host:port?type=ws&security=tls&...#Name   (and the URL-style vmess form)
    static bool ParseUrlForm(string link, string protocol, out V2RayEndpoint endpoint, out string error)
    {
        endpoint = null!;
        var uri = new Uri(link);
        var q = ParseQuery(uri.Query);
        var user = Uri.UnescapeDataString(uri.UserInfo);
        var cipher = protocol == "vless" ? Get(q, "encryption") ?? "none" : Get(q, "encryption") ?? "auto";

        // vmess://security:uuid@host:port is also seen.
        if (protocol == "vmess" && user.Contains(':'))
        {
            var parts = user.Split(':', 2);
            cipher = parts[0];
            user = parts[1];
        }

        var name = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
        var host = uri.Host.Trim('[', ']');
        return Build(protocol, name, host, uri.Port, user, cipher, 0, q, out endpoint, out error);
    }

    // vmess://base64({"v":"2","ps":"Name","add":"host","port":"443","id":"uuid","aid":"0","net":"ws",...})
    static bool ParseVmess(string link, out V2RayEndpoint endpoint, out string error)
    {
        endpoint = null!;
        var body = link["vmess://".Length..];
        var hash = body.IndexOf('#');
        var fragment = hash >= 0 ? Uri.UnescapeDataString(body[(hash + 1)..]) : "";
        if (hash >= 0) body = body[..hash];

        // The URL form has an @; the JSON form is pure base64.
        if (body.Contains('@'))
            return ParseUrlForm(link, "vmess", out endpoint, out error);

        var json = JsonNode.Parse(Encoding.UTF8.GetString(DecodeBase64(body)))?.AsObject()
                   ?? throw new JsonException("not a JSON object");
        string? S(string key) => json[key] switch
        {
            null => null,
            JsonValue v when v.TryGetValue<string>(out var str) => str,
            JsonValue v => v.ToJsonString().Trim('"'),
            _ => null,
        };

        var port = int.TryParse(S("port"), out var p) ? p : 0;
        var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Put(string key, string? value) { if (!string.IsNullOrEmpty(value)) q[key] = value; }
        Put("type", S("net"));            // v2rayN calls the transport "net" and the header "type"
        Put("headerType", S("type"));
        Put("host", S("host"));
        Put("path", S("path"));
        Put("security", S("tls") is "tls" ? "tls" : "none");
        Put("sni", S("sni"));
        Put("fp", S("fp"));
        Put("alpn", S("alpn"));
        Put("allowInsecure", S("allowInsecure") ?? S("insecure"));
        Put("seed", S("path"));
        var name = S("ps");
        if (string.IsNullOrWhiteSpace(name)) name = fragment;

        return Build("vmess", name ?? "", S("add") ?? "", port, S("id") ?? "",
            S("scy") is { Length: > 0 } scy ? scy : "auto",
            int.TryParse(S("aid"), out var aid) ? aid : 0, q, out endpoint, out error);
    }

    static bool Build(string protocol, string name, string address, int port, string userId, string cipher, int alterId,
        Dictionary<string, string> q, out V2RayEndpoint endpoint, out string error)
    {
        endpoint = null!;
        error = "";
        if (string.IsNullOrWhiteSpace(address)) { error = "The link has no server address."; return false; }
        if (port is < 1 or > 65535) { error = "The link has no valid port."; return false; }
        if (string.IsNullOrWhiteSpace(userId)) { error = "The link has no user ID (UUID)."; return false; }

        var network = (Get(q, "type") ?? "tcp").ToLowerInvariant();
        network = network switch { "raw" => "tcp", "http" => "h2", "splithttp" => "xhttp", "mkcp" => "kcp", _ => network };
        if (!Networks.Contains(network)) { error = $"The transport '{network}' is not supported."; return false; }

        var security = (Get(q, "security") ?? "none").ToLowerInvariant();
        if (security is not ("none" or "tls" or "reality")) { error = $"The security '{security}' is not supported."; return false; }
        if (security == "reality" && string.IsNullOrEmpty(Get(q, "pbk")))
        { error = "A REALITY link needs a public key (pbk)."; return false; }

        endpoint = new V2RayEndpoint
        {
            Protocol = protocol,
            Name = string.IsNullOrWhiteSpace(name) ? $"{address}:{port}" : name.Trim(),
            Address = address,
            Port = port,
            UserId = userId,
            Cipher = cipher,
            Flow = Get(q, "flow"),
            AlterId = alterId,
            Network = network,
            HeaderType = Get(q, "headerType") ?? "none",
            Host = Get(q, "host"),
            Path = Get(q, "path"),
            GrpcMode = Get(q, "mode"),
            Seed = Get(q, "seed"),
            Security = security,
            Sni = Get(q, "sni") ?? Get(q, "peer"),
            Fingerprint = Get(q, "fp"),
            Alpn = Get(q, "alpn"),
            AllowInsecure = Get(q, "allowInsecure") is "1" or "true",
            PublicKey = Get(q, "pbk"),
            ShortId = Get(q, "sid"),
            SpiderX = Get(q, "spx"),
        };
        return true;
    }

    static string? Get(Dictionary<string, string> q, string key) =>
        q.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            result[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
        }
        return result;
    }

    // Share links use standard or URL-safe base64, often without padding.
    static byte[] DecodeBase64(string text)
    {
        text = text.Trim().Replace('-', '+').Replace('_', '/');
        text = text.PadRight(text.Length + (4 - text.Length % 4) % 4, '=');
        return Convert.FromBase64String(text);
    }
}

/// <summary>Writes the Xray / V2Ray config that exposes a local SOCKS5 port and sends everything out through the profile.</summary>
public static class XrayConfig
{
    public static string Build(V2RayEndpoint e, int localSocksPort)
    {
        var outbound = new JsonObject
        {
            ["tag"] = "proxy",
            ["protocol"] = e.Protocol,
            ["settings"] = e.Protocol == "vless"
                ? new JsonObject
                {
                    ["vnext"] = new JsonArray(new JsonObject
                    {
                        ["address"] = e.Address,
                        ["port"] = e.Port,
                        ["users"] = new JsonArray(new JsonObject
                        {
                            ["id"] = e.UserId,
                            ["encryption"] = string.IsNullOrEmpty(e.Cipher) ? "none" : e.Cipher,
                            ["flow"] = e.Flow ?? "",
                        }),
                    }),
                }
                : new JsonObject
                {
                    ["vnext"] = new JsonArray(new JsonObject
                    {
                        ["address"] = e.Address,
                        ["port"] = e.Port,
                        ["users"] = new JsonArray(new JsonObject
                        {
                            ["id"] = e.UserId,
                            ["alterId"] = e.AlterId,
                            ["security"] = string.IsNullOrEmpty(e.Cipher) ? "auto" : e.Cipher,
                        }),
                    }),
                },
            ["streamSettings"] = Stream(e),
        };

        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning" },
            ["inbounds"] = new JsonArray(new JsonObject
            {
                ["tag"] = "socks-in",
                ["listen"] = "127.0.0.1",
                ["port"] = localSocksPort,
                ["protocol"] = "socks",
                ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = false },
            }),
            ["outbounds"] = new JsonArray(outbound),
        };
        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    static JsonObject Stream(V2RayEndpoint e)
    {
        var stream = new JsonObject { ["network"] = e.Network, ["security"] = e.Security };

        if (e.Security == "tls")
        {
            var tls = new JsonObject();
            // Recent Xray releases deprecate allowInsecure, so only write it when the link asks for it.
            if (e.AllowInsecure) tls["allowInsecure"] = true;
            if (!string.IsNullOrEmpty(e.Sni ?? e.Host)) tls["serverName"] = e.Sni ?? e.Host;
            if (!string.IsNullOrEmpty(e.Fingerprint)) tls["fingerprint"] = e.Fingerprint;
            if (!string.IsNullOrEmpty(e.Alpn))
                tls["alpn"] = new JsonArray(e.Alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(a => (JsonNode)a).ToArray());
            stream["tlsSettings"] = tls;
        }
        else if (e.Security == "reality")
        {
            stream["realitySettings"] = new JsonObject
            {
                ["serverName"] = e.Sni ?? "",
                ["fingerprint"] = string.IsNullOrEmpty(e.Fingerprint) ? "chrome" : e.Fingerprint,
                ["publicKey"] = e.PublicKey ?? "",
                ["shortId"] = e.ShortId ?? "",
                ["spiderX"] = e.SpiderX ?? "",
            };
        }

        JsonArray? Hosts() => string.IsNullOrEmpty(e.Host) ? null
            : new JsonArray(e.Host.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(h => (JsonNode)h).ToArray());

        switch (e.Network)
        {
            case "tcp" when e.HeaderType == "http":
                stream["tcpSettings"] = new JsonObject
                {
                    ["header"] = new JsonObject
                    {
                        ["type"] = "http",
                        ["request"] = new JsonObject
                        {
                            ["path"] = new JsonArray((e.Path ?? "/").Split(',').Select(p => (JsonNode)p).ToArray()),
                            ["headers"] = new JsonObject { ["Host"] = Hosts() ?? new JsonArray() },
                        },
                    },
                };
                break;
            case "ws":
                var ws = new JsonObject { ["path"] = string.IsNullOrEmpty(e.Path) ? "/" : e.Path };
                if (!string.IsNullOrEmpty(e.Host)) ws["headers"] = new JsonObject { ["Host"] = e.Host };
                stream["wsSettings"] = ws;
                break;
            case "grpc":
                stream["grpcSettings"] = new JsonObject
                {
                    ["serviceName"] = e.Path ?? "",
                    ["multiMode"] = e.GrpcMode == "multi",
                };
                break;
            case "h2":
                var h2 = new JsonObject { ["path"] = string.IsNullOrEmpty(e.Path) ? "/" : e.Path };
                if (Hosts() is { } hosts) h2["host"] = hosts;
                stream["httpSettings"] = h2;
                break;
            case "kcp":
                var kcp = new JsonObject { ["header"] = new JsonObject { ["type"] = e.HeaderType } };
                if (!string.IsNullOrEmpty(e.Seed ?? e.Path)) kcp["seed"] = e.Seed ?? e.Path;
                stream["kcpSettings"] = kcp;
                break;
            case "quic":
                stream["quicSettings"] = new JsonObject
                {
                    ["security"] = e.Host ?? "none",
                    ["key"] = e.Path ?? "",
                    ["header"] = new JsonObject { ["type"] = e.HeaderType },
                };
                break;
            case "httpupgrade":
                var hu = new JsonObject { ["path"] = string.IsNullOrEmpty(e.Path) ? "/" : e.Path };
                if (!string.IsNullOrEmpty(e.Host)) hu["host"] = e.Host;
                stream["httpupgradeSettings"] = hu;
                break;
            case "xhttp":
                var xh = new JsonObject { ["path"] = string.IsNullOrEmpty(e.Path) ? "/" : e.Path };
                if (!string.IsNullOrEmpty(e.Host)) xh["host"] = e.Host;
                if (!string.IsNullOrEmpty(e.GrpcMode)) xh["mode"] = e.GrpcMode;
                stream["xhttpSettings"] = xh;
                break;
        }
        return stream;
    }
}

/// <summary>The profiles the user added, kept in a private file next to the rules.</summary>
public sealed class V2RayProfileStore(string path)
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(Path.GetDirectoryName(RuleStore.DefaultPath)!, "v2ray-profiles.json");

    public List<V2RayProfile> Load()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<V2RayProfile>>(File.ReadAllText(path), Options) ?? []
                : [];
        }
        catch (JsonException)
        {
            File.Move(path, path + ".corrupt", overwrite: true); // keep it rather than silently overwrite
            return [];
        }
    }

    public void Save(IEnumerable<V2RayProfile> profiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(profiles, Options));
        // Share links carry the server's UUID and keys.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }
}
