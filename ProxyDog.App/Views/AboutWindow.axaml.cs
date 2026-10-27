using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ProxyDog.UI.Views;

public partial class AboutWindow : Window
{
    public const string GitHubUrl = "https://github.com/babaktaremi/proxydog";
    public const string ContactEmail = "babak.taremi.dev@gmail.com";

    public AboutWindow() : this(null) { }

    public AboutWindow(ViewModels.UpdateViewModel? update)
    {
        InitializeComponent();
        DataContext = update ?? ViewModels.UpdateViewModel.None;
        VersionLabel.Text = $"Version {AppInfo.VersionText}";
        GitHubLink.Content = GitHubUrl.Replace("https://", "");
        EmailLink.Content = ContactEmail;
    }

    async void OnGitHub(object? sender, RoutedEventArgs e) => await Launcher.LaunchUriAsync(new Uri(GitHubUrl));

    async void OnEmail(object? sender, RoutedEventArgs e) => await Launcher.LaunchUriAsync(new Uri($"mailto:{ContactEmail}"));
}
