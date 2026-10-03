using CommunityToolkit.Mvvm.ComponentModel;
using MineMount.Services;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace MineMount.ViewModels;

public partial class NotificationViewModel : ObservableObject
{
    private readonly INotificationService _notificationService;
    private readonly Dispatcher _dispatcher;

    [ObservableProperty]
    private ObservableCollection<NotificationItemViewModel> _notifications = new();

    public NotificationViewModel(INotificationService notificationService)
    {
        _notificationService = notificationService;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _notificationService.NotificationRaised += OnNotificationRaised;
    }

    private void OnNotificationRaised(object? sender, NotificationItem item)
    {
        _dispatcher.Invoke(() =>
        {
            var vm = new NotificationItemViewModel(item);
            Notifications.Insert(0, vm);

            _ = AutoDismissAsync(vm);
        });
    }

    private async Task AutoDismissAsync(NotificationItemViewModel vm)
    {
        try
        {
            await Task.Delay(vm.Duration);
            _dispatcher.Invoke(() => Notifications.Remove(vm));
        }
        catch
        {
            // La ventana puede cerrarse antes del auto-cierre
        }
    }
}

public partial class NotificationItemViewModel : ObservableObject
{
    private readonly NotificationItem _item;

    public NotificationItemViewModel(NotificationItem item)
    {
        _item = item;
    }

    public string Title => _item.Title;
    public string Message => _item.Message;
    public TimeSpan Duration => _item.Duration;

    public string Icon => _item.Type switch
    {
        NotificationType.Success => "✓",
        NotificationType.Info => "ℹ",
        NotificationType.Warning => "⚠",
        NotificationType.Error => "✕",
        NotificationType.Update => "↑",
        _ => "•"
    };
}
