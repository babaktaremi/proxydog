using System.Net;
using System.Text;
using ProxyDog.Core;

namespace ProxyDog.Engine;

/// <summary>
/// Wire format spoken by the injected hook: "PDG1", family (4|6), port (big endian), address bytes.
/// The engine answers with a single byte: 0 = tunnel ready, anything else = failure.
/// </summary>
public static class HookProtocol
{
    static readonly byte[] Magic = "PDG1"u8.ToArray();

    public static byte[] Encode(Destination d)
    {
        var addr = d.Address!.GetAddressBytes();
        var buf = new byte[7 + addr.Length];
        Magic.CopyTo(buf, 0);
        buf[4] = (byte)(addr.Length == 4 ? 4 : 6);
        buf[5] = (byte)(d.Port >> 8);
        buf[6] = (byte)d.Port;
        addr.CopyTo(buf, 7);
        return buf;
    }

    public static async Task<Destination> ReadAsync(Stream s, CancellationToken ct)
    {
        var first = new byte[1];
        await s.ReadExactlyAsync(first, ct);
        return await ReadAsync(s, first[0], ct);
    }

    /// <summary>Reads the rest of the header when the first byte was already consumed to tell protocols apart.</summary>
    public static async Task<Destination> ReadAsync(Stream s, byte firstByte, CancellationToken ct)
    {
        var head = new byte[7];
        head[0] = firstByte;
        await s.ReadExactlyAsync(head.AsMemory(1), ct);
        if (!head.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException("Not a ProxyDog hook connection.");

        var len = head[4] switch
        {
            4 => 4,
            6 => 16,
            _ => throw new InvalidDataException("Unknown address family."),
        };
        var addr = new byte[len];
        await s.ReadExactlyAsync(addr, ct);
        return new Destination(new IPAddress(addr), (ushort)((head[5] << 8) | head[6]));
    }
}

public sealed class ProxyHandshakeException(string message) : IOException(message);

/// <summary>Client side of RFC 1928 (CONNECT only) with RFC 1929 username/password.</summary>
public static class Socks5Client
{
    public static async Task ConnectAsync(
        Stream s, Destination dest, string? user, string? pass, CancellationToken ct)
    {
        var useAuth = !string.IsNullOrEmpty(user);
        await s.WriteAsync(useAuth ? new byte[] { 5, 2, 0, 2 } : new byte[] { 5, 1, 0 }, ct);

        var choice = new byte[2];
        await s.ReadExactlyAsync(choice, ct);
        if (choice[0] != 5)
            throw new ProxyHandshakeException("The server did not answer as a SOCKS5 proxy.");
        switch (choice[1])
        {
            case 0:
                break;
            case 2 when useAuth:
                await AuthenticateAsync(s, user!, pass ?? "", ct);
                break;
            case 0xFF:
                throw new ProxyHandshakeException("The SOCKS5 proxy accepts none of the offered login methods.");
            default:
                throw new ProxyHandshakeException($"The SOCKS5 proxy requires an unsupported login method ({choice[1]}).");
        }

        byte[] addr;
        byte atyp;
        if (dest.Domain is { } domain)
        {
            var name = Encoding.ASCII.GetBytes(domain);
            if (name.Length > 255)
                throw new ProxyHandshakeException("Host name is longer than 255 bytes.");
            atyp = 3;
            addr = [(byte)name.Length, .. name];
        }
        else
        {
            addr = dest.Address!.GetAddressBytes();
            atyp = (byte)(addr.Length == 4 ? 1 : 4);
        }
        var req = new byte[6 + addr.Length];
        req[0] = 5;
        req[1] = 1;
        req[3] = atyp;
        addr.CopyTo(req, 4);
        req[^2] = (byte)(dest.Port >> 8);
        req[^1] = (byte)dest.Port;
        await s.WriteAsync(req, ct);

        var reply = new byte[4];
        await s.ReadExactlyAsync(reply, ct);
        if (reply[1] != 0)
            throw new ProxyHandshakeException($"SOCKS5 proxy refused the connection: {Describe(reply[1])}.");

        var skip = reply[3] switch
        {
            1 => 4 + 2,
            4 => 16 + 2,
            3 => (await ReadByteAsync(s, ct)) + 2,
            _ => throw new ProxyHandshakeException("Malformed SOCKS5 reply."),
        };
        await s.ReadExactlyAsync(new byte[skip], ct);
    }

    static async Task AuthenticateAsync(Stream s, string user, string pass, CancellationToken ct)
    {
        var u = Encoding.UTF8.GetBytes(user);
        var p = Encoding.UTF8.GetBytes(pass);
        if (u.Length > 255 || p.Length > 255)
            throw new ProxyHandshakeException("SOCKS5 username or password is longer than 255 bytes.");

        var msg = new byte[3 + u.Length + p.Length];
        msg[0] = 1;
        msg[1] = (byte)u.Length;
        u.CopyTo(msg, 2);
        msg[2 + u.Length] = (byte)p.Length;
        p.CopyTo(msg, 3 + u.Length);
        await s.WriteAsync(msg, ct);

        var status = new byte[2];
        await s.ReadExactlyAsync(status, ct);
        if (status[1] != 0)
            throw new ProxyHandshakeException("The SOCKS5 proxy rejected the username or password.");
    }

    static async Task<int> ReadByteAsync(Stream s, CancellationToken ct)
    {
        var b = new byte[1];
        await s.ReadExactlyAsync(b, ct);
        return b[0];
    }

    static string Describe(byte code) => code switch
    {
        1 => "general failure",
        2 => "not allowed by ruleset",
        3 => "network unreachable",
        4 => "host unreachable",
        5 => "connection refused",
        6 => "TTL expired",
        7 => "command not supported",
        8 => "address type not supported",
        _ => $"error {code}",
    };
}

/// <summary>HTTP proxy tunnelling with CONNECT and optional Basic authentication.</summary>
public static class HttpConnectClient
{
    public static async Task ConnectAsync(
        Stream s, Destination dest, string? user, string? pass, CancellationToken ct)
    {
        var target = dest.ToString();
        var sb = new StringBuilder()
            .Append($"CONNECT {target} HTTP/1.1\r\n")
            .Append($"Host: {target}\r\n");
        if (!string.IsNullOrEmpty(user))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
            sb.Append($"Proxy-Authorization: Basic {token}\r\n");
        }
        sb.Append("\r\n");
        await s.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);

        // Read byte by byte so nothing past the header block (tunnel data) is consumed.
        var header = new StringBuilder();
        var one = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (header.Length > 8192)
                throw new ProxyHandshakeException("HTTP proxy sent an oversized response header.");
            await s.ReadExactlyAsync(one, ct);
            header.Append((char)one[0]);
        }

        var statusLine = header.ToString().Split("\r\n")[0];
        var parts = statusLine.Split(' ', 3);
        if (parts.Length < 2 || !int.TryParse(parts[1], out var code))
            throw new ProxyHandshakeException("The server did not answer as an HTTP proxy.");
        if (code == 407)
            throw new ProxyHandshakeException("The HTTP proxy requires valid credentials (407).");
        if (code is < 200 or > 299)
            throw new ProxyHandshakeException($"HTTP proxy refused the tunnel: {statusLine}");
    }
}

/// <summary>Server side of SOCKS5 CONNECT, for apps (Chromium, Electron) that take a --proxy-server flag.</summary>
public static class InboundSocks5
{
    /// <summary>The version byte (5) has already been read. Returns the target and a reply callback.</summary>
    public static async Task<(Destination Dest, Func<bool, Task> Reply)> AcceptAsync(Stream s, CancellationToken ct)
    {
        var count = new byte[1];
        await s.ReadExactlyAsync(count, ct);
        await s.ReadExactlyAsync(new byte[count[0]], ct);
        await s.WriteAsync(new byte[] { 5, 0 }, ct); // no authentication: loopback only

        var req = new byte[4];
        await s.ReadExactlyAsync(req, ct);
        if (req[0] != 5 || req[1] != 1)
        {
            await s.WriteAsync(Reply(7), ct);
            throw new ProxyHandshakeException("Only SOCKS5 CONNECT is supported.");
        }

        Destination dest;
        switch (req[3])
        {
            case 1 or 4:
                var ip = new byte[req[3] == 1 ? 4 : 16];
                await s.ReadExactlyAsync(ip, ct);
                dest = new Destination(new IPAddress(ip), await ReadPortAsync(s, ct));
                break;
            case 3:
                var len = new byte[1];
                await s.ReadExactlyAsync(len, ct);
                var name = new byte[len[0]];
                await s.ReadExactlyAsync(name, ct);
                dest = Destination.ForDomain(Encoding.ASCII.GetString(name), await ReadPortAsync(s, ct));
                break;
            default:
                await s.WriteAsync(Reply(8), ct);
                throw new ProxyHandshakeException("Unsupported SOCKS5 address type.");
        }
        return (dest, ok => s.WriteAsync(Reply(ok ? (byte)0 : (byte)5), CancellationToken.None).AsTask());
    }

    static async Task<ushort> ReadPortAsync(Stream s, CancellationToken ct)
    {
        var p = new byte[2];
        await s.ReadExactlyAsync(p, ct);
        return (ushort)((p[0] << 8) | p[1]);
    }

    static byte[] Reply(byte code) => [5, code, 0, 1, 0, 0, 0, 0, 0, 0];
}
