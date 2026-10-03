using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MineMount.Models;
using MineMount.Services;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

namespace MineMount.ViewModels;

public partial class SeriesDetailViewModel : ObservableObject
{
    private readonly ISeriesService _seriesService;
    private readonly ISeriesInstallService _installService;
    private readonly IGameLauncherService _gameLauncher;
    private readonly INotificationService _notificationService;
    private readonly INavigationService _navigationService;
    private readonly ILogService _logService;

    private CancellationTokenSource? _operationCts;

    public event Action? SeriesChanged;

    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _logo = string.Empty;

    [ObservableProperty]
    private string _banner = string.Empty;

    [ObservableProperty]
    private string _version = string.Empty;

    [ObservableProperty]
    private string _installedVersion = string.Empty;

    [ObservableProperty]
    private string _installPath = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private Brush _statusBrush = Brushes.Gray;

    [ObservableProperty]
    private bool _isAvailable = true;

    [ObservableProperty]
    private bool _isNotInstalled;

    [ObservableProperty]
    private bool _isInstalled;

    [ObservableProperty]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private bool _hasSelection;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _progressBytesText = string.Empty;

    [ObservableProperty]
    private string _resultMessage = string.Empty;

    public SeriesDetailViewModel(
        ISeriesService seriesService,
        ISeriesInstallService installService,
        IGameLauncherService gameLauncher,
        INotificationService notificationService,
        INavigationService navigationService,
        ILogService logService)
    {
        _seriesService = seriesService;
        _installService = installService;
        _gameLauncher = gameLauncher;
        _notificationService = notificationService;
        _navigationService = navigationService;
        _logService = logService;
    }

    public async Task LoadAsync(string? seriesId)
    {
        if (string.IsNullOrWhiteSpace(seriesId))
        {
            HasSelection = false;
            return;
        }

        try
        {
            var info = await _seriesService.GetSeriesAsync(seriesId);
            if (info == null)
            {
                HasSelection = false;
                StatusText = "Serie no encontrada";
                return;
            }

            HasSelection = true;
            Id = info.Id;
            Name = info.Name;
            Description = info.Description;
            Logo = info.Logo;
            Banner = info.Banner;
            Version = info.Version;
            InstalledVersion = info.InstalledVersion;
            InstallPath = info.InstallPath;
            IsAvailable = info.IsAvailable;

            ApplyStatus(info);
        }
        catch (Exception ex)
        {
            _logService.Error($"Failed to load series {seriesId}", ex);
            StatusText = "Error al cargar la serie";
        }
    }

    private void ApplyStatus(SeriesInfo info)
    {
        StatusText = info.Status switch
        {
            SeriesStatus.ComingSoon => "PRÓXIMAMENTE",
            SeriesStatus.NotInstalled => "NO INSTALADO",
            SeriesStatus.Installed => "INSTALADA",
            SeriesStatus.UpdateAvailable => "ACTUALIZACIÓN DISPONIBLE",
            SeriesStatus.MissingFiles => "FALTAN ARCHIVOS",
            _ => info.StatusText
        };

        StatusBrush = info.Status switch
        {
            SeriesStatus.Installed => new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),
            SeriesStatus.MissingFiles => new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36)),
            SeriesStatus.UpdateAvailable => new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00)),
            SeriesStatus.ComingSoon => new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)),
            _ => new SolidColorBrush(Color.FromRgb(0xB3, 0xB3, 0xB3))
        };

        IsNotInstalled = info.Status == SeriesStatus.NotInstalled;
        IsInstalled = info.IsInstalled;
        IsUpdateAvailable = info.Status == SeriesStatus.UpdateAvailable;
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        await RunOperationAsync("Instalación",
            (progress, ct) => _installService.InstallAsync(Id, progress, ct));
    }

    [RelayCommand]
    private async Task UpdateAsync()
    {
        await RunOperationAsync("Actualización",
            (progress, ct) => _installService.UpdateAsync(Id, progress, ct));
    }

    [RelayCommand]
    private async Task RepairAsync()
    {
        await RunOperationAsync("Reparación",
            (progress, ct) => _installService.RepairAsync(Id, progress, ct));
    }

    [RelayCommand]
    private async Task PlayAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(Id)) return;

        var result = await _gameLauncher.LaunchAsync(Id);
        if (!result.Success && !string.IsNullOrEmpty(result.Message))
        {
            _notificationService.NotifyError("No se pudo iniciar", result.Message);
        }
    }

    [RelayCommand]
    private void CancelOperation()
    {
        _operationCts?.Cancel();
    }

    private async Task RunOperationAsync(
        string verb,
        Func<IProgress<SeriesProgress>, CancellationToken, Task<SeriesOperationResult>> operation)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(Id)) return;

        _operationCts = new CancellationTokenSource();

        try
        {
            IsBusy = true;
            ResultMessage = string.Empty;
            ProgressPercent = 0;
            ProgressText = "Preparando...";
            ProgressBytesText = string.Empty;

            var progress = new Progress<SeriesProgress>(p =>
            {
                ProgressPercent = p.Percent;
                ProgressText = $"Paso {p.StepIndex}/{p.StepCount} · {p.StepName} — {p.Message}";
                ProgressBytesText = FormatProgressBytes(p);
            });

            var result = await operation(progress, _operationCts.Token);
            ResultMessage = result.Message;
        }
        catch (Exception ex)
        {
            _logService.Error("Series operation failed", ex);
            ResultMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            ProgressText = string.Empty;
            ProgressBytesText = string.Empty;
            _operationCts.Dispose();
            _operationCts = null;
        }

        await LoadAsync(Id);
        SeriesChanged?.Invoke();
    }

    private static string FormatProgressBytes(SeriesProgress p)
    {
        if (p.TotalBytes <= 0 && p.BytesReceived <= 0) return string.Empty;

        var received = FormatBytes(p.BytesReceived);
        var total = p.TotalBytes > 0 ? FormatBytes(p.TotalBytes) : "?";
        var speed = p.SpeedBps > 0 ? $"{FormatBytes(p.SpeedBps)}/s" : string.Empty;

        return speed.Length > 0 ? $"{received} / {total} · {speed}" : $"{received} / {total}";
    }

    private static string FormatBytes(double bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824:F2} GB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576:F1} MB";
        if (bytes >= 1_024) return $"{bytes / 1_024:F0} KB";
        return $"{bytes:F0} B";
    }

    [RelayCommand]
    private void Back()
    {
        _navigationService.NavigateTo(NavigationPage.Series);
    }
}
