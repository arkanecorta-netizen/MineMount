using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MineMount.Services;
using System;
using System.Threading.Tasks;

using MineMount.Models;

namespace MineMount.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ILogService _logService;
    private readonly ISettingsService _settingsService;
    private readonly IAuthService _authService;
    private readonly IProfileService _profileService;

    [ObservableProperty]
    private string _userName = "Jugador";

    [ObservableProperty]
    private string _email = string.Empty;

    [ObservableProperty]
    private bool _isLoggedIn;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = "Listo";

    [ObservableProperty]
    private int _allocatedRAM = 4096;

    [ObservableProperty]
    private int _minRAM = 512;

    [ObservableProperty]
    private int _maxRAM = 16384;

    [ObservableProperty]
    private string _javaPath = string.Empty;

    [ObservableProperty]
    private string _gameDirectory = string.Empty;

    [ObservableProperty]
    private string _seriesInstallPath = string.Empty;

    [ObservableProperty]
    private bool _autoUpdate = true;

    [ObservableProperty]
    private bool _showNews = true;

    [ObservableProperty]
    private bool _enableAnimations = true;

    [ObservableProperty]
    private bool _enableSounds = true;

    [ObservableProperty]
    private string _theme = "Dark";

    [ObservableProperty]
    private string _language = "es-ES";

    // Fondo animado: "Auto" (rotación) · "Fijo" · "Desactivado" (PCs flojas)
    [ObservableProperty]
    private string _backgroundMode = "Auto";

    [ObservableProperty]
    private int _selectedBackground;

    [ObservableProperty]
    private bool _enableParticles = true;

    [ObservableProperty]
    private string _launcherVersion = "1.0.0";

    [ObservableProperty]
    private string _lastLogin = string.Empty;

    public SettingsViewModel(
        ILogService logService,
        ISettingsService settingsService,
        IAuthService authService,
        IProfileService profileService,
        IUpdateService updateService)
    {
        _logService = logService;
        _settingsService = settingsService;
        _authService = authService;
        _profileService = profileService;
        _launcherVersion = updateService.CurrentVersion;
        _ = LoadSettingsAsync();
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            IsLoading = true;
            StatusText = "Cargando configuración...";

            var settings = await _settingsService.GetSettingsAsync();

            AllocatedRAM = settings.AllocatedRAM;
            JavaPath = settings.JavaPath;
            GameDirectory = settings.GameDirectory;
            SeriesInstallPath = settings.SeriesInstallPath;
            AutoUpdate = settings.AutoUpdate;
            ShowNews = settings.ShowNews;
            EnableAnimations = settings.EnableAnimations;
            EnableSounds = settings.EnableSounds;
            Theme = settings.Theme;
            Language = settings.Language;
            BackgroundMode = string.IsNullOrWhiteSpace(settings.BackgroundMode) ? "Auto" : settings.BackgroundMode;
            SelectedBackground = settings.SelectedBackground;
            EnableParticles = settings.EnableParticles;

            IsLoggedIn = _authService.IsAuthenticated;
            if (IsLoggedIn)
            {
                UserName = _authService.CurrentUser?.Name ?? "Jugador";
                Email = _authService.CurrentUser?.Email ?? string.Empty;
                LastLogin = _authService.CurrentUser?.LastLogin.ToString("g") ?? string.Empty;
            }

            StatusText = "Configuración cargada";
        }
        catch (Exception ex)
        {
            _logService.Error("Failed to load settings", ex);
            StatusText = "Error al cargar configuración";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        try
        {
            IsLoading = true;
            StatusText = "Guardando configuración...";

            if (!string.IsNullOrWhiteSpace(SeriesInstallPath)
                && !SeriesValidation.IsValidInstallPath(SeriesInstallPath))
            {
                StatusText = "Ruta de series inválida";
                IsLoading = false;
                return;
            }

            var settings = new LauncherSettings
            {
                AllocatedRAM = AllocatedRAM,
                JavaPath = JavaPath,
                GameDirectory = GameDirectory,
                SeriesInstallPath = SeriesInstallPath,
                AutoUpdate = AutoUpdate,
                ShowNews = ShowNews,
                EnableAnimations = EnableAnimations,
                EnableSounds = EnableSounds,
                Theme = Theme,
                Language = Language,
                BackgroundMode = BackgroundMode,
                SelectedBackground = SelectedBackground,
                EnableParticles = EnableParticles
            };

            await _settingsService.SaveSettingsAsync(settings);

            StatusText = "Configuración guardada";
            _logService.Info("Settings saved successfully");
        }
        catch (Exception ex)
        {
            _logService.Error("Failed to save settings", ex);
            StatusText = "Error al guardar";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        try
        {
            IsLoading = true;
            StatusText = "Iniciando sesión...";

            var result = await _authService.LoginAsync(Email, string.Empty);

            if (result)
            {
                IsLoggedIn = true;
                UserName = _authService.CurrentUser?.Name ?? "Jugador";
                Email = _authService.CurrentUser?.Email ?? string.Empty;
                LastLogin = DateTime.Now.ToString("g");
                StatusText = "Sesión iniciada";
            }
            else
            {
                StatusText = "Error al iniciar sesión";
            }
        }
        catch (Exception ex)
        {
            _logService.Error("Failed to login", ex);
            StatusText = "Error al iniciar sesión";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        try
        {
            IsLoading = true;
            StatusText = "Cerrando sesión...";

            await _authService.LogoutAsync();

            IsLoggedIn = false;
            UserName = "Jugador";
            Email = string.Empty;
            LastLogin = string.Empty;

            StatusText = "Sesión cerrada";
        }
        catch (Exception ex)
        {
            _logService.Error("Failed to logout", ex);
            StatusText = "Error al cerrar sesión";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void BrowseJavaPath()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Java Executable (java.exe)|java.exe|All Files (*.*)|*.*",
            Title = "Seleccionar Java"
        };

        if (dialog.ShowDialog() == true)
        {
            JavaPath = dialog.FileName;
        }
    }

    [RelayCommand]
    private void BrowseGameDirectory()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Seleccionar directorio del juego"
        };

        if (dialog.ShowDialog() == true)
        {
            GameDirectory = dialog.FolderName;
        }
    }

    [RelayCommand]
    private async Task CreateProfileAsync()
    {
        try
        {
            IsLoading = true;
            StatusText = "Creando perfil...";

            var profile = new UserProfile
            {
                Name = $"Perfil {DateTime.Now:yyyy-MM-dd HH:mm}",
                CreatedAt = DateTime.Now,
                LastUsed = DateTime.Now
            };

            await _profileService.CreateProfileAsync(profile);

            StatusText = "Perfil creado";
        }
        catch (Exception ex)
        {
            _logService.Error("Failed to create profile", ex);
            StatusText = "Error al crear perfil";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void ResetSettings()
    {
        AllocatedRAM = 4096;
        JavaPath = string.Empty;
        GameDirectory = string.Empty;
        AutoUpdate = true;
        ShowNews = true;
        EnableAnimations = true;
        EnableSounds = true;
        Theme = "Dark";
        Language = "es-ES";
        BackgroundMode = "Auto";
        SelectedBackground = 0;
        EnableParticles = true;

        StatusText = "Configuración restablecida";
    }
}