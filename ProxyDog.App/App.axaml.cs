using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using ProxyDog.Core;
using ProxyDog.Engine;
using ProxyDog.UI.ViewModels;
using ProxyDog.UI.Views;

namespace ProxyDog.UI;

public sealed class App : Application
{
    MainViewModel? main;
    UpdateViewModel? update;
    MainWindow? window;
    TrayIcon? tray;
    IClassicDesktopStyleApplicationLifetime? desktop;
    bool quitting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
        {
            desktop = d;
            // The app lives in the menu bar / tray; closing the window only hides it.
            d.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            d.ShutdownRequested += (_, _) => StopAllBlocking();

            update = new UpdateViewModel(
                new UpdateChecker(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }),
                new SettingsStore(SettingsStore.DefaultPath),
                AppInfo.Version,
                InstallDetector.Detect(),
                AppInfo.Notify);
            main = new MainViewModel(new RuleStore(RuleStore.DefaultPath), new RecentProxyStore(RecentProxyStore.DefaultPath), update,
                new V2RayProfileStore(V2RayProfileStore.DefaultPath),
                new V2RayCoreViewModel(new V2RayCoreInstaller(new HttpClient { Timeout = TimeSpan.FromMinutes(3) })));
            window = new MainWindow { DataContext = main };
            window.Closing += (_, e) =>
            {
                if (quitting) return;
                e.Cancel = true;
                window.Hide();
            };
            d.MainWindow = window;

            if (Environment.GetEnvironmentVariable("PROXYDOG_DEBUG") == "1")
                Console.Error.WriteLine($"ProxyDog: before: {MacStatusItem.AppNames()}");
            MacStatusItem.SetAppName("ProxyDog");
            if (Environment.GetEnvironmentVariable("PROXYDOG_DEBUG") == "1")
                Console.Error.WriteLine($"ProxyDog: after:  {MacStatusItem.AppNames()}");
            using (var dock = AssetLoader.Open(new Uri("avares://ProxyDog/Assets/AppIcon.png")))
                MacDockIcon.Set(dock);
            // The app menu's About item opens Avalonia's own About box; show ProxyDog's instead.
            Window.WindowOpenedEvent.AddClassHandler<Window>((opened, _) =>
            {
                if (opened.GetType().FullName != "Avalonia.Dialogs.AboutAvaloniaDialog") return;
                // Closing here, inside Avalonia's own Show, makes it throw while it finishes setting the window up.
                // Hide it now and close it once Show has returned.
                opened.Opacity = 0;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    opened.Close();
                    ShowAbout();
                });
            });

            CreateTray();
            main.RulesChanged += RebuildMenu;
            update.Changed += RebuildMenu;

            // Look for a new release shortly after start (not in the way of launching), then every 12 hours.
            Avalonia.Threading.DispatcherTimer.RunOnce(() => _ = update.CheckAsync(manual: false), TimeSpan.FromSeconds(8));
            var recheck = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(12) };
            recheck.Tick += (_, _) => _ = update.CheckAsync(manual: false);
            recheck.Start();
            RebuildMenu();
        }
        base.OnFrameworkInitializationCompleted();
    }

    void CreateTray()
    {
        // macOS wants a one-colour template glyph in the menu bar; other desktops get the coloured icon.
        var asset = OperatingSystem.IsMacOS() ? "TrayIcon.png" : "proxydog.png";
        using var stream = AssetLoader.Open(new Uri($"avares://ProxyDog/Assets/{asset}"));
        tray = new TrayIcon
        {
            Icon = new WindowIcon(stream),
            ToolTipText = "ProxyDog",
            Menu = new NativeMenu(),
        };
        tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(this, [tray]);
        if (OperatingSystem.IsMacOS())
        {
            // The status item can appear a little after the tray is attached (slower on a cold or debug start), and
            // AppKit may swap its image later. Keep the template flag applied: it is a cheap check every couple of
            // seconds, and without the flag a black glyph is invisible on a dark menu bar.
            var keepTemplate = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            keepTemplate.Tick += (_, _) =>
            {
                MacStatusItem.MarkTemplate();
                MacStatusItem.RenameAboutItem("ProxyDog");
            };
            keepTemplate.Start();
            Avalonia.Threading.DispatcherTimer.RunOnce(() => MacStatusItem.MarkTemplate(), TimeSpan.FromMilliseconds(300));

            if (Environment.GetEnvironmentVariable("PROXYDOG_DEBUG") == "1")
            {
                var watch = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                watch.Tick += (_, _) => Console.Error.WriteLine($"ProxyDog: {DateTime.Now:HH:mm:ss} menu bar rendering: {MacStatusItem.DescribeRendering()}; app menu: {MacStatusItem.DescribeMainMenu()}");
                watch.Start();
            }
        }
    }

    void RebuildMenu()
    {
        if (tray is null || main is null) return;
        // Reuse the one NativeMenu: the macOS exporter rejects a replacement object.
        var menu = tray.Menu ??= new NativeMenu();
        menu.Items.Clear();

        if (update?.Release is { } release)
        {
            var available = new NativeMenuItem($"Update Available: {release.Version}…");
            available.Click += (_, _) => update.ViewReleaseCommand.Execute(null);
            menu.Add(available);
            menu.Add(new NativeMenuItemSeparator());
        }

        var show = new NativeMenuItem("Show ProxyDog");
        show.Click += (_, _) => ShowWindow();
        menu.Add(show);
        menu.Add(new NativeMenuItemSeparator());

        if (main.Rules.Count == 0)
        {
            menu.Add(new NativeMenuItem("No rules yet") { IsEnabled = false });
        }
        foreach (var rule in main.Rules)
        {
            var item = new NativeMenuItem(rule.Title)
            {
                ToggleType = NativeMenuItemToggleType.CheckBox,
                IsChecked = rule.IsOn,
            };
            var captured = rule;
            item.Click += (_, _) => captured.IsOn = !captured.IsOn;
            menu.Add(item);
        }

        menu.Add(new NativeMenuItemSeparator());
        if (update is not null)
        {
            var check = new NativeMenuItem("Check for Updates…");
            check.Click += (_, _) =>
            {
                ShowAbout(); // the About window shows the result
                _ = update.CheckAsync(manual: true);
            };
            menu.Add(check);
            var auto = new NativeMenuItem("Check Automatically")
            {
                ToggleType = NativeMenuItemToggleType.CheckBox,
                IsChecked = update.AutoCheck,
            };
            auto.Click += (_, _) => update.AutoCheck = !update.AutoCheck;
            menu.Add(auto);
            menu.Add(new NativeMenuItemSeparator());
        }
        var quit = new NativeMenuItem("Quit ProxyDog");
        quit.Click += async (_, _) => await QuitAsync();
        menu.Add(quit);

        var active = main.Rules.Count(r => r.IsOn);
        tray.ToolTipText = active == 0 ? "ProxyDog: all rules off" : $"ProxyDog: {active} rule{(active == 1 ? "" : "s")} on";
    }

    AboutWindow? about;

    void ShowAbout()
    {
        if (about is not null)
        {
            about.Activate();
            return;
        }
        about = new AboutWindow(update);
        about.Closed += (_, _) => about = null;
        about.Show();
    }

    void ShowWindow()
    {
        if (window is null) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    async Task QuitAsync()
    {
        quitting = true;
        if (main is not null)
            await main.ShutdownAsync();
        desktop?.Shutdown();
    }

    void StopAllBlocking()
    {
        quitting = true;
        try { main?.ShutdownAsync().Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { /* exiting anyway; hooked apps fail closed once the engine is gone */ }
    }
}
