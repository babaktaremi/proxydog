using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDog.Core;
using ProxyDog.Engine;

namespace ProxyDog.UI.ViewModels;

public sealed partial class RuleViewModel : TabViewModel
{
    const int MaxRows = 1500;

    readonly MainViewModel main;
    readonly Dictionary<Guid, LogRowViewModel> rows = [];
    RuleSession? session;
    bool isOn;     // what is actually running
    bool wantOn;   // what the user last asked for; the switch shows this
    readonly SemaphoreSlim gate = new(1, 1);

    public RuleViewModel(ProxyRule rule, MainViewModel main)
    {
        Rule = rule;
        this.main = main;
        EditCommand = new RelayCommand(() => main.Edit(this));
        DeleteCommand = new AsyncRelayCommand(() => main.DeleteAsync(this));
        OpenCommand = new RelayCommand(() => main.SelectedTab = this);
        ClearLogCommand = new RelayCommand(ClearLog);
        LaunchCommand = new AsyncRelayCommand(LaunchAsync);
    }

    public ProxyRule Rule { get; private set; }
    public ObservableCollection<LogRowViewModel> Logs { get; } = [];

    public IRelayCommand EditCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }
    public IRelayCommand OpenCommand { get; }
    public IRelayCommand ClearLogCommand { get; }
    public IAsyncRelayCommand LaunchCommand { get; }

    [ObservableProperty] string? lastError;
    [ObservableProperty] int openConnections;
    [ObservableProperty] int totalConnections;

    public override string Title => Rule.Name;
    public override bool IsActive => isOn;
    public override bool ShowsStatus => true;
    public string Summary => $"{(Rule.Kind == ProxyKind.Socks5 ? "SOCKS5" : "HTTP")} {Rule.Host}:{Rule.Port}";
    public string AppName => Path.GetFileNameWithoutExtension(Rule.AppPath.TrimEnd('/', '\\'));
    /// <summary>Applies at once, also to a running rule, and is saved with the rule.</summary>
    public bool KillSwitch
    {
        get => Rule.KillSwitch;
        set
        {
            if (Rule.KillSwitch == value) return;
            if (session is { } s) s.SetKillSwitch(value);
            else Rule.KillSwitch = value;
            OnPropertyChanged();
            main.Persist();
        }
    }
    public bool HasLogs => Logs.Count > 0;
    public bool HasError => !string.IsNullOrEmpty(LastError);
    public bool CanLaunch => isOn && !(session?.IsAppRunning ?? false);

    public string StatusText =>
        wantOn && !isOn ? "Starting…"
        : !wantOn && isOn ? "Turning off…"
        : !isOn ? "Off"
        : session?.IsAppRunning ?? false ? $"On, proxying {AppName}"
        : $"On, {AppName} is not running";

    /// <summary>
    /// The switch. It records what the user asked for at once and the real start or stop catches up in order,
    /// so flipping it again while a start is still under way is never lost.
    /// </summary>
    public bool IsOn
    {
        get => wantOn;
        set
        {
            if (wantOn == value) return;
            wantOn = value;
            RaiseState();
            main.NotifyRulesChanged();
            _ = ReconcileAsync();
        }
    }

    partial void OnLastErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    public void Update(ProxyRule rule)
    {
        Rule = rule;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(AppName));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(KillSwitch));
    }

    public Task TurnOnAsync()
    {
        IsOn = true;
        return ReconcileAsync();
    }

    public Task TurnOffAsync()
    {
        IsOn = false;
        return ReconcileAsync();
    }

    // One start or stop at a time, repeated until what is running matches what was asked for.
    async Task ReconcileAsync()
    {
        await gate.WaitAsync();
        try
        {
            while (wantOn != isOn)
            {
                if (wantOn) await StartCoreAsync();
                else await StopCoreAsync();
            }
        }
        finally
        {
            gate.Release();
        }
    }

    async Task StartCoreAsync()
    {
        LastError = null;
        var s = new RuleSession(Rule);
        s.ConnectionEvent += e => Dispatcher.UIThread.Post(() => Apply(e));
        s.Notice += (text, problem) => Dispatcher.UIThread.Post(() => AddNotice(text, problem));
        s.StateChanged += () => Dispatcher.UIThread.Post(RaiseState);
        s.KillSwitchTripped += reason => Dispatcher.UIThread.Post(() =>
        {
            // The session already closed the app and stopped itself; reflect that in the UI.
            session = null;
            wantOn = false;
            LastError = $"Kill switch: {reason}. {AppName} was closed and the rule turned off.";
            SetOn(false);
        });
        try
        {
            // Start() checks code signing and may wait for the old copy to quit, so keep it off the UI thread.
            await Task.Run(s.Start);
            session = s;
            SetOn(true);
            main.Persist(); // keeps the rule's local port (and what was sent to Telegram) for next time
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AddNotice(ex.Message, true);
            wantOn = false; // it did not start, so the switch goes back to off
            SetOn(false);
        }
    }

    async Task StopCoreAsync()
    {
        var s = session;
        session = null;
        if (s is not null)
            await s.StopAsync();
        SetOn(false);
    }

    async Task LaunchAsync()
    {
        try
        {
            LastError = null;
            if (session is { } s)
            {
                await Task.Run(s.LaunchApp);
                main.Persist();
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AddNotice(ex.Message, true);
        }
    }

    void SetOn(bool value)
    {
        isOn = value;
        RaiseState();
        main.NotifyRulesChanged();
    }

    void RaiseState()
    {
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanLaunch));
    }

    void ClearLog()
    {
        foreach (var r in Logs.Where(r => r.IsOpen).ToList())
            rows.Remove(r.Id);
        Logs.Clear();
        OnPropertyChanged(nameof(HasLogs));
    }

    void AddRow(LogRowViewModel row)
    {
        Logs.Add(row);
        if (Logs.Count > MaxRows)
        {
            var first = Logs[0];
            Logs.RemoveAt(0);
            rows.Remove(first.Id);
        }
        OnPropertyChanged(nameof(HasLogs));
    }

    void AddNotice(string text, bool problem) =>
        AddRow(new LogRowViewModel(Guid.NewGuid(), DateTimeOffset.Now, text)
        {
            IsNotice = true,
            Status = problem ? "Warning" : "Info",
            IsFailed = problem,
        });

    void Apply(ConnectionEvent e)
    {
        switch (e.Kind)
        {
            case ConnectionEventKind.Opened:
                var row = new LogRowViewModel(e.Id, e.Time, e.Destination);
                rows[e.Id] = row;
                AddRow(row);
                TotalConnections++;
                OpenConnections++;
                break;
            case ConnectionEventKind.Connected when rows.TryGetValue(e.Id, out var r):
                r.Status = "Open";
                r.IsOpen = true;
                break;
            case ConnectionEventKind.Closed when rows.TryGetValue(e.Id, out var r):
                r.Status = "Closed";
                r.IsOpen = false;
                r.Detail = $"sent {FormatBytes(e.BytesUp)}, received {FormatBytes(e.BytesDown)}";
                OpenConnections = Math.Max(0, OpenConnections - 1);
                break;
            case ConnectionEventKind.Failed when rows.TryGetValue(e.Id, out var r):
                r.Status = "Failed";
                r.IsFailed = true;
                r.IsOpen = false;
                r.Detail = e.Error ?? "";
                OpenConnections = Math.Max(0, OpenConnections - 1);
                break;
        }
    }

    static string FormatBytes(long n) => n switch
    {
        < 1024 => $"{n} B",
        < 1024 * 1024 => $"{n / 1024.0:0.#} KB",
        _ => $"{n / 1024.0 / 1024.0:0.#} MB",
    };
}
