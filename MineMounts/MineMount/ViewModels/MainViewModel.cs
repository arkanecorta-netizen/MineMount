using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MineMount.Models;
using MineMount.Services;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace MineMount.ViewModels;

public enum NavigationPage
{
    Home,
    Games,
    Servers,
    Settings,
    Series,
    SeriesDetail
}

/// <summary>Tarjeta promocional pequeña de la barra inferior (navegación interna).</summary>
public partial class PromoItem : ObservableObject
{
    public string TitleKey { get; set; } = string.Empty;
    public string DescriptionKey { get; set; } = string.Empty;
    public string IconKey { get; set; } = "IconSparkles";
    public string Target { get; set; } = "Series";

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;
}

public partial class MainViewModel : ObservableObject
{
    private readonly INavigationService _navigationService;
    private readonly ISettingsService _settingsService;
    private readonly IUpdateService _updateService;
    private readonly ILogService _logService;
    private readonly INotificationService _notificationService;
    private readonly ISeriesService _seriesService;
    private readonly ISeriesInstallService _installService;
    private readonly IGameLauncherService _gameLauncher;
    private readonly IAuthService _authService;
    private readonly IAppearanceService _appearanceService;

    [ObservableProperty]
    private NavigationPage _currentPage = NavigationPage.Home;

    [ObservableProperty]
    private string _windowTitle = "MineMount";

    [ObservableProperty]
    private bool _isMaximized;

    [ObservableProperty]
    private string _version = "1.0.0";

    [ObservableProperty]
    private string _statusText = "Listo";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyMessage = string.Empty;

    [ObservableProperty]
    private string _userName = "Jugador";

    [ObservableProperty]
    private string _avatarUrl = string.Empty;

    [ObservableProperty]
    private bool _isLoggedIn;

    [ObservableProperty]
    private string _currentGameVersion = "1.21.4";

    [ObservableProperty]
    private string _selectedModpack = "Vanilla";

    [ObservableProperty]
    private int _allocatedRAM = 4096;

    [ObservableProperty]
    private string _launcherStatus = "Actualizado";

    [ObservableProperty]
    private bool _hasUpdates;

    [ObservableProperty]
    private string _updateStatus = string.Empty;

    [ObservableProperty]
    private double _updateProgress;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private object? _currentPageViewModel;

    [ObservableProperty]
    private string _selectedNavTag = "Home";

    // ---------- Barra inferior fija (estilo launcher oficial) ----------
    [ObservableProperty]
    private ObservableCollection<SeriesInfo> _availableSeries = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlayPrimary))]
    private SeriesInfo? _selectedPlaySeries;

    [ObservableProperty]
    private string _playButtonText = "CARGANDO…";

    [ObservableProperty]
    private string _playStatusDetail = "Buscando series…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlayPrimary))]
    private bool _isPlayBusy;

    // Error recuperable de la última operación (botón pasa a REINTENTAR)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlayPrimary))]
    private bool _playHasError;

    [ObservableProperty]
    private bool _playStatusIsError;

    // Minecraft en ejecución (botón en estado JUGANDO, deshabilitado)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlayPrimary))]
    private bool _isGameRunning;

    [ObservableProperty]
    private double _playProgress;

    [ObservableProperty]
    private string _playProgressText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<PromoItem> _promoItems = new();

    [ObservableProperty]
    private bool _promosCompact;

    // ---------- Fondo animado + partículas ----------
    [ObservableProperty]
    private string _backgroundMode = "Auto";

    [ObservableProperty]
    private int _selectedBackground;

    [ObservableProperty]
    private int _backgroundInterval = 8;

    [ObservableProperty]
    private int _backgroundDim = 25;

    [ObservableProperty]
    private int _backgroundBlur;

    [ObservableProperty]
    private System.Collections.Generic.List<string> _customBackgrounds = new();

    [ObservableProperty]
    private bool _enableParticles = true;

    [ObservableProperty]
    private bool _enableAnimations = true;

    [ObservableProperty]
    private bool _windowActive = true;

    public bool ParticlesActive => EnableParticles && EnableAnimations && WindowActive
        && !string.Equals(BackgroundMode, "Desactivado", StringComparison.OrdinalIgnoreCase);

    public bool CanPlayPrimary => !IsPlayBusy
        && !IsGameRunning
        && SelectedPlaySeries != null
        && (SelectedPlaySeries.Status != SeriesStatus.ComingSoon || PlayHasError);

    private readonly HomeViewModel _homeViewModel;
    private readonly GamesViewModel _gamesViewModel;
    private readonly ServersViewModel _serversViewModel;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly SeriesViewModel _seriesViewModel;
    private readonly SeriesDetailViewModel _seriesDetailViewModel;

    public NotificationViewModel Notifications { get; }

    public MainViewModel(
        INavigationService navigationService,
        ISettingsService settingsService,
        IUpdateService updateService,
        ILogService logService,
        INotificationService notificationService,
        HomeViewModel homeViewModel,
        GamesViewModel gamesViewModel,
        ServersViewModel serversViewModel,
        SettingsViewModel settingsViewModel,
        SeriesViewModel seriesViewModel,
        SeriesDetailViewModel seriesDetailViewModel,
        ISeriesService seriesService,
        ISeriesInstallService installService,
        IGameLauncherService gameLauncher,
        IAuthService authService,
        IAppearanceService appearanceService)
    {
        _navigationService = navigationService;
        _settingsService = settingsService;
        _updateService = updateService;
        _logService = logService;
        _notificationService = notificationService;
        _homeViewModel = homeViewModel;
        _gamesViewModel = gamesViewModel;
        _serversViewModel = serversViewModel;
        _settingsViewModel = settingsViewModel;
        _seriesViewModel = seriesViewModel;
        _seriesDetailViewModel = seriesDetailViewModel;
        _seriesService = seriesService;
        _installService = installService;
        _gameLauncher = gameLauncher;
        _authService = authService;
        _appearanceService = appearanceService;
        _appearanceService.LanguageChanged += (_, _) =>
        {
            RefreshPromoTexts();
            RefreshSession();
            if (!IsPlayBusy && !IsGameRunning && !PlayHasError)
                UpdatePlayBar();
        };
        Notifications = new NotificationViewModel(notificationService);

        _version = updateService.CurrentVersion;
        _currentPageViewModel = _homeViewModel;

        _navigationService.Navigated += OnNavigated;
        _settingsService.SettingsChanged += (_, _) =>
            Application.Current.Dispatcher.InvokeAsync(() => _ = RefreshAppearanceAsync());
        _authService.SessionChanged += (_, _) => RefreshSession();
        _gameLauncher.GameCrashed += OnGameCrashed;
        _seriesViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SeriesViewModel.SelectedSeries)
                || e.PropertyName == nameof(SeriesViewModel.SelectedCard))
                SyncPlaySelection();
        };
        _seriesViewModel.GameLaunched += NotifyLaunched;

        BuildPromos();
        RefreshSession();
        _ = InitializeAsync();
    }

    private void OnGameCrashed(object? sender, GameCrashedArgs e)
    {
        // Viene del hilo del proceso: volver a la UI
        Application.Current.Dispatcher.Invoke(() =>
        {
            _notificationService.NotifyError(
                Loc.T("S.Crash.Title"),
                Loc.Tf("S.Crash.Msg", e.ExitCode),
                TimeSpan.FromSeconds(15));
        });
    }

    /// <summary>La tarjeta clicada pasa a ser la serie activa de la barra.</summary>
    private void SyncPlaySelection()
    {
        var id = _seriesViewModel.SelectedSeries?.Id;
        if (string.IsNullOrWhiteSpace(id)) return;
        var match = AvailableSeries.FirstOrDefault(s =>
            string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        if (match != null && !ReferenceEquals(match, SelectedPlaySeries))
            SelectedPlaySeries = match;
    }

    /// <summary>Juego lanzado desde una tarjeta: la barra pasa a JUGANDO.</summary>
    private void NotifyLaunched(string seriesId)
    {
        var match = AvailableSeries.FirstOrDefault(s =>
            string.Equals(s.Id, seriesId, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            SelectedPlaySeries = match;
            EnterPlayingState(match);
        }
        else
        {
            _ = RefreshPlaySeriesAsync();
        }
    }

    private void RefreshSession()
    {
        var session = _authService.CurrentSession;
        if (session != null)
        {
            UserName = session.Name;
            IsLoggedIn = true;
            AvatarUrl = session.Kind == AccountKind.Microsoft && !string.IsNullOrWhiteSpace(session.Uuid)
                ? $"https://minotar.net/helm/{Uri.EscapeDataString(session.Uuid)}/100.png"
                : $"https://minotar.net/helm/{Uri.EscapeDataString(session.Name)}/100.png";
        }
        else
        {
            IsLoggedIn = false;
            UserName = Loc.T("S.Home.Launcher");
            AvatarUrl = string.Empty;
        }
    }

    partial void OnSelectedPlaySeriesChanged(SeriesInfo? value)
    {
        PlayHasError = false;
        PlayStatusIsError = false;
        UpdatePlayBar();
    }
    partial void OnWindowActiveChanged(bool value) => OnPropertyChanged(nameof(ParticlesActive));
    partial void OnEnableParticlesChanged(bool value) => OnPropertyChanged(nameof(ParticlesActive));
    partial void OnEnableAnimationsChanged(bool value) => OnPropertyChanged(nameof(ParticlesActive));
    partial void OnBackgroundModeChanged(string value) => OnPropertyChanged(nameof(ParticlesActive));

    private void BuildPromos()
    {
        PromoItems = new ObservableCollection<PromoItem>
        {
            new() { TitleKey = "S.Promo.Event", DescriptionKey = "S.Promo.EventSub", IconKey = "IconCalendar", Target = "Series" },
            new() { TitleKey = "S.Promo.Updates", DescriptionKey = "S.Promo.UpdatesSub", IconKey = "IconUpdate", Target = "Updates" },
        };
        RefreshPromoTexts();
    }

    /// <summary>Re-traduce las promos al cambiar el idioma.</summary>
    public void RefreshPromoTexts()
    {
        foreach (var promo in PromoItems)
        {
            promo.Title = Loc.T(promo.TitleKey);
            promo.Description = Loc.T(promo.DescriptionKey);
        }
    }

    private void OnNavigated(object? sender, NavigationEventArgs e)
    {
        CurrentPage = e.Page;
        SelectedNavTag = e.Page == NavigationPage.SeriesDetail
            ? nameof(NavigationPage.Series)
            : e.Page.ToString();

        CurrentPageViewModel = e.Page switch
        {
            NavigationPage.Games => _gamesViewModel,
            NavigationPage.Servers => _serversViewModel,
            NavigationPage.Settings => _settingsViewModel,
            NavigationPage.Series => _seriesViewModel,
            NavigationPage.SeriesDetail => _seriesDetailViewModel,
            _ => _homeViewModel
        };

        switch (e.Page)
        {
            case NavigationPage.Series:
                _ = _seriesViewModel.RefreshAsync();
                _ = RefreshPlaySeriesAsync();
                break;
            case NavigationPage.SeriesDetail:
                _ = _seriesDetailViewModel.LoadAsync(e.Parameter as string);
                break;
            case NavigationPage.Home:
                _ = RefreshPlaySeriesAsync();
                break;
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            IsBusy = true;
            BusyMessage = "Inicializando MineMount...";

            await _settingsService.LoadAsync();
            await RefreshAppearanceAsync();

            var hasUpdate = await _updateService.CheckForUpdatesAsync();
            HasUpdates = hasUpdate;

            if (hasUpdate)
            {
                UpdateStatus = $"Actualización v{_updateService.LatestVersion} disponible";
                StatusText = "Actualización disponible";
            }
            else if (!string.IsNullOrEmpty(_updateService.LastError))
            {
                UpdateStatus = _updateService.LastError;
            }
            else
            {
                UpdateStatus = "MineMount está actualizado";
            }

            await RefreshPlaySeriesAsync();

            _logService.Info($"MineMount {Version} initialized successfully");
        }
        catch (Exception ex)
        {
            _logService.Error("Failed to initialize MineMount", ex);
            StatusText = "Error de inicialización";
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
        }
    }

    public async Task RefreshAppearanceAsync()
    {
        try
        {
            var s = await _settingsService.GetSettingsAsync();
            BackgroundMode = string.IsNullOrWhiteSpace(s.BackgroundMode) ? "Auto" : s.BackgroundMode;
            SelectedBackground = Math.Max(0, s.SelectedBackground);
            BackgroundInterval = Math.Clamp(s.BackgroundIntervalSeconds, 2, 20);
            BackgroundDim = Math.Clamp(s.BackgroundDim, 0, 80);
            BackgroundBlur = Math.Clamp(s.BackgroundBlur, 0, 20);
            CustomBackgrounds = new System.Collections.Generic.List<string>(s.CustomBackgrounds ?? new());
            EnableParticles = s.EnableParticles;
            EnableAnimations = s.EnableAnimations;
            RefreshSession();
        }
        catch (Exception ex)
        {
            _logService.Warning($"No se pudo refrescar la apariencia: {ex.Message}");
        }
    }

    public async Task RefreshPlaySeriesAsync()
    {
        try
        {
            var list = await _seriesService.GetSeriesAsync();
            var previousId = SelectedPlaySeries?.Id;
            AvailableSeries = new ObservableCollection<SeriesInfo>(list);

            SelectedPlaySeries = list.FirstOrDefault(s => string.Equals(s.Id, previousId, StringComparison.OrdinalIgnoreCase))
                ?? list.FirstOrDefault(s => s.IsAvailable)
                ?? list.FirstOrDefault();

            if (SelectedPlaySeries == null)
            {
                PlayButtonText = Loc.T("S.NoSeries");
                PlayStatusDetail = Loc.T("S.Play.NoCatalog");
            }
            else
            {
                UpdatePlayBar();
            }

            NotifyPendingUpdates(list);
        }
        catch (Exception ex)
        {
            _logService.Warning($"No se pudo cargar la serie para JUGAR: {ex.Message}");
            PlayButtonText = Loc.T("S.Play");
            PlayStatusDetail = Loc.T("S.Play.Connect");
        }
    }

    private bool _modsNoticeShown;

    /// <summary>Aviso discreto (una vez por sesión) si hay updates de mods.</summary>
    private void NotifyPendingUpdates(System.Collections.Generic.List<SeriesInfo> list)
    {
        if (_modsNoticeShown) return;
        try
        {
            var pending = list.FirstOrDefault(s => s.Status == SeriesStatus.UpdateAvailable);
            if (pending == null) return;
            _modsNoticeShown = true;
            _notificationService.NotifyInfo(
                pending.Name,
                Loc.Tf("S.Update.AvailableFor", pending.Name, pending.Version),
                TimeSpan.FromSeconds(8));
        }
        catch
        {
            // Aviso opcional: nunca rompe la carga
        }
    }

    private void UpdatePlayBar()
    {
        if (IsGameRunning) return; // estado JUGANDO: no pisar hasta que termine

        var s = SelectedPlaySeries;
        if (s == null) return;

        PlayButtonText = s.Status switch
        {
            SeriesStatus.Installed => Loc.T("S.Play"),
            SeriesStatus.NotInstalled => Loc.T("S.Install"),
            SeriesStatus.UpdateAvailable => Loc.T("S.Update"),
            SeriesStatus.MissingFiles => Loc.T("S.Repair"),
            SeriesStatus.ComingSoon => Loc.T("S.ComingSoon"),
            _ => Loc.T("S.Play")
        };

        PlayStatusDetail = BuildStatusDetail(s);
        OnPropertyChanged(nameof(CanPlayPrimary));
        CurrentGameVersion = string.IsNullOrWhiteSpace(s.Definition.MinecraftVersion)
            ? CurrentGameVersion
            : s.Definition.MinecraftVersion;
    }

    /// <summary>
    /// Estado corto bajo el botón en una sola línea: estado + mods + fecha.
    /// La versión y el loader van solo en el selector, sin repetirse.
    /// </summary>
    private static string BuildStatusDetail(SeriesInfo s)
    {
        var head = s.Status switch
        {
            SeriesStatus.Installed => Loc.T("S.Play.Head.Installed"),
            SeriesStatus.NotInstalled => Loc.T("S.Play.Head.Available"),
            SeriesStatus.UpdateAvailable => Loc.T("S.Play.Head.UpdateReady"),
            SeriesStatus.MissingFiles => Loc.T("S.Play.Head.Missing"),
            SeriesStatus.ComingSoon => Loc.T("Status.ComingSoon"),
            _ => s.StatusText
        };

        var parts = new System.Collections.Generic.List<string> { head };

        if (s.IsInstalled && !string.IsNullOrWhiteSpace(s.InstallPath) && Directory.Exists(s.InstallPath))
        {
            try
            {
                var modsDir = Path.Combine(s.InstallPath, "mods");
                if (Directory.Exists(modsDir))
                {
                    var jars = Directory.GetFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly).Length;
                    if (jars > 0) parts.Add(Loc.Tf("S.Play.Mods", jars));
                }

                var updated = Directory.GetLastWriteTime(s.InstallPath);
                if (updated > new DateTime(2009, 1, 1))
                    parts.Add(Loc.Tf("S.Play.Updated", updated));
            }
            catch
            {
                // Estadísticas opcionales: si fallan, se omiten sin romper el estado.
            }
        }

        return string.Join(" · ", parts);
    }

    [RelayCommand]
    private async Task PlayPrimaryAsync()
    {
        var series = SelectedPlaySeries;
        if (series == null || IsPlayBusy || IsGameRunning) return;
        if (series.Status == SeriesStatus.ComingSoon && !PlayHasError) return;

        PlayHasError = false;
        PlayStatusIsError = false;
        IsPlayBusy = true;
        PlayProgress = 0;

        var workingText = series.Status switch
        {
            SeriesStatus.Installed => Loc.T("S.Starting"),
            SeriesStatus.UpdateAvailable => Loc.T("S.Updating"),
            SeriesStatus.MissingFiles => Loc.T("S.Repairing"),
            _ => Loc.T("S.Installing")
        };
        PlayButtonText = workingText;
        PlayProgressText = Loc.T("S.Play.Preparing");

        try
        {
            if (series.Status == SeriesStatus.Installed)
            {
                var launchProgress = new Progress<double>(p =>
                {
                    PlayProgress = p;
                    PlayProgressText = Loc.Tf("S.Play.StartingPct", p);
                });
                var launched = await _gameLauncher.LaunchAsync(series.Id, launchProgress);
                if (!launched.Success)
                {
                    SetPlayError(string.IsNullOrWhiteSpace(launched.Message)
                        ? Loc.T("S.Play.CouldNotStart")
                        : launched.Message);
                    return;
                }

                EnterPlayingState(series);

                var closeOnLaunch = false;
                try
                {
                    closeOnLaunch = (await _settingsService.GetSettingsAsync()).CloseOnLaunch;
                }
                catch
                {
                    // Sin configuración: no cerrar
                }
                if (closeOnLaunch)
                {
                    Application.Current.Shutdown();
                }
                return;
            }

            var progress = new Progress<SeriesProgress>(p =>
            {
                PlayProgress = p.Percent;
                PlayProgressText = Loc.Tf("S.Play.Step", p.StepIndex, p.StepCount, p.StepName, p.Percent);
            });

            SeriesOperationResult result = series.Status switch
            {
                SeriesStatus.UpdateAvailable => await _installService.UpdateAsync(series.Id, progress),
                SeriesStatus.MissingFiles => await _installService.RepairAsync(series.Id, progress),
                _ => await _installService.InstallAsync(series.Id, progress)
            };

            if (result.Success)
                _notificationService.NotifySuccess(Loc.Tf("S.Play.ReadyList", series.Name), result.Message);
            else
                SetPlayError(result.Message);
        }
        catch (Exception ex)
        {
            _logService.Error("Primary play action failed", ex);
            SetPlayError(ex.Message);
        }
        finally
        {
            IsPlayBusy = false;
            if (!PlayHasError && !IsGameRunning)
            {
                PlayProgressText = string.Empty;
                UpdatePlayBar();
            }
        }

        if (!PlayHasError && !IsGameRunning)
            await RefreshPlaySeriesAsync();
    }

    /// <summary>Estado de error: botón en rojo (REINTENTAR) + detalle del fallo.</summary>
    private void SetPlayError(string message)
    {
        PlayHasError = true;
        PlayStatusIsError = true;
        PlayButtonText = Loc.T("S.Retry");
        PlayStatusDetail = message;
        OnPropertyChanged(nameof(CanPlayPrimary));
        _notificationService.NotifyError(Loc.T("S.Common.Error"), message);
    }

    private DispatcherTimer? _gameWatchTimer;

    /// <summary>Estado JUGANDO: botón verde deshabilitado hasta que cierra el juego.</summary>
    private void EnterPlayingState(SeriesInfo series)
    {
        IsGameRunning = true;
        PlayButtonText = Loc.T("S.Playing");
        PlayProgressText = string.Empty;
        PlayProgress = 0;
        PlayStatusDetail = Loc.Tf("S.Play.Running", series.Name);
        OnPropertyChanged(nameof(CanPlayPrimary));

        _gameWatchTimer?.Stop();
        _gameWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _gameWatchTimer.Tick += async (_, _) =>
        {
            try
            {
                if (_gameLauncher.IsGameRunning) return;
            }
            catch
            {
                return;
            }
            _gameWatchTimer?.Stop();
            IsGameRunning = false;
            await RefreshPlaySeriesAsync();
        };
        _gameWatchTimer.Start();
    }

    [RelayCommand]
    private void Promo(string? target)
    {
        switch (target)
        {
            case "Series": _navigationService.NavigateTo(NavigationPage.Series); break;
            case "Settings": _navigationService.NavigateTo(NavigationPage.Settings); break;
            case "Updates":
                _ = CheckUpdatesAsync();
                _navigationService.NavigateTo(NavigationPage.Settings);
                break;
            default: _navigationService.NavigateTo(NavigationPage.Home); break;
        }
    }

    [RelayCommand]
    private void NavigateHome()
    {
        _navigationService.NavigateTo(NavigationPage.Home);
    }

    [RelayCommand]
    private void NavigateSeries()
    {
        _navigationService.NavigateTo(NavigationPage.Series);
    }

    [RelayCommand]
    private void NavigateGames()
    {
        _navigationService.NavigateTo(NavigationPage.Games);
    }

    [RelayCommand]
    private void NavigateServers()
    {
        _navigationService.NavigateTo(NavigationPage.Servers);
    }

    [RelayCommand]
    private void NavigateSettings()
    {
        _navigationService.NavigateTo(NavigationPage.Settings);
    }

    [RelayCommand]
    private void Minimize()
    {
        if (Application.Current.MainWindow != null)
        {
            Application.Current.MainWindow.WindowState = WindowState.Minimized;
        }
    }

    [RelayCommand]
    private void MaximizeRestore()
    {
        var window = Application.Current.MainWindow;
        if (window == null) return;

        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    [RelayCommand]
    private void Close()
    {
        Application.Current.Shutdown();
    }

    [RelayCommand]
    private async Task PlayAsync()
    {
        try
        {
            IsBusy = true;
            BusyMessage = "Preparando Minecraft...";
            StatusText = "Iniciando...";

            _logService.Info($"Starting Minecraft with version {CurrentGameVersion}");

            await Task.Delay(1500);

            StatusText = "Minecraft iniciado";
            _logService.Info("Minecraft started successfully");
        }
        catch (Exception ex)
        {
            _logService.Error("Failed to start Minecraft", ex);
            StatusText = "Error al iniciar";
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
        }
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        try
        {
            IsBusy = true;
            BusyMessage = "Buscando actualizaciones...";

            var hasUpdate = await _updateService.CheckForUpdatesAsync();
            HasUpdates = hasUpdate;

            if (hasUpdate)
            {
                UpdateStatus = "Actualización disponible";
                StatusText = "Actualización disponible";
            }
            else
            {
                UpdateStatus = "MineMount está actualizado";
                StatusText = "Actualizado";
            }
        }
        catch (Exception ex)
        {
            _logService.Error("Failed to check for updates", ex);
            UpdateStatus = "Error al buscar actualizaciones";
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
        }
    }

    [RelayCommand]
    private async Task UpdateNowAsync()
    {
        try
        {
            IsBusy = true;
            BusyMessage = "Descargando actualización...";
            IsDownloading = true;
            UpdateProgress = 0;

            var progress = new Progress<double>(p =>
            {
                UpdateProgress = p;
                UpdateStatus = $"Descargando v{_updateService.LatestVersion}... {p:F0}%";
            });

            var ok = await _updateService.DownloadUpdateAsync(progress);

            if (ok)
            {
                UpdateStatus = "Instalando actualización...";
                _logService.Info("Actualización descargada; aplicando y reiniciando");
                _updateService.ApplyAndRestart();
            }
            else
            {
                UpdateStatus = _updateService.LastError;
                StatusText = "Error de actualización";
                _logService.Warning($"Descarga de actualización falló: {_updateService.LastError}");
            }
        }
        catch (Exception ex)
        {
            _logService.Error("Update failed", ex);
            UpdateStatus = "Error al actualizar";
        }
        finally
        {
            IsDownloading = false;
            IsBusy = false;
            BusyMessage = string.Empty;
        }
    }

    [RelayCommand]
    private void Login()
    {
        _navigationService.NavigateTo(NavigationPage.Settings);
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        await _authService.LogoutAsync();
        RefreshSession();
        _logService.Info("User logged out");
    }
}

public class NewsItem
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public string ImageUrl { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool Featured { get; set; }
}
