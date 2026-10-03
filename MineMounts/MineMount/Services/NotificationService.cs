using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MineMount.Services;

public enum NotificationType
{
    Success,
    Info,
    Warning,
    Error,
    Update
}

public class NotificationItem
{
    public Guid Id { get; } = Guid.NewGuid();
    public NotificationType Type { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(5);
    public DateTime CreatedAt { get; } = DateTime.Now;
}

public interface INotificationService
{
    event EventHandler<NotificationItem>? NotificationRaised;
    void Notify(NotificationType type, string title, string message, TimeSpan? duration = null);
    void NotifySuccess(string title, string message, TimeSpan? duration = null);
    void NotifyInfo(string title, string message, TimeSpan? duration = null);
    void NotifyWarning(string title, string message, TimeSpan? duration = null);
    void NotifyError(string title, string message, TimeSpan? duration = null);
    void NotifyUpdate(string title, string message, TimeSpan? duration = null);
}

// Servicio de notificaciones no invasivas: la UI se suscribe y las
// muestra en una esquina. Sin MessageBox, sin spam: solo eventos
// importantes (instalación completada, errores, actualizaciones).
public class NotificationService : INotificationService
{
    public event EventHandler<NotificationItem>? NotificationRaised;

    public void Notify(NotificationType type, string title, string message, TimeSpan? duration = null)
    {
        var item = new NotificationItem
        {
            Type = type,
            Title = title,
            Message = message,
            Duration = duration ?? TimeSpan.FromSeconds(5)
        };

        NotificationRaised?.Invoke(this, item);
    }

    public void NotifySuccess(string title, string message, TimeSpan? duration = null)
        => Notify(NotificationType.Success, title, message, duration);

    public void NotifyInfo(string title, string message, TimeSpan? duration = null)
        => Notify(NotificationType.Info, title, message, duration);

    public void NotifyWarning(string title, string message, TimeSpan? duration = null)
        => Notify(NotificationType.Warning, title, message, duration);

    public void NotifyError(string title, string message, TimeSpan? duration = null)
        => Notify(NotificationType.Error, title, message, duration);

    public void NotifyUpdate(string title, string message, TimeSpan? duration = null)
        => Notify(NotificationType.Update, title, message, duration);
}
