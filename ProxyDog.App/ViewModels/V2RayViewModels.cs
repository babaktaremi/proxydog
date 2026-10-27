using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDog.Core;
using ProxyDog.Engine;

namespace ProxyDog.UI.ViewModels;

/// <summary>A saved profile as the list shows it.</summary>
public sealed class V2RayProfileItem
{
    public V2RayProfileItem(V2RayProfile profile)
    {
        Profile = profile;
        if (V2RayLink.TryParse(profile.Link, out var endpoint, out var error))
        {
            Endpoint = endpoint;
            Summary = endpoint.Summary;
        }
        else
        {
            Summary = "Unreadable link: " + error;
        }
    }

    public V2RayProfile Profile { get; }
    public V2RayEndpoint? Endpoint { get; }
    public Guid Id => Profile.Id;
    public string Name => Profile.Name;
    public string Summary { get; }
}

/// <summary>Whether an Xray core is available, and the one-click download when it is not.</summary>
public sealed partial class V2RayCoreViewModel : ObservableObject
{
    readonly V2RayCoreInstaller? installer;

    public V2RayCoreViewModel(V2RayCoreInstaller? installer = null)
    {
        this.installer = installer;
        Refresh();
    }

    [ObservableProperty] string? path;
    [ObservableProperty] string status = "";
    [ObservableProperty] bool isBusy;

    public bool IsInstalled => Path is not null;
    public bool CanDownload => installer is not null && !IsInstalled && !IsBusy && V2RayCoreInstaller.CurrentAssetName() is not null;

    partial void OnPathChanged(string? value)
    {
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(CanDownload));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanDownload));

    public void Refresh()
    {
        Path = V2RayCoreLocator.Find();
        Status = Path is null
            ? "V2Ray profiles need the Xray core, which is not installed."
            : $"Using {Path}";
    }

    [RelayCommand]
    async Task Download()
    {
        if (installer is null || IsBusy) return;
        IsBusy = true;
        try
        {
            var progress = new Progress<string>(message => Status = message);
            Path = await installer.InstallAsync(V2RayCoreLocator.UserCoreDirectory, progress);
            Status = $"Installed to {Path}";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException
                                       or PlatformNotSupportedException or TaskCanceledException)
        {
            Status = "Could not install the core: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
