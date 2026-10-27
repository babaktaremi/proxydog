using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDog.Core;

namespace ProxyDog.UI.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    readonly RuleStore store;
    readonly RecentProxyStore? recents;

    public MainViewModel(RuleStore store, RecentProxyStore? recents = null, UpdateViewModel? update = null)
    {
        Update = update ?? UpdateViewModel.None;
        this.store = store;
        this.recents = recents;
        RulesPage = new RulesPageViewModel(this);
        Tabs.Add(RulesPage);
        foreach (var rule in store.Load())
        {
            var vm = new RuleViewModel(rule, this);
            Rules.Add(vm);
            Tabs.Add(vm);
        }
        selectedTab = RulesPage;
    }

    public UpdateViewModel Update { get; }
    public RulesPageViewModel RulesPage { get; }
    public ObservableCollection<TabViewModel> Tabs { get; } = [];
    public ObservableCollection<RuleViewModel> Rules { get; } = [];

    [ObservableProperty] TabViewModel? selectedTab;
    [ObservableProperty] RuleEditorViewModel? editor;

    public bool HasRules => Rules.Count > 0;
    public bool IsEmpty => Rules.Count == 0 && Editor is null;
    public bool IsEditing => Editor is not null;
    public bool ShowList => Rules.Count > 0 && Editor is null;

    /// <summary>Set by the window: opens the OS file picker and returns the chosen path.</summary>
    public Func<Task<string?>>? PickApplication { get; set; }

    /// <summary>Raised whenever rules are added, removed, renamed or switched, so the tray menu can refresh.</summary>
    public event Action? RulesChanged;

    public void NotifyRulesChanged()
    {
        OnPropertyChanged(nameof(HasRules));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowList));
        RulesChanged?.Invoke();
    }

    partial void OnEditorChanged(RuleEditorViewModel? value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(ShowList));
    }

    [RelayCommand]
    void AddRule()
    {
        SelectedTab = RulesPage;
        Editor = new RuleEditorViewModel(this, null);
    }

    public void Edit(RuleViewModel rule)
    {
        SelectedTab = RulesPage;
        Editor = new RuleEditorViewModel(this, rule.Rule);
    }

    public void CloseEditor() => Editor = null;

    /// <summary>Proxies saved before: the recents file first, then any used by current rules, without duplicates.</summary>
    public IReadOnlyList<RecentProxy> RecentProxies() =>
        (recents?.Load() ?? [])
            .Concat(Rules.Select(r => RecentProxy.From(r.Rule)))
            .DistinctBy(p => p.Key)
            .Take(8)
            .ToList();

    public async Task SaveRuleAsync(ProxyRule rule, bool isNew)
    {
        if (isNew)
        {
            var vm = new RuleViewModel(rule, this);
            Rules.Add(vm);
            Tabs.Add(vm);
            SelectedTab = vm; // the new rule's log tab opens right away
        }
        else
        {
            var vm = Rules.First(r => r.Rule.Id == rule.Id);
            // Changes apply to a fresh launch, so a running rule is switched off first.
            if (vm.IsOn) await vm.TurnOffAsync();
            vm.Update(rule);
        }
        Persist();
        recents?.Add(RecentProxy.From(rule));
        Editor = null;
        NotifyRulesChanged();
    }

    public async Task DeleteAsync(RuleViewModel rule)
    {
        await rule.TurnOffAsync();
        Rules.Remove(rule);
        Tabs.Remove(rule);
        if (Editor is not null) Editor = null;
        SelectedTab = RulesPage;
        Persist();
        NotifyRulesChanged();
    }

    readonly object persistLock = new();

    public void Persist()
    {
        lock (persistLock)
            store.Save(Rules.Select(r => r.Rule).ToList());
    }

    public async Task ShutdownAsync()
    {
        foreach (var r in Rules.ToList())
            await r.TurnOffAsync();
    }
}
