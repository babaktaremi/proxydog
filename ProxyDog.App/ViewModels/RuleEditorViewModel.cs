using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDog.Core;

namespace ProxyDog.UI.ViewModels;

public sealed partial class RuleEditorViewModel : ObservableObject
{
    readonly MainViewModel main;
    readonly ProxyRule? existing;

    public RuleEditorViewModel(MainViewModel main, ProxyRule? existing)
    {
        this.main = main;
        this.existing = existing;
        var r = existing ?? new ProxyRule { Kind = ProxyKind.Socks5, Port = 1080 };
        name = r.Name;
        kindIndex = (int)r.Kind;
        host = r.Host;
        portText = r.Port == 0 ? "" : r.Port.ToString();
        username = r.Username ?? "";
        password = r.Password ?? "";
        appPath = r.AppPath;
        arguments = r.Arguments ?? "";
        whenRunningIndex = (int)r.IfRunning;
        killSwitch = r.KillSwitch;
        Heading = existing is null ? "New rule" : "Edit rule";
        Recents = main.RecentProxies();

        selectedProfile = main.FindV2RayProfile(r.V2RayProfileId);
        main.V2RayProfiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasProfiles));
            OnPropertyChanged(nameof(NoProfiles));
        };
    }

    // ----- V2Ray: pick one of the saved profiles, or add one -----

    public ObservableCollection<V2RayProfileItem> Profiles => main.V2RayProfiles;
    public V2RayCoreViewModel Core => main.V2RayCore;
    public bool HasProfiles => Profiles.Count > 0;
    public bool NoProfiles => Profiles.Count == 0;

    /// <summary>The proxy section shows host, port and login for SOCKS5 and HTTP, and the profile list for V2Ray.</summary>
    public bool IsV2Ray => KindIndex == 2;
    public bool IsManual => KindIndex != 2;
    public bool ShowRecents => HasRecents && IsManual;

    [ObservableProperty] V2RayProfileItem? selectedProfile;
    [ObservableProperty] bool isAddingProfile;
    [ObservableProperty] string newLink = "";
    [ObservableProperty] string newName = "";
    [ObservableProperty] string? profileError;

    [RelayCommand]
    void ShowAddProfile()
    {
        NewLink = "";
        NewName = "";
        ProfileError = null;
        IsAddingProfile = true;
    }

    [RelayCommand]
    void CancelAddProfile()
    {
        IsAddingProfile = false;
        ProfileError = null;
    }

    [RelayCommand]
    void AddProfile()
    {
        var item = main.AddV2RayProfile(NewLink, NewName, out var error);
        if (item is null)
        {
            ProfileError = error;
            return;
        }
        SelectedProfile = item;
        IsAddingProfile = false;
        ProfileError = null;
        Error = null;
    }

    [RelayCommand]
    void RemoveProfile()
    {
        if (SelectedProfile is not { } item) return;
        if (main.RemoveV2RayProfile(item, out var error))
        {
            SelectedProfile = null;
            ProfileError = null;
        }
        else
        {
            ProfileError = error;
        }
    }

    public string Heading { get; }
    public IReadOnlyList<RecentProxy> Recents { get; }
    public bool HasRecents => Recents.Count > 0;

    [ObservableProperty] RecentProxy? selectedRecent;

    // Picking a recent proxy fills the fields, then the box clears so it can be used again.
    partial void OnSelectedRecentChanged(RecentProxy? value)
    {
        if (value is null) return;
        KindIndex = (int)value.Kind;
        Host = value.Host;
        PortText = value.Port.ToString();
        Username = value.Username ?? "";
        Password = value.Password ?? "";
        Error = null;
        Dispatcher.UIThread.Post(() => SelectedRecent = null);
    }

    [ObservableProperty] string name;
    [ObservableProperty] int kindIndex;
    [ObservableProperty] string host;
    [ObservableProperty] string portText;
    [ObservableProperty] string username;
    [ObservableProperty] string password;
    [ObservableProperty] string appPath;
    [ObservableProperty] string arguments;
    [ObservableProperty] int whenRunningIndex;
    [ObservableProperty] bool killSwitch;
    [ObservableProperty] string? error;

    public string AppLabel => string.IsNullOrEmpty(AppPath) ? "No application chosen" : AppPath;

    partial void OnAppPathChanged(string value) => OnPropertyChanged(nameof(AppLabel));

    partial void OnKindIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsV2Ray));
        OnPropertyChanged(nameof(IsManual));
        OnPropertyChanged(nameof(ShowRecents));
        if (value == 2) return;

        // Swap the placeholder port only when the user has not typed their own.
        if (PortText is "" or "1080" or "8080")
            PortText = value == 0 ? "1080" : "8080";
    }

    [RelayCommand]
    async Task ChooseApp()
    {
        if (main.PickApplication is null) return;
        var picked = await main.PickApplication();
        if (picked is not null)
        {
            AppPath = picked;
            Error = null;
        }
    }

    [RelayCommand]
    void Cancel() => main.CloseEditor();

    [RelayCommand]
    Task Save()
    {
        var port = 0;
        if (IsV2Ray)
        {
            if (SelectedProfile is null)
                return Fail(HasProfiles ? "Choose a V2Ray profile." : "Add a V2Ray profile first: paste a vless:// or vmess:// link.");
            if (SelectedProfile.Endpoint is null)
                return Fail($"That profile cannot be used: {SelectedProfile.Summary}");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(Host))
                return Fail("Enter the proxy host name or address.");
            if (!int.TryParse(PortText, out port) || port is < 1 or > 65535)
                return Fail("The proxy port must be a number from 1 to 65535.");
        }
        if (string.IsNullOrEmpty(AppPath))
            return Fail("Choose the application whose traffic should use this proxy.");
        if (!File.Exists(AppPath) && !Directory.Exists(AppPath))
            return Fail("That application no longer exists. Choose it again.");

        if (Directory.Exists(AppPath) && !AppPath.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            return Fail("That is a folder, not an application. Choose an .app.");
        if (OperatingSystem.IsMacOS() && File.Exists(AppPath)
            && (File.GetUnixFileMode(AppPath) & UnixFileMode.UserExecute) == 0)
            return Fail("That file is not a program. Choose an .app or an executable.");

        var rule = existing?.Clone() ?? new ProxyRule();
        rule.Kind = KindIndex switch { 0 => ProxyKind.Socks5, 1 => ProxyKind.Http, _ => ProxyKind.V2Ray };
        if (IsV2Ray)
        {
            rule.Host = "";
            rule.Port = 0;
            rule.Username = rule.Password = null;
            rule.V2RayProfileId = SelectedProfile!.Id;
            rule.V2RayProfileName = SelectedProfile.Name;
        }
        else
        {
            rule.Host = Host.Trim();
            rule.Port = port;
            rule.Username = string.IsNullOrEmpty(Username) ? null : Username;
            rule.Password = string.IsNullOrEmpty(Username) ? null : Password;
            rule.V2RayProfileId = null;
            rule.V2RayProfileName = null;
        }
        rule.AppPath = AppPath;
        rule.Arguments = string.IsNullOrWhiteSpace(Arguments) ? null : Arguments.Trim();
        rule.KillSwitch = KillSwitch;
        rule.IfRunning = WhenRunningIndex == 1 ? RunningAppPolicy.Restart : RunningAppPolicy.LeaveRunning;
        var appName = Path.GetFileNameWithoutExtension(AppPath.TrimEnd('/', '\\'));
        rule.Name = string.IsNullOrWhiteSpace(Name) ? $"{appName} via {(IsV2Ray ? rule.V2RayProfileName : rule.Host)}" : Name.Trim();

        return main.SaveRuleAsync(rule, existing is null);

        Task Fail(string message)
        {
            Error = message;
            return Task.CompletedTask;
        }
    }
}
