using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDog.Core;

namespace ProxyDog.UI.ViewModels;

/// <summary>Checks GitHub for a newer release and holds what the banner, tray menu and About window show.</summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    readonly UpdateChecker? checker;
    readonly SettingsStore? store;
    readonly AppSettings settings;
    readonly Action<string, string>? notify;
    bool dismissed;

    public UpdateViewModel(UpdateChecker? checker, SettingsStore? store, SemVer current, InstallMethod method, Action<string, string>? notify = null)
    {
        this.checker = checker;
        this.store = store;
        this.notify = notify;
        Current = current;
        Method = method;
        settings = store?.Load() ?? new AppSettings();
    }

    /// <summary>Used when nothing should be checked (tests, previews).</summary>
    public static UpdateViewModel None => new(null, null, new SemVer(0, 0, 0), InstallMethod.Manual);

    public SemVer Current { get; }
    public InstallMethod Method { get; }

    /// <summary>Set by the window: copies text to the system clipboard.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>Raised when the available release changes, so the tray menu can refresh.</summary>
    public event Action? Changed;

    [ObservableProperty] ReleaseInfo? release;
    [ObservableProperty] bool isChecking;
    [ObservableProperty] string status = "";

    /// <summary>The banner: a release is out and the user has not dismissed it.</summary>
    public bool IsAvailable => Release is not null && !dismissed;
    public string Headline => Release is null ? "" : $"ProxyDog {Release.Version} is available";
    public string Detail => $"You have {Current}.";
    public string? Command => InstallDetector.UpdateCommand(Method);
    public bool HasCommand => Command is not null;
    public string ReleasePage => Release?.Url ?? checker?.ReleasesPage ?? "https://github.com/babaktaremi/proxydog/releases";

    public bool AutoCheck
    {
        get => settings.CheckForUpdates;
        set
        {
            if (settings.CheckForUpdates == value) return;
            settings.CheckForUpdates = value;
            store?.Save(settings);
            OnPropertyChanged();
            Changed?.Invoke();
        }
    }

    partial void OnReleaseChanged(ReleaseInfo? value) => RaiseBanner();

    void RaiseBanner()
    {
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(Headline));
        OnPropertyChanged(nameof(ReleasePage));
        Changed?.Invoke();
    }

    /// <summary>
    /// Asks GitHub. An automatic check stays quiet when the user opted out or skipped that version; a manual one
    /// always answers, and shows a release the user had dismissed.
    /// </summary>
    public async Task CheckAsync(bool manual)
    {
        if (checker is null || IsChecking) return;
        if (!manual && !settings.CheckForUpdates) return;

        IsChecking = true;
        if (manual) Status = "Checking…";
        try
        {
            var result = await checker.CheckAsync(Current);
            switch (result.Status)
            {
                case UpdateCheckStatus.Available:
                    var found = result.Release!;
                    var skipped = settings.SkippedVersion == found.Version.ToString();
                    if (!manual && skipped)
                    {
                        Status = $"Version {found.Version} is available (skipped).";
                        return;
                    }
                    dismissed = false;
                    Release = found;
                    RaiseBanner();
                    Status = $"Version {found.Version} is available.";
                    if (!manual && settings.NotifiedVersion != found.Version.ToString())
                    {
                        settings.NotifiedVersion = found.Version.ToString();
                        store?.Save(settings);
                        notify?.Invoke("ProxyDog update available", $"Version {found.Version} is out (you have {Current}).");
                    }
                    break;
                case UpdateCheckStatus.UpToDate:
                    Release = null;
                    Status = "You're up to date.";
                    break;
                case UpdateCheckStatus.NoReleases:
                    Release = null;
                    Status = "No releases have been published yet.";
                    break;
                default:
                    // A failed automatic check is not worth bothering anyone about.
                    if (manual) Status = $"Couldn't check for updates. {result.Error}";
                    break;
            }
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    Task CheckNow() => CheckAsync(manual: true);

    [RelayCommand]
    void ViewRelease() => AppInfo.OpenUrl(ReleasePage);

    [RelayCommand]
    async Task CopyCommand()
    {
        if (Command is { } command && CopyToClipboard is { } copy)
            await copy(command);
    }

    /// <summary>Hide the banner for now; the tray menu keeps the entry and the next launch asks again.</summary>
    [RelayCommand]
    void Later()
    {
        dismissed = true;
        RaiseBanner();
    }

    /// <summary>Ignore this version entirely: no banner or notification until a newer one is released.</summary>
    [RelayCommand]
    void Skip()
    {
        if (Release is null) return;
        settings.SkippedVersion = Release.Version.ToString();
        store?.Save(settings);
        dismissed = true;
        RaiseBanner();
    }
}
