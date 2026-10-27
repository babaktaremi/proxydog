using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDog.Core;

namespace ProxyDog.UI.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    readonly RuleStore store;
    readonly RecentProxyStore? recents;

    readonly V2RayProfileStore? v2rayStore;

    public MainViewModel(RuleStore store, RecentProxyStore? recents = null, UpdateViewModel? update = null,
        V2RayProfileStore? v2rayStore = null, V2RayCoreViewModel? v2rayCore = null)
    {
        Update = update ?? UpdateViewModel.None;
        V2RayCore = v2rayCore ?? new V2RayCoreViewModel();
        this.v2rayStore = v2rayStore;
        foreach (var profile in v2rayStore?.Load() ?? [])
            V2RayProfiles.Add(new V2RayProfileItem(profile));
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
    public V2RayCoreViewModel V2RayCore { get; }
    public ObservableCollection<V2RayProfileItem> V2RayProfiles { get; } = [];
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

    public V2RayProfileItem? FindV2RayProfile(Guid? id) => id is null ? null : V2RayProfiles.FirstOrDefault(p => p.Id == id);

    /// <summary>Validates and saves a pasted vless:// or vmess:// link. Returns null and sets <paramref name="error"/> when it cannot.</summary>
    public V2RayProfileItem? AddV2RayProfile(string? link, string? name, out string error)
    {
        if (!V2RayLink.TryParse(link, out var endpoint, out error))
            return null;
        if (V2RayProfiles.FirstOrDefault(p => p.Profile.Link == link!.Trim()) is { } existing)
        {
            error = $"That link is already saved as \"{existing.Name}\".";
            return null;
        }
        var profile = new V2RayProfile
        {
            Name = string.IsNullOrWhiteSpace(name) ? endpoint.Name : name.Trim(),
            Link = link!.Trim(),
        };
        var item = new V2RayProfileItem(profile);
        V2RayProfiles.Add(item);
        v2rayStore?.Save(V2RayProfiles.Select(p => p.Profile));
        error = "";
        return item;
    }

    /// <summary>A profile that a rule still uses cannot be removed.</summary>
    public bool RemoveV2RayProfile(V2RayProfileItem item, out string error)
    {
        var users = Rules.Count(r => r.Rule.Kind == ProxyKind.V2Ray && r.Rule.V2RayProfileId == item.Id);
        if (users > 0)
        {
            error = users == 1 ? "A rule uses this profile. Change or remove that rule first." : $"{users} rules use this profile. Change or remove them first.";
            return false;
        }
        V2RayProfiles.Remove(item);
        v2rayStore?.Save(V2RayProfiles.Select(p => p.Profile));
        error = "";
        return true;
    }

    /// <summary>Proxies saved before: the recents file first, then any used by current rules, without duplicates.</summary>
    public IReadOnlyList<RecentProxy> RecentProxies() =>
        (recents?.Load() ?? [])
            .Concat(Rules.Where(r => r.Rule.Kind != ProxyKind.V2Ray).Select(r => RecentProxy.From(r.Rule)))
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
        if (rule.Kind != ProxyKind.V2Ray) recents?.Add(RecentProxy.From(rule));
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
