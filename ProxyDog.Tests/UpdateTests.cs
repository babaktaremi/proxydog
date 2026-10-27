using System.Net;
using System.Text;
using ProxyDog.Core;

namespace ProxyDog.Tests;

public class SemVerTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, null)]
    [InlineData("v1.2.3", 1, 2, 3, null)]
    [InlineData("V10.0.1", 10, 0, 1, null)]
    [InlineData("1.0.0-rc.1", 1, 0, 0, "rc.1")]
    [InlineData("1.0.0+abc123", 1, 0, 0, null)]
    [InlineData("1.0.0-beta.2+build.5", 1, 0, 0, "beta.2")]
    public void Parses(string text, int major, int minor, int patch, string? pre) =>
        Assert.Equal(new SemVer(major, minor, patch, pre), SemVer.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("one.two.three")]
    [InlineData("1.0.0-")]
    [InlineData("-1.0.0")]
    public void Rejects_what_is_not_a_version(string text) => Assert.False(SemVer.TryParse(text, out _));

    [Theory]
    [InlineData("1.0.1", "1.0.0")]
    [InlineData("1.1.0", "1.0.9")]
    [InlineData("2.0.0", "1.99.99")]
    [InlineData("1.0.0", "1.0.0-rc.1")]          // a release beats its own prerelease
    [InlineData("1.0.0-rc.2", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.10", "1.0.0-rc.9")]    // numeric parts compare as numbers, not text
    [InlineData("1.0.0-rc", "1.0.0-beta")]
    [InlineData("1.0.0-rc.1", "1.0.0-rc")]       // more identifiers win when the prefix is equal
    public void Orders_by_semver(string newer, string older)
    {
        Assert.True(SemVer.Parse(newer) > SemVer.Parse(older));
        Assert.True(SemVer.Parse(older) < SemVer.Parse(newer));
    }

    [Fact]
    public void Build_metadata_does_not_change_the_order() =>
        Assert.Equal(0, SemVer.Parse("1.2.3+aaa").CompareTo(SemVer.Parse("1.2.3+zzz")));
}

public class UpdateCheckerTests
{
    sealed class Fake(HttpStatusCode status, string body, Exception? throws = null) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen = request;
            if (throws is not null) throw throws;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    static (UpdateChecker Checker, Fake Handler) Make(HttpStatusCode status, string body, Exception? throws = null)
    {
        var handler = new Fake(status, body, throws);
        return (new UpdateChecker(new HttpClient(handler)), handler);
    }

    const string Release = """{"tag_name":"v1.3.0","html_url":"https://github.com/babaktaremi/proxydog/releases/tag/v1.3.0","body":"Faster."}""";

    [Fact]
    public async Task Reports_a_newer_release_with_its_link()
    {
        var (checker, handler) = Make(HttpStatusCode.OK, Release);
        var result = await checker.CheckAsync(SemVer.Parse("1.2.0"));

        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.Equal(SemVer.Parse("1.3.0"), result.Release!.Version);
        Assert.Equal("https://github.com/babaktaremi/proxydog/releases/tag/v1.3.0", result.Release.Url);
        Assert.Equal("Faster.", result.Release.Notes);
        Assert.Equal("https://api.github.com/repos/babaktaremi/proxydog/releases/latest", handler.Seen!.RequestUri!.ToString());
        Assert.Contains("ProxyDog/1.2.0", handler.Seen.Headers.UserAgent.ToString()); // GitHub rejects requests without a User-Agent
    }

    [Theory]
    [InlineData("1.3.0")]   // same version
    [InlineData("2.0.0")]   // running something newer than the latest release
    public async Task Says_up_to_date_when_nothing_newer_exists(string current)
    {
        var (checker, _) = Make(HttpStatusCode.OK, Release);
        Assert.Equal(UpdateCheckStatus.UpToDate, (await checker.CheckAsync(SemVer.Parse(current))).Status);
    }

    [Fact]
    public async Task A_repo_without_releases_is_not_an_error()
    {
        var (checker, _) = Make(HttpStatusCode.NotFound, "{}");
        Assert.Equal(UpdateCheckStatus.NoReleases, (await checker.CheckAsync(SemVer.Parse("1.0.0"))).Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]            // rate limited
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Server_errors_are_reported_not_thrown(HttpStatusCode code)
    {
        var (checker, _) = Make(code, "{}");
        var result = await checker.CheckAsync(SemVer.Parse("1.0.0"));
        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Contains(((int)code).ToString(), result.Error);
    }

    [Fact]
    public async Task An_unreadable_tag_fails_cleanly()
    {
        var (checker, _) = Make(HttpStatusCode.OK, """{"tag_name":"nightly","html_url":"x"}""");
        Assert.Equal(UpdateCheckStatus.Failed, (await checker.CheckAsync(SemVer.Parse("1.0.0"))).Status);
    }

    [Fact]
    public async Task Garbage_and_network_failures_fail_cleanly()
    {
        var (bad, _) = Make(HttpStatusCode.OK, "<html>not json</html>");
        Assert.Equal(UpdateCheckStatus.Failed, (await bad.CheckAsync(SemVer.Parse("1.0.0"))).Status);

        var (offline, _) = Make(HttpStatusCode.OK, "", new HttpRequestException("No such host is known"));
        var result = await offline.CheckAsync(SemVer.Parse("1.0.0"));
        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Contains("No such host", result.Error);
    }
}

public class InstallDetectorTests
{
    static InstallMethod Detect(string baseDir, string[]? existing = null, string? snapEnv = null) =>
        InstallDetector.Detect(baseDir, p => existing?.Contains(p) ?? false, k => k == "SNAP" ? snapEnv : null);

    [Fact]
    public void Homebrew_cask_is_recognised_by_its_caskroom_record() =>
        Assert.Equal(InstallMethod.Homebrew, Detect("/Applications/ProxyDog.app/Contents/MacOS/", ["/opt/homebrew/Caskroom/proxydog"]));

    [Fact]
    public void Intel_homebrew_prefix_counts_too() =>
        Assert.Equal(InstallMethod.Homebrew, Detect("/Applications/ProxyDog.app/Contents/MacOS/", ["/usr/local/Caskroom/proxydog"]));

    [Fact]
    public void Linuxbrew_formula_is_recognised() =>
        Assert.Equal(InstallMethod.Homebrew, Detect("/home/linuxbrew/.linuxbrew/Cellar/proxydog/1.0.0/libexec/"));

    [Theory]
    [InlineData("/snap/proxydog/12/usr/lib/proxydog/", null)]
    [InlineData("/somewhere/", "/snap/proxydog/12")]
    public void Snap_is_recognised_by_path_or_environment(string baseDir, string? snapEnv) =>
        Assert.Equal(InstallMethod.Snap, Detect(baseDir, snapEnv: snapEnv));

    [Fact]
    public void Pacman_install_lives_under_usr_lib() =>
        Assert.Equal(InstallMethod.Pacman, Detect("/usr/lib/proxydog/"));

    [Fact]
    public void A_downloaded_app_is_manual() =>
        Assert.Equal(InstallMethod.Manual, Detect("/Users/me/Downloads/ProxyDog.app/Contents/MacOS/"));

    [Fact]
    public void Each_package_manager_has_its_own_update_command_and_manual_has_none()
    {
        Assert.Contains("brew", InstallDetector.UpdateCommand(InstallMethod.Homebrew));
        Assert.Contains("snap refresh", InstallDetector.UpdateCommand(InstallMethod.Snap));
        Assert.Contains("proxydog", InstallDetector.UpdateCommand(InstallMethod.Pacman));
        Assert.Null(InstallDetector.UpdateCommand(InstallMethod.Manual));
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void Defaults_to_checking_and_round_trips_choices()
    {
        var dir = Directory.CreateTempSubdirectory();
        var store = new SettingsStore(Path.Combine(dir.FullName, "s", "settings.json"));
        Assert.True(store.Load().CheckForUpdates);

        store.Save(new AppSettings { CheckForUpdates = false, SkippedVersion = "1.4.0", NotifiedVersion = "1.4.0" });
        var loaded = store.Load();
        Assert.False(loaded.CheckForUpdates);
        Assert.Equal("1.4.0", loaded.SkippedVersion);
        Assert.Equal("1.4.0", loaded.NotifiedVersion);
        dir.Delete(true);
    }

    [Fact]
    public void A_corrupt_file_falls_back_to_defaults()
    {
        var dir = Directory.CreateTempSubdirectory();
        var path = Path.Combine(dir.FullName, "settings.json");
        File.WriteAllText(path, "{ not json");
        Assert.True(new SettingsStore(path).Load().CheckForUpdates);
        dir.Delete(true);
    }
}
