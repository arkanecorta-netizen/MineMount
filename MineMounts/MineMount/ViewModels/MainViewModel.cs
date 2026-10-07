using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MineMount.Models;
using MineMount.Services;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

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
public class PromoItem
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string IconKey { get; set; } = "IconSparkles";
    public string Target { get; set; } = "Series";
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

    [ObservableProperty]
    private double _playProgress;

    [ObservableProperty]
    private string _playProgressText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<PromoItem> _promoItems = new();

    // ---------- Fondo animado + partículas ----------
    [ObservableProperty]
    private string _backgroundMode = "Auto";

    [ObservableProperty]
    private int _selectedBackground;

    [ObservableProperty]
    private bool _enableParticles = true;

    [ObservableProperty]
    private bool _enableAnimations = true;

    [ObservableProperty]
    private bool _windowActive = true;

    public bool ParticlesActive => EnableParticles && EnableAnimations && WindowActive
        && !string.Equals(BackgroundMode, "Desactivado", StringComparison.OrdinalIgnoreCase);

    public bool CanPlayPrimary => !IsPlayBusy
        && SelectedPlaySeries != null
        && SelectedPlaySeries.Status != SeriesStatus.ComingSoon;

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
        IAuthService authService)
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
        Notifications = new NotificationViewModel(notificationService);

        _version = updateService.CurrentVersion;
        _currentPageViewModel = _homeViewModel;

        _navigationService.Navigated += OnNavigated;
        _settingsService.SettingsChanged += (_, _) => _ = RefreshAppearanceAsync();

        BuildPromos();
        UpdateAvatar();
        _ = InitializeAsync();
    }

    partial void OnSelectedPlaySeriesChanged(SeriesInfo? value) => UpdatePlayBar();
    partial void OnUserNameChanged(string value) => UpdateAvatar();
    partial void OnWindowActiveChanged(bool value) => OnPropertyChanged(nameof(ParticlesActive));
    partial void OnEnableParticlesChanged(bool value) => OnPropertyChanged(nameof(ParticlesActive));
    partial void OnEnableAnimationsChanged(bool value) => OnPropertyChanged(nameof(ParticlesActive));
    partial void OnBackgroundModeChanged(string value) => OnPropertyChanged(nameof(ParticlesActive));

    private void UpdateAvatar()
    {
        var name = (UserName ?? string.Empty).Trim();
        AvatarUrl = name.Length > 0
            ? $"https://minotar.net/helm/{Uri.EscapeDataString(name)}/100.png"
            : string.Empty;
    }

    private void BuildPromos()
    {
        PromoItems = new ObservableCollection<PromoItem>
        {
            new() { Title = "Próximo evento", Description = "Noche de estreno · ver Series", IconKey = "IconCalendar", Target = "Series" },
            new() { Title = "Novedades", Description = "Lo último en Inicio", IconKey = "IconDiscord", Target = "Home" },
            new() { Title = "Personalizá", Description = "Fondo y partículas", IconKey = "IconSparkles", Target = "Settings" },
        };
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
            EnableParticles = s.EnableParticles;
            EnableAnimations = s.EnableAnimations;
            if (_authService.IsAuthenticated && _authService.CurrentUser != null)
            {
                UserName = _authService.CurrentUser.Name;
                IsLoggedIn = true;
            }
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
                PlayButtonText = "SIN SERIES";
                PlayStatusDetail = "No hay series en el catálogo";
            }
            else
            {
                UpdatePlayBar();
            }
        }
        catch (Exception ex)
        {
            _logService.Warning($"No se pudo cargar la serie para JUGAR: {ex.Message}");
            PlayButtonText = "JUGAR";
            PlayStatusDetail = "Conectate para ver las series";
        }
    }

    private void UpdatePlayBar()
    {
        var s = SelectedPlaySeries;
        if (s == null) return;

        PlayButtonText = s.Status switch
        {
            SeriesStatus.Installed => "JUGAR",
            SeriesStatus.NotInstalled => "INSTALAR",
            SeriesStatus.UpdateAvailable => "ACTUALIZAR",
            SeriesStatus.MissingFiles => "REPARAR",
            SeriesStatus.ComingSoon => "PRÓXIMAMENTE",
            _ => "JUGAR"
        };

        PlayStatusDetail = BuildStatusDetail(s);
        OnPropertyChanged(nameof(CanPlayPrimary));
        CurrentGameVersion = string.IsNullOrWhiteSpace(s.Definition.MinecraftVersion)
            ? CurrentGameVersion
            : s.Definition.MinecraftVersion;
    }

    /// <summary>
    /// Estado real bajo el botón (versión, loader, mods, tamaño y fecha del directorio instalado).
    /// Sin datos, muestra solo lo conocido — nunca inventa números.
    /// </summary>
    private static string BuildStatusDetail(SeriesInfo s)
    {
        var head = s.Status switch
        {
            SeriesStatus.Installed => "Instalada",
            SeriesStatus.NotInstalled => "Disponible",
            SeriesStatus.UpdateAvailable => "Actualización lista",
            SeriesStatus.MissingFiles => "Faltan archivos",
            SeriesStatus.ComingSoon => "Próximamente",
            _ => s.StatusText
        };

        var parts = new System.Collections.Generic.List<string> { head, $"v{s.Version}" };

        if (!string.IsNullOrWhiteSpace(s.Definition.MinecraftVersion))
            parts.Add(s.Definition.MinecraftVersion);
        if (!string.IsNullOrWhiteSpace(s.Definition.Loader))
            parts.Add(s.Definition.Loader);

        if (s.IsInstalled && !string.IsNullOrWhiteSpace(s.InstallPath) && Directory.Exists(s.InstallPath))
        {
            try
            {
                var modsDir = Path.Combine(s.InstallPath, "mods");
                if (Directory.Exists(modsDir))
                {
                    var jars = Directory.GetFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly).Length;
                    if (jars > 0) parts.Add($"{jars} mods");
                }

                var updated = Directory.GetLastWriteTime(s.InstallPath);
                if (updated > new DateTime(2009, 1, 1))
                    parts.Add($"Act. {updated:dd/MM/yyyy}");
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
        if (series == null || IsPlayBusy) return;
        if (series.Status == SeriesStatus.ComingSoon) return;

        IsPlayBusy = true;
        PlayProgress = 0;
        PlayProgressText = "Preparando…";

        try
        {
            if (series.Status == SeriesStatus.Installed)
            {
                var launchProgress = new Progress<double>(p =>
                {
                    PlayProgress = p;
                    PlayProgressText = $"Preparando… {p:F0}%";
                });
                var launched = await _gameLauncher.LaunchAsync(series.Id, launchProgress);
                if (!launched.Success && !string.IsNullOrEmpty(launched.Message))
                    _notificationService.NotifyError("No se pudo iniciar", launched.Message);
                return;
            }

            var progress = new Progress<SeriesProgress>(p =>
            {
                PlayProgress = p.Percent;
                PlayProgressText = $"Paso {p.StepIndex}/{p.StepCount} · {p.StepName}";
            });

            SeriesOperationResult result = series.Status switch
            {
                SeriesStatus.UpdateAvailable => await _installService.UpdateAsync(series.Id, progress),
                SeriesStatus.MissingFiles => await _installService.RepairAsync(series.Id, progress),
                _ => await _installService.InstallAsync(series.Id, progress)
            };

            if (result.Success)
                _notificationService.NotifySuccess($"{series.Name} lista", result.Message);
            else
                _notificationService.NotifyError("Operación fallida", result.Message);
        }
        catch (Exception ex)
        {
            _logService.Error("Primary play action failed", ex);
            _notificationService.NotifyError("Error", ex.Message);
        }
        finally
        {
            IsPlayBusy = false;
            PlayProgressText = string.Empty;
        }

        await RefreshPlaySeriesAsync();
    }

    [RelayCommand]
    private void Promo(string? target)
    {
        switch (target)
        {
            case "Series": _navigationService.NavigateTo(NavigationPage.Series); break;
            case "Settings": _navigationService.NavigateTo(NavigationPage.Settings); break;
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
    private void Logout()
    {
        IsLoggedIn = false;
        UserName = "Jugador";
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
