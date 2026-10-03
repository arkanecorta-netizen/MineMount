using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MineMount.Services;
using System;
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

public partial class MainViewModel : ObservableObject
{
    private readonly INavigationService _navigationService;
    private readonly ISettingsService _settingsService;
    private readonly IUpdateService _updateService;
    private readonly ILogService _logService;
    private readonly INotificationService _notificationService;

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
        SeriesDetailViewModel seriesDetailViewModel)
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
        Notifications = new NotificationViewModel(notificationService);

        _version = updateService.CurrentVersion;
        _currentPageViewModel = _homeViewModel;

        _navigationService.Navigated += OnNavigated;
        _ = InitializeAsync();
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
                break;
            case NavigationPage.SeriesDetail:
                _ = _seriesDetailViewModel.LoadAsync(e.Parameter as string);
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