using CommunityToolkit.Mvvm.ComponentModel;

namespace ProxyDog.UI.ViewModels;

/// <summary>Anything shown as a tab: the rules page or one rule's connection log.</summary>
public abstract class TabViewModel : ObservableObject
{
    public abstract string Title { get; }

    /// <summary>True when the tab's rule is switched on (drives the dot in the tab header).</summary>
    public virtual bool IsActive => false;

    /// <summary>Whether the header shows the on/off dot (rule tabs only).</summary>
    public virtual bool ShowsStatus => false;
}

public sealed class RulesPageViewModel(MainViewModel main) : TabViewModel
{
    public MainViewModel Main { get; } = main;
    public override string Title => "All Rules";
}
