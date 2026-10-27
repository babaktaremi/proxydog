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
        if (string.IsNullOrWhiteSpace(Host))
            return Fail("Enter the proxy host name or address.");
        if (!int.TryParse(PortText, out var port) || port is < 1 or > 65535)
            return Fail("The proxy port must be a number from 1 to 65535.");
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
        rule.Kind = KindIndex == 0 ? ProxyKind.Socks5 : ProxyKind.Http;
        rule.Host = Host.Trim();
        rule.Port = port;
        rule.Username = string.IsNullOrEmpty(Username) ? null : Username;
        rule.Password = string.IsNullOrEmpty(Username) ? null : Password;
        rule.AppPath = AppPath;
        rule.Arguments = string.IsNullOrWhiteSpace(Arguments) ? null : Arguments.Trim();
        rule.KillSwitch = KillSwitch;
        rule.IfRunning = WhenRunningIndex == 1 ? RunningAppPolicy.Restart : RunningAppPolicy.LeaveRunning;
        var appName = Path.GetFileNameWithoutExtension(AppPath.TrimEnd('/', '\\'));
        rule.Name = string.IsNullOrWhiteSpace(Name) ? $"{appName} via {rule.Host}" : Name.Trim();

        return main.SaveRuleAsync(rule, existing is null);

        Task Fail(string message)
        {
            Error = message;
            return Task.CompletedTask;
        }
    }
}
