using CommunityToolkit.Mvvm.ComponentModel;

namespace ProxyDog.UI.ViewModels;

public sealed partial class LogRowViewModel(Guid id, DateTimeOffset time, string destination) : ObservableObject
{
    public Guid Id { get; } = id;
    public string Time { get; } = time.ToString("HH:mm:ss");
    public string Destination { get; } = destination;

    [ObservableProperty] string status = "Connecting";
    [ObservableProperty] string detail = "";
    [ObservableProperty] bool isFailed;
    [ObservableProperty] bool isOpen;
    public bool IsNotice { get; init; }

    /// <summary>Notices are one sentence, so they use the destination and detail columns together.</summary>
    public int MessageSpan => IsNotice ? 2 : 1;
}
