using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace ProxyDog.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        if (OperatingSystem.IsMacOS())
            UseMacChrome();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is ViewModels.MainViewModel vm)
            {
                vm.PickApplication = PickApplicationAsync;
                vm.Update.CopyToClipboard = async text =>
                {
                    if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
                };
            }
        };
    }

    // Full-size content under a transparent title bar, with a translucent sidebar like Finder or Settings.
    void UseMacChrome()
    {
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
        ExtendClientAreaTitleBarHeightHint = -1;
        TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur];
        Classes.Add("vibrant");
        // An unfocused macOS window loses its blur; swap to a solid sidebar so nothing washes out.
        Activated += (_, _) => Classes.Set("inactive", false);
        Deactivated += (_, _) => Classes.Set("inactive", true);
    }

    async Task<string?> PickApplicationAsync()
    {
        if (OperatingSystem.IsMacOS())
            return await MacAppPicker.PickAsync();

        var options = new FilePickerOpenOptions
        {
            Title = "Choose the application to proxy",
            AllowMultiple = false,
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync("/usr/share/applications"),
            FileTypeFilter =
            [
                new FilePickerFileType("Applications and programs") { Patterns = ["*.desktop"] },
                FilePickerFileTypes.All,
            ],
        };
        var files = await StorageProvider.OpenFilePickerAsync(options);
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
}
