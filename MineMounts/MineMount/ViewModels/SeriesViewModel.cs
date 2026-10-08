using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MineMount.Models;
using MineMount.Services;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MineMount.ViewModels;

/// <summary>Tarjeta de serie con progreso propio de operación.</summary>
public partial class SeriesCard : ObservableObject
{
    public SeriesInfo Info { get; }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _progressText = string.Empty;

    public SeriesCard(SeriesInfo Info)
    {
        this.Info = Info;
    }

    [ObservableProperty]
    private bool _isSelected;

    public bool ShowChangelog => Info.Status == SeriesStatus.UpdateAvailable
        && !string.IsNullOrWhiteSpace(Info.Definition.Changelog);

    public string ChangelogTitle => Loc.Tf("S.Series.Changelog", Info.Version);

    public string McLine => Loc.Tf("S.Series.MCLine",
        Info.Definition.MinecraftVersion, Info.Definition.Loader, Info.Version);
}

public partial class SeriesViewModel : ObservableObject
{
    private readonly ISeriesService _seriesService;
    private readonly ISeriesInstallService _installService;
    private readonly IGameLauncherService _gameLauncher;
    private readonly INotificationService _notificationService;
    private readonly IAppearanceService _appearanceService;
    private readonly ILogService _logService;

    private string? _lastSelectedId;
    private int _lastCount = -1;
    private CancellationTokenSource? _operationCts;

    /// <summary>Se dispara cuando una serie se lanza desde una tarjeta.</summary>
    public event Action<string>? GameLaunched;

    [ObservableProperty]
    private ObservableCollection<SeriesCard> _cards = new();

    [ObservableProperty]
    private SeriesCard? _selectedCard;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private int _gridColumns = 3;

    [ObservableProperty]
    private bool _showCrashBanner;

    [ObservableProperty]
    private string _crashMessage = string.Empty;

    private string _crashLogPath = string.Empty;

    public SeriesInfo? SelectedSeries => SelectedCard?.Info;
    public bool HasSeries => Cards.Count > 0;

    public SeriesViewModel(
        ISeriesService seriesService,
        ISeriesInstallService installService,
        IGameLauncherService gameLauncher,
        INotificationService notificationService,
        IAppearanceService appearanceService,
        ILogService logService)
    {
        _seriesService = seriesService;
        _installService = installService;
        _gameLauncher = gameLauncher;
        _notificationService = notificationService;
        _appearanceService = appearanceService;
        _logService = logService;

        _gameLauncher.GameCrashed += (_, e) =>
        {
            // Viene del hilo del proceso: volver a la UI
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                _crashLogPath = e.LogPath;
                CrashMessage = Loc.Tf("S.Crash.Msg", e.ExitCode);
                ShowCrashBanner = true;
            });
        };
        _appearanceService.LanguageChanged += (_, _) =>
        {
            RefreshCounter();
            _ = RefreshAsync();
        };

        _ = RefreshAsync();
    }

    partial void OnSelectedCardChanged(SeriesCard? value)
    {
        foreach (var card in Cards)
            card.IsSelected = ReferenceEquals(card, value);
        if (value != null) _lastSelectedId = value.Info.Id;
        OnPropertyChanged(nameof(SelectedSeries));
    }

    public void RefreshCounter()
    {
        if (_lastCount < 0) return;
        StatusText = _lastCount switch
        {
            0 => Loc.T("S.Series.None"),
            1 => Loc.T("S.Series.Single"),
            _ => Loc.Tf("S.Series.Many", _lastCount)
        };
        OnPropertyChanged(nameof(HasSeries));
    }

    public async Task RefreshAsync()
    {
        try
        {
            IsLoading = true;
            StatusText = Loc.T("S.Series.Loading");

            var list = await _seriesService.GetSeriesAsync();
            Cards = new ObservableCollection<SeriesCard>(list.Select(s => new SeriesCard(s)));
            _lastCount = list.Count;
            RefreshCounter();

            var toSelect = Cards.FirstOrDefault(c => string.Equals(c.Info.Id, _lastSelectedId, StringComparison.OrdinalIgnoreCase))
                ?? Cards.FirstOrDefault();
            SelectedCard = toSelect;
        }
        catch (Exception ex)
        {
            _logService.Error("Failed to load series", ex);
            StatusText = Loc.T("S.Series.Error");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void SelectCard(SeriesCard? card)
    {
        if (card != null) SelectedCard = card;
    }

    [RelayCommand]
    private void DismissCrash()
    {
        ShowCrashBanner = false;
    }

    [RelayCommand]
    private void ViewCrashLog()
    {
        try
        {
            var window = new Views.LogViewer(_crashLogPath);
            window.Show();
        }
        catch (Exception ex)
        {
            _logService.Warning($"No se pudo abrir el visor: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task PlayCardAsync(SeriesCard? card)
    {
        if (card == null || card.IsBusy) return;
        SelectedCard = card;
        ShowCrashBanner = false;

        card.IsBusy = true;
        card.Progress = 0;
        card.ProgressText = Loc.T("S.Play.Preparing");
        try
        {
            var progress = new Progress<double>(p =>
            {
                card.Progress = p;
                card.ProgressText = Loc.Tf("S.Play.StartingPct", p);
            });

            var result = await _gameLauncher.LaunchAsync(card.Info.Id, progress);
            if (result.Success)
            {
                GameLaunched?.Invoke(card.Info.Id);
            }
            else if (!string.IsNullOrWhiteSpace(result.Message))
            {
                _notificationService.NotifyError(Loc.T("S.Common.Error"), result.Message);
            }
        }
        catch (Exception ex)
        {
            _logService.Error("Play from card failed", ex);
            _notificationService.NotifyError(Loc.T("S.Common.Error"), ex.Message);
        }
        finally
        {
            card.IsBusy = false;
            card.ProgressText = string.Empty;
        }
    }

    [RelayCommand]
    private async Task InstallCardAsync(SeriesCard? card)
    {
        if (card != null) await RunCardOperationAsync(card, "install");
    }

    [RelayCommand]
    private async Task UpdateCardAsync(SeriesCard? card)
    {
        if (card != null) await RunCardOperationAsync(card, "update");
    }

    [RelayCommand]
    private async Task RepairCardAsync(SeriesCard? card)
    {
        if (card != null) await RunCardOperationAsync(card, "repair");
    }

    private async Task RunCardOperationAsync(SeriesCard card, string op)
    {
        if (card.IsBusy) return;
        SelectedCard = card;

        _operationCts?.Cancel();
        _operationCts = new CancellationTokenSource();

        card.IsBusy = true;
        card.Progress = 0;
        card.ProgressText = Loc.T("S.Play.Preparing");
        try
        {
            var progress = new Progress<SeriesProgress>(p =>
            {
                card.Progress = p.Percent;
                card.ProgressText = Loc.Tf("S.Play.Step", p.StepIndex, p.StepCount, p.StepName, p.Percent);
            });

            SeriesOperationResult result = op switch
            {
                "update" => await _installService.UpdateAsync(card.Info.Id, progress, _operationCts.Token),
                "repair" => await _installService.RepairAsync(card.Info.Id, progress, _operationCts.Token),
                _ => await _installService.InstallAsync(card.Info.Id, progress, _operationCts.Token)
            };

            if (result.Success)
                _notificationService.NotifySuccess(Loc.Tf("S.Play.ReadyList", card.Info.Name), result.Message);
            else
                _notificationService.NotifyError(Loc.T("S.Common.Error"), result.Message);
        }
        catch (OperationCanceledException)
        {
            // Cancelado por el usuario
        }
        catch (Exception ex)
        {
            _logService.Error("Card operation failed", ex);
            _notificationService.NotifyError(Loc.T("S.Common.Error"), ex.Message);
        }
        finally
        {
            card.IsBusy = false;
            card.ProgressText = string.Empty;
            _operationCts?.Dispose();
            _operationCts = null;
        }

        await RefreshAsync();
    }

    [RelayCommand]
    private void OpenFolder(SeriesCard? card)
    {
        try
        {
            var path = card?.Info.InstallPath;
            if (string.IsNullOrWhiteSpace(path)) return;
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logService.Warning($"No se pudo abrir la carpeta: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task UninstallCardAsync(SeriesCard? card)
    {
        if (card == null) return;

        var answer = System.Windows.MessageBox.Show(
            Loc.Tf("S.Series.UninstallMsg", card.Info.Name),
            Loc.T("S.Series.UninstallTitle"),
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (answer != System.Windows.MessageBoxResult.Yes) return;

        var ok = await _installService.UninstallAsync(card.Info.Id);
        if (ok)
        {
            _notificationService.NotifySuccess(
                Loc.Tf("S.Series.Uninstalled", card.Info.Name), string.Empty);
        }
        else
        {
            _notificationService.NotifyError(
                Loc.T("S.Common.Error"), Loc.Tf("S.Series.Uninstalled", card.Info.Name));
        }

        await RefreshAsync();
    }
}
