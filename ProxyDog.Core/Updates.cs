using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProxyDog.Core;

/// <summary>A semantic version (https://semver.org): 1.2.3, optionally with a -prerelease tag. Build metadata is ignored.</summary>
public readonly record struct SemVer(int Major, int Minor, int Patch, string? Pre = null) : IComparable<SemVer>
{
    public static bool TryParse(string? text, out SemVer version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text[0] is 'v' or 'V') text = text[1..];
        var plus = text.IndexOf('+');
        if (plus >= 0) text = text[..plus];
        string? pre = null;
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            pre = text[(dash + 1)..];
            text = text[..dash];
            if (pre.Length == 0) return false;
        }
        var parts = text.Split('.');
        if (parts.Length != 3
            || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor) || !int.TryParse(parts[2], out var patch)
            || major < 0 || minor < 0 || patch < 0)
            return false;
        version = new SemVer(major, minor, patch, pre);
        return true;
    }

    public static SemVer Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"'{text}' is not a semantic version.");

    public int CompareTo(SemVer other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        // A version with a prerelease tag is older than the same version without one.
        if (Pre is null) return other.Pre is null ? 0 : 1;
        if (other.Pre is null) return -1;
        return ComparePre(Pre, other.Pre);
    }

    static int ComparePre(string a, string b)
    {
        var x = a.Split('.');
        var y = b.Split('.');
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            var xn = int.TryParse(x[i], out var xi);
            var yn = int.TryParse(y[i], out var yi);
            var c = xn && yn ? xi.CompareTo(yi)
                  : xn ? -1            // numeric identifiers sort before text ones
                  : yn ? 1
                  : string.CompareOrdinal(x[i], y[i]);
            if (c != 0) return c;
        }
        return x.Length.CompareTo(y.Length);
    }

    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Pre is null ? "" : $"-{Pre}");
}

public sealed record ReleaseInfo(SemVer Version, string Tag, string Url, string? Notes);

public enum UpdateCheckStatus { UpToDate, Available, NoReleases, Failed }

public sealed record UpdateCheckResult(UpdateCheckStatus Status, ReleaseInfo? Release = null, string? Error = null);

/// <summary>Asks GitHub for the latest published (non-draft, non-prerelease) release and compares it with this build.</summary>
public sealed class UpdateChecker(HttpClient http, string repository = "babaktaremi/proxydog")
{
    public string ReleasesPage => $"https://github.com/{repository}/releases";

    public async Task<UpdateCheckResult> CheckAsync(SemVer current, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ProxyDog", current.ToString()));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new UpdateCheckResult(UpdateCheckStatus.NoReleases);
            if (!response.IsSuccessStatusCode)
                return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: $"GitHub answered {(int)response.StatusCode}.");

            var dto = await JsonSerializer.DeserializeAsync<ReleaseDto>(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (dto?.TagName is null || !SemVer.TryParse(dto.TagName, out var latest))
                return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: "The latest release has no version number I can read.");

            if (latest <= current)
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate);
            return new UpdateCheckResult(UpdateCheckStatus.Available,
                new ReleaseInfo(latest, dto.TagName, dto.HtmlUrl ?? ReleasesPage, dto.Body));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: ex is TaskCanceledException ? "The update check timed out." : ex.Message);
        }
    }

    sealed class ReleaseDto
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
    }
}

public enum InstallMethod { Manual, Homebrew, Snap, Pacman }

/// <summary>Works out how ProxyDog was installed, so the update alert can show the right command.</summary>
public static class InstallDetector
{
    public static InstallMethod Detect(string baseDirectory, Func<string, bool> pathExists, Func<string, string?> env)
    {
        if (!string.IsNullOrEmpty(env("SNAP")) || baseDirectory.StartsWith("/snap/", StringComparison.Ordinal))
            return InstallMethod.Snap;
        // A cask copies the app into /Applications but leaves a record under Caskroom.
        if (pathExists("/opt/homebrew/Caskroom/proxydog") || pathExists("/usr/local/Caskroom/proxydog")
            || baseDirectory.Contains("/Cellar/proxydog", StringComparison.Ordinal)
            || baseDirectory.Contains("/linuxbrew/", StringComparison.Ordinal))
            return InstallMethod.Homebrew;
        if (baseDirectory.StartsWith("/usr/lib/proxydog", StringComparison.Ordinal) || baseDirectory.StartsWith("/opt/proxydog", StringComparison.Ordinal))
            return InstallMethod.Pacman;
        return InstallMethod.Manual;
    }

    public static InstallMethod Detect() =>
        Detect(AppContext.BaseDirectory, p => Directory.Exists(p) || File.Exists(p), Environment.GetEnvironmentVariable);

    /// <summary>The command a user runs to update, or null when there is no package manager to ask.</summary>
    public static string? UpdateCommand(InstallMethod method) => method switch
    {
        InstallMethod.Homebrew => "brew update && brew upgrade --cask proxydog",
        InstallMethod.Snap => "sudo snap refresh proxydog",
        InstallMethod.Pacman => "yay -Syu proxydog-bin",
        _ => null,
    };
}

/// <summary>Small per-user preferences kept next to the rules file.</summary>
public sealed class AppSettings
{
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>A version the user chose to ignore: no alert until a newer one appears.</summary>
    public string? SkippedVersion { get; set; }

    /// <summary>The newest version a system notification was already shown for, so it fires once per release.</summary>
    public string? NotifiedVersion { get; set; }
}

public sealed class SettingsStore(string path)
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(Path.GetDirectoryName(RuleStore.DefaultPath)!, "settings.json");

    public AppSettings Load()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings()
                : new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings(); // preferences only; start fresh
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        File.Move(temp, path, overwrite: true);
    }
}
