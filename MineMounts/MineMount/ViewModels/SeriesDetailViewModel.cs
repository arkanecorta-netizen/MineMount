using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MineMount.Models;
using MineMount.Services;
using System;
using System.Threading.Tasks;
using System.Windows.Media;

namespace MineMount.ViewModels;

public partial class SeriesDetailViewModel : ObservableObject
{
    private readonly ISeriesService _seriesService;
    private readonly INavigationService _navigationService;
    private readonly ILogService _logService;

    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _image = string.Empty;

    [ObservableProperty]
    private string _version = string.Empty;

    [ObservableProperty]
    private string _installedVersion = string.Empty;

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
    private bool _isBusy;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _resultMessage = string.Empty;

    public SeriesDetailViewModel(
        ISeriesService seriesService,
        INavigationService navigationService,
        ILogService logService)
    {
        _seriesService = seriesService;
        _navigationService = navigationService;
        _logService = logService;
    }

    public async Task LoadAsync(string? seriesId)
    {
        if (string.IsNullOrWhiteSpace(seriesId)) return;

        try
        {
            var info = await _seriesService.GetSeriesAsync(seriesId);
            if (info == null)
            {
                StatusText = "Serie no encontrada";
                return;
            }

            Id = info.Id;
            Name = info.Name;
            Description = info.Description;
            Image = info.Image;
            Version = info.Version;
            InstalledVersion = info.InstalledVersion;
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
            SeriesStatus.ComingSoon => "Próximamente",
            SeriesStatus.NotInstalled => "NO INSTALADO",
            SeriesStatus.Installed => "TODO INSTALADO",
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
        await RunOperationAsync(progress => _seriesService.InstallAsync(Id, progress));
    }

    [RelayCommand]
    private async Task UpdateAsync()
    {
        await RunOperationAsync(progress => _seriesService.UpdateAsync(Id, progress));
    }

    [RelayCommand]
    private async Task RepairAsync()
    {
        await RunOperationAsync(progress => _seriesService.RepairAsync(Id, progress));
    }

    private async Task RunOperationAsync(Func<IProgress<SeriesProgress>, Task<SeriesOperationResult>> operation)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(Id)) return;

        try
        {
            IsBusy = true;
            ResultMessage = string.Empty;
            ProgressPercent = 0;
            ProgressText = "Preparando...";

            var progress = new Progress<SeriesProgress>(p =>
            {
                ProgressPercent = p.Percent;
                ProgressText = $"{p.FileIndex}/{p.FileCount} · {p.FileName} — {p.Message}";
            });

            var result = await operation(progress);
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
        }

        await LoadAsync(Id);
    }

    [RelayCommand]
    private void Back()
    {
        _navigationService.NavigateTo(NavigationPage.Series);
    }
}
