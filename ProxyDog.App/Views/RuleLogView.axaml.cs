using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using ProxyDog.UI.ViewModels;

namespace ProxyDog.UI.Views;

public partial class RuleLogView : UserControl
{
    INotifyCollectionChanged? watched;

    public RuleLogView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (watched is not null) watched.CollectionChanged -= OnLogsChanged;
            watched = (DataContext as RuleViewModel)?.Logs;
            if (watched is not null) watched.CollectionChanged += OnLogsChanged;
        };
    }

    // Keep the newest connection in view, like a terminal tail.
    void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (LogList.ItemCount > 0)
                LogList.ScrollIntoView(LogList.ItemCount - 1);
        }, DispatcherPriority.Background);
    }
}
