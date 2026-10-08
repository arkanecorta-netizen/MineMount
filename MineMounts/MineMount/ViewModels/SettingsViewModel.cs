using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MineMount.Models;
using MineMount.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace MineMount.ViewModels;

/// <summary>Opción de fondo para la grilla (incluidas + propias).</summary>
public sealed class BackgroundOption
{
    public int Index { get; set; }
    public string Uri { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsCustom { get; set; }
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly ILogService _logService;
    private readonly ISettingsService _settingsService;
    private readonly IAuthService _authService;
    private readonly IUpdateService _updateService;
    private readonly IAppearanceService _appearanceService;
    private readonly ISystemInfoService _systemInfo;
    private readonly ISeriesService _seriesService;
    private readonly ISeriesInstallService _installService;
    private readonly IGameLauncherService _gameLauncher;
    private readonly IJavaDownloaderService _javaDownloader;
    private readonly IDiskSpaceService _diskSpace;
    private readonly ITempCleanupService _tempCleanup;

    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _savedTimer;
    private bool _loading = true;

    private static readonly HashSet<string> NoSaveProps = new(StringComparer.Ordinal)
    {
        nameof(IsLoading), nameof(SaveNoticeVisible), nameof(StatusText),
        nameof(TotalRamGB), nameof(RecommendedRamMB), nameof(AllocatedGB),
        nameof(ShowRamWarning), nameof(JavaDisplay), nameof(JavaInstalls),
        nameof(RequiredJava), nameof(IsDetectingJava), nameof(IsDownloadingJava),
        nameof(JavaProgress), nameof(SeriesUsedSize), nameof(SeriesFreeSpace),
        nameof(IsLoadingSpace), nameof(InstalledSeries), nameof(IsRepairing),
        nameof(RepairProgress), nameof(AvailableBackgrounds), nameof(CacheSize),
        nameof(IsExporting), nameof(IsCheckingUpdates), nameof(IsUpdating),
        nameof(IsDownloadingSetup), nameof(SetupProgress),
        nameof(IsUpdateAvailable), nameof(UpdateProgress), nameof(UpdateInfo), nameof(AccountAvatar),
        nameof(AccountKindLabel), nameof(SpeedLabel), nameof(AccountName)
    };

    // ---------- Cuenta ----------
    [ObservableProperty]
    private string _accountName = string.Empty;

    [ObservableProperty]
    private string _accountKindLabel = string.Empty;

    [ObservableProperty]
    private string _accountAvatar = string.Empty;

    [ObservableProperty]
    private bool _isLoggedIn;

    // ---------- Juego ----------
    [ObservableProperty]
    private long _totalRamGB;

    [ObservableProperty]
    private int _recommendedRamMB = 4096;

    [ObservableProperty]
    private int _allocatedRAM = 4096;

    [ObservableProperty]
    private int _minRAM = 1024;

    [ObservableProperty]
    private int _maxRAM = 16384;

    [ObservableProperty]
    private ObservableCollection<JavaInstall> _javaInstalls = new();

    [ObservableProperty]
    private JavaInstall? _selectedJava;

    [ObservableProperty]
    private bool _isDetectingJava;

    [ObservableProperty]
    private bool _isDownloadingJava;

    [ObservableProperty]
    private double _javaProgress;

    [ObservableProperty]
    private string _javaPath = string.Empty;

    [ObservableProperty]
    private string _gameDirectory = string.Empty;

    [ObservableProperty]
    private string _jvmArgs = string.Empty;

    [ObservableProperty]
    private bool _fullscreen;

    [ObservableProperty]
    private string _resolution = "1280x720";

    [ObservableProperty]
    private ObservableCollection<string> _resolutions = new()
    {
        "1280x720", "1366x768", "1600x900", "1920x1080", "2560x1440", "3840x2160"
    };

    [ObservableProperty]
    private bool _closeOnLaunch;

    [ObservableProperty]
    private bool _minimizeOnLaunch;

    public string AllocatedGB => $"{AllocatedRAM / 1024.0:F1} GB";
    public string RecommendedGB => $"{RecommendedRamMB / 1024.0:F1} GB";
    public bool ShowRamWarning => TotalRamGB > 0 && AllocatedRAM > TotalRamGB * 1024 * 0.75;
    public string JavaDisplay
    {
        get
        {
            if (IsDownloadingJava) return $"Descargando Java… {JavaProgress:F0}%";
            if (SelectedJava != null)
                return Loc.Tf("S.Game.JavaDetected", SelectedJava.Display, SelectedJava.Path);
            if (!string.IsNullOrWhiteSpace(JavaPath))
                return JavaPath;
            return Loc.T("S.Game.JavaNone");
        }
    }

    // ---------- Series ----------
    [ObservableProperty]
    private string _seriesInstallPath = string.Empty;

    [ObservableProperty]
    private string _seriesUsedSize = "—";

    [ObservableProperty]
    private string _seriesFreeSpace = "—";

    [ObservableProperty]
    private bool _isLoadingSpace;

    [ObservableProperty]
    private ObservableCollection<SeriesInfo> _installedSeries = new();

    [ObservableProperty]
    private SeriesInfo? _selectedRepairSeries;

    [ObservableProperty]
    private bool _isRepairing;

    [ObservableProperty]
    private double _repairProgress;

    [ObservableProperty]
    private string _repairProgressText = string.Empty;

    // ---------- Fondo ----------
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
    private ObservableCollection<BackgroundOption> _availableBackgrounds = new();

    // ---------- Apariencia ----------
    [ObservableProperty]
    private bool _enableAnimations = true;

    [ObservableProperty]
    private bool _enableParticles = true;

    [ObservableProperty]
    private bool _enableSounds = true;

    [ObservableProperty]
    private int _clickVolume = 30;

    [ObservableProperty]
    private string _theme = "Dark";

    [ObservableProperty]
    private string _language = "es-ES";

    public string SpeedLabel => DownloadSpeedLimitKBps <= 0
        ? Loc.T("S.Dl.Unlimited")
        : Loc.Tf("S.Dl.SpeedVal", DownloadSpeedLimitKBps);

    // ---------- Descargas ----------
    [ObservableProperty]
    private int _downloadSpeedLimitKBps;

    [ObservableProperty]
    private int _maxConcurrentDownloads = 2;

    // ---------- Estado ----------
    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _saveNoticeVisible;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _cacheSize = "—";

    [ObservableProperty]
    private bool _isExporting;

    [ObservableProperty]
    private string _launcherVersion = "1.0.0";

    [ObservableProperty]
    private string _updateInfo = string.Empty;

    [ObservableProperty]
    private bool _isCheckingUpdates;

    [ObservableProperty]
    private bool _isDownloadingSetup;

    [ObservableProperty]
    private double _setupProgress;

    [ObservableProperty]
    private bool _isUpdating;

    [ObservableProperty]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private double _updateProgress;

    [ObservableProperty]
    private int _requiredJava = 17;

    public SettingsViewModel(
        ILogService logService,
        ISettingsService settingsService,
        IAuthService authService,
        IUpdateService updateService,
        IAppearanceService appearanceService,
        ISystemInfoService systemInfo,
        ISeriesService seriesService,
        ISeriesInstallService installService,
        IGameLauncherService gameLauncher,
        IJavaDownloaderService javaDownloader,
        IDiskSpaceService diskSpace,
        ITempCleanupService tempCleanup)
    {
        _logService = logService;
        _settingsService = settingsService;
        _authService = authService;
        _updateService = updateService;
        _appearanceService = appearanceService;
        _systemInfo = systemInfo;
        _seriesService = seriesService;
        _installService = installService;
        _gameLauncher = gameLauncher;
        _javaDownloader = javaDownloader;
        _diskSpace = diskSpace;
        _tempCleanup = tempCleanup;
        _launcherVersion = updateService.CurrentVersion;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveTimer.Tick += async (_, _) =>
        {
            _saveTimer.Stop();
            await SaveAllAsync();
        };
        _savedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _savedTimer.Tick += (_, _) =>
        {
            _savedTimer.Stop();
            SaveNoticeVisible = false;
        };

        PropertyChanged += (_, e) =>
        {
            if (_loading || e.PropertyName == null || NoSaveProps.Contains(e.PropertyName))
                return;
            _saveTimer.Stop();
            _saveTimer.Start();
        };

        _authService.SessionChanged += (_, _) => RefreshAccount();
        _appearanceService.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(AllocatedGB));
            OnPropertyChanged(nameof(JavaDisplay));
            OnPropertyChanged(nameof(SpeedLabel));
            RefreshBackgroundList();
        };

        _ = LoadAllAsync();
    }

    // ================================================================
    //  Carga
    // ================================================================
    private async Task LoadAllAsync()
    {
        _loading = true;
        IsLoading = true;
        try
        {
            var s = await _settingsService.GetSettingsAsync();

            AllocatedRAM = s.AllocatedRAM <= 0 ? 4096 : s.AllocatedRAM;
            JavaPath = s.JavaPath;
            GameDirectory = string.IsNullOrWhiteSpace(s.GameDirectory)
                ? MinecraftInstallService.DefaultGameDirectory()
                : s.GameDirectory;
            // La ruta de series nunca se muestra vacía: se ve la efectiva
            SeriesInstallPath = string.IsNullOrWhiteSpace(s.SeriesInstallPath)
                ? System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MineMount", "Series")
                : s.SeriesInstallPath;
            EnableAnimations = s.EnableAnimations;
            EnableSounds = s.EnableSounds;
            ClickVolume = Math.Clamp(s.ClickVolume, 0, 100);
            Theme = s.Theme;
            Language = s.Language;
            BackgroundMode = string.IsNullOrWhiteSpace(s.BackgroundMode) ? "Auto" : s.BackgroundMode;
            SelectedBackground = Math.Max(0, s.SelectedBackground);
            BackgroundInterval = Math.Clamp(s.BackgroundIntervalSeconds, 2, 20);
            BackgroundDim = Math.Clamp(s.BackgroundDim, 0, 80);
            BackgroundBlur = Math.Clamp(s.BackgroundBlur, 0, 20);
            EnableParticles = s.EnableParticles;
            DownloadSpeedLimitKBps = Math.Max(0, s.DownloadSpeedLimitKBps);
            MaxConcurrentDownloads = Math.Clamp(s.MaxConcurrentDownloads, 1, 4);
            JvmArgs = s.JvmArgs;
            Fullscreen = s.Fullscreen;
            Resolution = string.IsNullOrWhiteSpace(s.Resolution) ? "1280x720" : s.Resolution;
            CloseOnLaunch = s.CloseOnLaunch;
            MinimizeOnLaunch = s.MinimizeOnLaunch;

            TotalRamGB = Math.Max(1, _systemInfo.GetTotalRamMB() / 1024);
            RecommendedRamMB = (int)Math.Clamp(TotalRamGB * 1024 / 2, 2048, 8192);
            MaxRAM = (int)Math.Min(32768, Math.Max(4096, TotalRamGB * 1024));
            OnPropertyChanged(nameof(AllocatedGB));
            OnPropertyChanged(nameof(ShowRamWarning));

            RefreshAccount();
            RefreshBackgroundList();
            _ = RefreshSpaceAsync();
            _ = RefreshCacheSizeAsync();
            _ = DetectJavaAsync();
            _ = RefreshInstalledSeriesAsync();
        }
        catch (Exception ex)
        {
            _logService.Error("Failed to load settings", ex);
        }
        finally
        {
            _loading = false;
            IsLoading = false;
        }
    }

    partial void OnAllocatedRAMChanged(int value)
    {
        OnPropertyChanged(nameof(AllocatedGB));
        OnPropertyChanged(nameof(ShowRamWarning));
    }

    partial void OnSelectedJavaChanged(JavaInstall? value)
    {
        OnPropertyChanged(nameof(JavaDisplay));
    }

    partial void OnDownloadSpeedLimitKBpsChanged(int value)
    {
        OnPropertyChanged(nameof(SpeedLabel));
    }

    // ================================================================
    //  Autoguardado con aviso discreto
    // ================================================================
    private async Task SaveAllAsync()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(SeriesInstallPath)
                && !SeriesValidation.IsValidInstallPath(SeriesInstallPath))
            {
                return;
            }

            var s = await _settingsService.GetSettingsAsync();
            s.AllocatedRAM = AllocatedRAM;
            s.JavaPath = JavaPath;
            s.GameDirectory = GameDirectory;
            s.SeriesInstallPath = SeriesInstallPath;
            s.EnableAnimations = EnableAnimations;
            s.EnableSounds = EnableSounds;
            s.ClickVolume = Math.Clamp(ClickVolume, 0, 100);
            s.BackgroundMode = BackgroundMode;
            s.SelectedBackground = SelectedBackground;
            s.BackgroundIntervalSeconds = Math.Clamp(BackgroundInterval, 2, 20);
            s.BackgroundDim = Math.Clamp(BackgroundDim, 0, 80);
            s.BackgroundBlur = Math.Clamp(BackgroundBlur, 0, 20);
            s.EnableParticles = EnableParticles;
            s.DownloadSpeedLimitKBps = Math.Max(0, DownloadSpeedLimitKBps);
            s.MaxConcurrentDownloads = Math.Clamp(MaxConcurrentDownloads, 1, 4);
            s.JvmArgs = JvmArgs?.Trim() ?? string.Empty;
            s.Fullscreen = Fullscreen;
            s.Resolution = Resolution;
            s.CloseOnLaunch = CloseOnLaunch;
            s.MinimizeOnLaunch = MinimizeOnLaunch;

            // Solo modo oscuro. El idioma se aplica en vivo.
            var langChanged = !string.Equals(s.Language, Language, StringComparison.OrdinalIgnoreCase);
            s.Theme = "Dark";
            s.Language = Language;

            await _settingsService.SaveSettingsAsync(s);

            if (langChanged) await _appearanceService.SetLanguageAsync(Language);

            SaveNoticeVisible = true;
            _savedTimer.Stop();
            _savedTimer.Start();
        }
        catch (Exception ex)
        {
            _logService.Error("Failed to autosave settings", ex);
        }
    }

    // ================================================================
    //  Cuenta
    // ================================================================
    private void RefreshAccount()
    {
        var session = _authService.CurrentSession;
        IsLoggedIn = session != null;
        AccountName = session?.Name ?? string.Empty;
        AccountKindLabel = session == null
            ? string.Empty
            : session.Kind == AccountKind.Microsoft
                ? Loc.T("S.Auth.Microsoft")
                : Loc.T("S.Auth.Offline");
        AccountAvatar = session == null
            ? string.Empty
            : session.Kind == AccountKind.Microsoft && !string.IsNullOrWhiteSpace(session.Uuid)
                ? $"https://minotar.net/helm/{Uri.EscapeDataString(session.Uuid)}/100.png"
                : $"https://minotar.net/helm/{Uri.EscapeDataString(session.Name)}/100.png";
    }

    [RelayCommand]
    private async Task ChangeAccountAsync()
    {
        var window = new Views.WelcomeWindow(
            new WelcomeViewModel(_authService,
                App.GetService<IMicrosoftAuthService>(), _logService));
        window.ShowDialog();
        await window.ViewModel.Done.Task;
        RefreshAccount();
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        await _authService.LogoutAsync();
        RefreshAccount();

        var window = new Views.WelcomeWindow(
            new WelcomeViewModel(_authService,
                App.GetService<IMicrosoftAuthService>(), _logService));
        window.ShowDialog();
        var session = await window.ViewModel.Done.Task;
        if (session == null)
        {
            System.Windows.Application.Current.Shutdown();
            return;
        }
        RefreshAccount();
    }

    // ================================================================
    //  Juego: RAM / Java / carpeta / extras
    // ================================================================
    [RelayCommand]
    private void SetAutoRam()
    {
        AllocatedRAM = RecommendedRamMB;
    }

    [RelayCommand]
    private async Task DetectJavaAsync()
    {
        if (IsDetectingJava) return;
        IsDetectingJava = true;
        OnPropertyChanged(nameof(JavaDisplay));
        try
        {
            var list = await _gameLauncher.ListJavaAsync();
            JavaInstalls = new ObservableCollection<JavaInstall>(list);

            var mc = (await _seriesService.GetSeriesAsync()).FirstOrDefault()?.Definition.MinecraftVersion;
            RequiredJava = _gameLauncher.GetRequiredJava(string.IsNullOrWhiteSpace(mc) ? "1.20.1" : mc);

            SelectedJava = list.FirstOrDefault(j => j.Major >= RequiredJava)
                ?? list.FirstOrDefault();

            if (SelectedJava != null && string.IsNullOrWhiteSpace(JavaPath))
                JavaPath = SelectedJava.Path;
        }
        catch (Exception ex)
        {
            _logService.Warning($"Detección de Java falló: {ex.Message}");
        }
        finally
        {
            IsDetectingJava = false;
            OnPropertyChanged(nameof(JavaDisplay));
        }
    }

    [RelayCommand]
    private async Task DownloadJavaAsync()
    {
        if (IsDownloadingJava) return;
        IsDownloadingJava = true;
        OnPropertyChanged(nameof(JavaDisplay));
        try
        {
            var progress = new Progress<double>(p =>
            {
                JavaProgress = p;
                OnPropertyChanged(nameof(JavaDisplay));
            });
            var path = await _javaDownloader.DownloadJavaAsync(
                RequiredJava, progress, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(path))
            {
                JavaPath = path;
                await DetectJavaAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelado
        }
        catch (Exception ex)
        {
            _logService.Error("Descarga de Java falló", ex);
        }
        finally
        {
            IsDownloadingJava = false;
            OnPropertyChanged(nameof(JavaDisplay));
        }
    }

    [RelayCommand]
    private void BrowseJava()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Java (java.exe, javaw.exe)|java.exe;javaw.exe|All Files (*.*)|*.*",
            Title = "Seleccionar Java"
        };
        if (dialog.ShowDialog() == true)
        {
            JavaPath = dialog.FileName;
            SelectedJava = null;
            OnPropertyChanged(nameof(JavaDisplay));
        }
    }

    [RelayCommand]
    private void OpenGameDir()
    {
        try
        {
            var dir = string.IsNullOrWhiteSpace(GameDirectory)
                ? MinecraftInstallService.DefaultGameDirectory()
                : GameDirectory;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logService.Warning($"No se pudo abrir la carpeta: {ex.Message}");
        }
    }

    [RelayCommand]
    private void BrowseGameDir()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Seleccionar directorio del juego"
        };
        if (dialog.ShowDialog() == true)
            GameDirectory = dialog.FolderName;
    }

    [RelayCommand]
    private void ResetGame()
    {
        AllocatedRAM = RecommendedRamMB;
        JavaPath = string.Empty;
        GameDirectory = MinecraftInstallService.DefaultGameDirectory();
        JvmArgs = string.Empty;
        Fullscreen = false;
        Resolution = "1280x720";
        CloseOnLaunch = false;
        MinimizeOnLaunch = false;
        _ = DetectJavaAsync();
    }

    // ================================================================
    //  Series: espacio, mover, reparar por serie
    // ================================================================
    private async Task RefreshSpaceAsync()
    {
        IsLoadingSpace = true;
        try
        {
            var root = await _seriesService.GetInstallRootAsync();
            var used = await SystemInfoService.GetDirectorySizeAsync(root);
            SeriesUsedSize = _diskSpace.FormatSize(used);
            var freeMB = _diskSpace.GetFreeSpaceMB(root);
            SeriesFreeSpace = freeMB < 0 ? "—" : _diskSpace.FormatSize(freeMB * 1024 * 1024);
        }
        catch
        {
            SeriesUsedSize = "—";
            SeriesFreeSpace = "—";
        }
        finally
        {
            IsLoadingSpace = false;
        }
    }

    private async Task RefreshInstalledSeriesAsync()
    {
        try
        {
            var all = await _seriesService.GetSeriesAsync();
            InstalledSeries = new ObservableCollection<SeriesInfo>(all.Where(s => s.IsInstalled));
            SelectedRepairSeries ??= InstalledSeries.FirstOrDefault();
        }
        catch
        {
            InstalledSeries = new ObservableCollection<SeriesInfo>();
        }
    }

    [RelayCommand]
    private void OpenSeriesFolder()
    {
        try
        {
            var task = _seriesService.GetInstallRootAsync();
            task.Wait(TimeSpan.FromSeconds(10));
            Directory.CreateDirectory(task.Result);
            Process.Start(new ProcessStartInfo(task.Result) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logService.Warning($"No se pudo abrir la carpeta: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task MoveInstallationAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Elegir nueva ubicación de las series"
        };
        if (dialog.ShowDialog() != true) return;

        var confirm = System.Windows.MessageBox.Show(
            Loc.T("S.SeriesS.MoveConfirm"),
            Loc.T("S.SeriesS.Move"),
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        IsLoading = true;
        try
        {
            var source = await _seriesService.GetInstallRootAsync();
            var dest = Path.Combine(dialog.FolderName, "Series");

            var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
            long bytes = 0;
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(source, file);
                var target = Path.Combine(dest, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
                bytes += new FileInfo(target).Length;
            }

            // Verificar: misma cantidad de archivos y bytes
            var copied = Directory.GetFiles(dest, "*", SearchOption.AllDirectories);
            long copiedBytes = copied.Sum(f => new FileInfo(f).Length);
            if (copied.Length != files.Length || copiedBytes != bytes)
                throw new IOException("La verificación falló: faltan archivos.");

            Directory.Delete(source, recursive: true);

            SeriesInstallPath = dest;
            await SaveAllAsync();
            await RefreshSpaceAsync();
            _logService.Info(Loc.T("S.SeriesS.Moved"));
        }
        catch (Exception ex)
        {
            _logService.Error("Mover instalación falló", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RepairSeriesAsync()
    {
        var series = SelectedRepairSeries;
        if (series == null || IsRepairing) return;

        IsRepairing = true;
        RepairProgress = 0;
        RepairProgressText = Loc.T("S.Play.Preparing");
        try
        {
            var progress = new Progress<SeriesProgress>(p =>
            {
                RepairProgress = p.Percent;
                RepairProgressText = Loc.Tf("S.Play.Step", p.StepIndex, p.StepCount, p.StepName, p.Percent);
            });
            var result = await _installService.RepairAsync(series.Id, progress);
            RepairProgressText = result.Message;
            await RefreshInstalledSeriesAsync();
            await RefreshSpaceAsync();
        }
        catch (Exception ex)
        {
            _logService.Error("Reparación falló", ex);
            RepairProgressText = ex.Message;
        }
        finally
        {
            IsRepairing = false;
        }
    }

    [RelayCommand]
    private void ResetSeriesSection()
    {
        SeriesInstallPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MineMount", "Series");
        _ = RefreshSpaceAsync();
    }

    // ================================================================
    //  Fondo
    // ================================================================
    private void RefreshBackgroundList()
    {
        try
        {
            var list = new ObservableCollection<BackgroundOption>();
            var i = 0;
            foreach (var item in BackgroundCatalog.Items)
            {
                list.Add(new BackgroundOption
                {
                    Index = i++,
                    Uri = item.PackUri,
                    Name = item.DisplayName,
                    IsCustom = false
                });
            }

            var s = _settingsService.GetSettingsAsync().GetAwaiter().GetResult();
            foreach (var path in s.CustomBackgrounds ?? new())
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                    list.Add(new BackgroundOption
                    {
                        Index = i++,
                        Uri = path,
                        Name = Path.GetFileNameWithoutExtension(path),
                        IsCustom = true
                    });
                }
                catch
                {
                    // Entrada inválida: se saltea
                }
            }

            AvailableBackgrounds = list;
            if (SelectedBackground >= list.Count)
                SelectedBackground = 0;
        }
        catch (Exception ex)
        {
            _logService.Warning($"Lista de fondos falló: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task AddMyImageAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Imágenes|*.png;*.jpg;*.jpeg;*.bmp;*.webp",
            Title = "Elegir imagen de fondo"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var destDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MineMount", "Backgrounds");
            Directory.CreateDirectory(destDir);

            var dest = Path.Combine(destDir, Path.GetFileName(dialog.FileName));
            File.Copy(dialog.FileName, dest, overwrite: true);

            var s = await _settingsService.GetSettingsAsync();
            if (!s.CustomBackgrounds.Contains(dest, StringComparer.OrdinalIgnoreCase))
                s.CustomBackgrounds.Add(dest);
            await _settingsService.SaveSettingsAsync(s);

            RefreshBackgroundList();
            SelectedBackground = AvailableBackgrounds.Count - 1;
            BackgroundMode = "Fijo";
        }
        catch (Exception ex)
        {
            _logService.Error("No se pudo agregar la imagen", ex);
        }
    }

    [RelayCommand]
    private void ResetBackground()
    {
        BackgroundMode = "Auto";
        SelectedBackground = 0;
        BackgroundInterval = 8;
        BackgroundDim = 25;
        BackgroundBlur = 0;
    }

    // ================================================================
    //  Apariencia / Descargas
    // ================================================================
    [RelayCommand]
    private void ResetAppearance()
    {
        EnableAnimations = true;
        EnableParticles = true;
        EnableSounds = true;
        ClickVolume = 30;
        Theme = "Dark";
        Language = "es-ES";
    }

    [RelayCommand]
    private void ResetDownloads()
    {
        DownloadSpeedLimitKBps = 0;
        MaxConcurrentDownloads = 2;
    }

    // ================================================================
    //  Diagnóstico
    // ================================================================
    private async Task RefreshCacheSizeAsync()
    {
        try
        {
            var cache = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MineMount", "cache");
            var bytes = await SystemInfoService.GetDirectorySizeAsync(cache);
            CacheSize = _diskSpace.FormatSize(bytes);
        }
        catch
        {
            CacheSize = "—";
        }
    }

    [RelayCommand]
    private void ViewLogs()
    {
        try
        {
            new Views.LogViewer(null).Show();
        }
        catch (Exception ex)
        {
            _logService.Warning($"No se pudo abrir el visor: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task CleanCacheAsync()
    {
        IsLoading = true;
        try
        {
            var cache = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MineMount", "cache");
            var before = await SystemInfoService.GetDirectorySizeAsync(cache);

            await _tempCleanup.CleanupStartupAsync();

            if (Directory.Exists(cache))
            {
                foreach (var file in Directory.EnumerateFiles(cache, "*.tmp", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // En uso: se saltea
                    }
                }
            }

            var after = await SystemInfoService.GetDirectorySizeAsync(cache);
            await RefreshCacheSizeAsync();
            StatusText = Loc.Tf("S.Diag.Cleaned", _diskSpace.FormatSize(Math.Max(0, before - after)));
        }
        catch (Exception ex)
        {
            _logService.Error("Limpieza falló", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "ZIP|*.zip",
            FileName = "minemount-diagnostico.zip",
            Title = "Exportar diagnóstico"
        };
        if (dialog.ShowDialog() != true) return;

        IsExporting = true;
        try
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MineMount");
            var stage = Path.Combine(Path.GetTempPath(), "MineMount", "diag");
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
            Directory.CreateDirectory(stage);

            var log = Path.Combine(appData, "minemount.log");
            if (File.Exists(log)) File.Copy(log, Path.Combine(stage, "minemount.log"), true);

            var settingsPath = Path.Combine(appData, "settings.json");
            if (File.Exists(settingsPath)) File.Copy(settingsPath, Path.Combine(stage, "settings.json"), true);

            var series = await _seriesService.GetSeriesAsync();
            var info = new System.Text.StringBuilder();
            info.AppendLine($"MineMount {LauncherVersion} · {DateTime.Now:yyyy-MM-dd HH:mm}");
            info.AppendLine($"SO: {_systemInfo.GetOsDescription()}");
            info.AppendLine($"CPU: {_systemInfo.GetCpuName()}");
            info.AppendLine($"RAM: {_systemInfo.GetTotalRamMB() / 1024} GB");
            foreach (var gpu in _systemInfo.GetGpuNames())
                info.AppendLine($"GPU: {gpu}");
            info.AppendLine($"Disco: {_systemInfo.GetAppDataInfo()}");
            info.AppendLine($"Cuenta: {AccountName} ({AccountKindLabel})");
            info.AppendLine($"Series: {series.Count}");
            foreach (var s in series)
                info.AppendLine($"  - {s.Id} v{s.Version} [{s.Status}]");
            await File.WriteAllTextAsync(Path.Combine(stage, "sysinfo.txt"), info.ToString());

            if (File.Exists(dialog.FileName)) File.Delete(dialog.FileName);
            ZipFile.CreateFromDirectory(stage, dialog.FileName);
            Directory.Delete(stage, recursive: true);

            StatusText = Loc.T("S.Diag.Exported");
            _logService.Info($"Diagnóstico exportado: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            _logService.Error("Exportar diagnóstico falló", ex);
        }
        finally
        {
            IsExporting = false;
        }
    }

    // ================================================================
    //  Acerca de
    // ================================================================
    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (IsCheckingUpdates || IsUpdating) return;
        IsCheckingUpdates = true;
        try
        {
            var has = await _updateService.CheckForUpdatesAsync();
            IsUpdateAvailable = has;
            UpdateInfo = has
                ? Loc.Tf("S.About.Available", _updateService.LatestVersion) + "\n\n" + _updateService.ReleaseNotes
                : Loc.T("S.About.UpToDate");
        }
        catch (Exception ex)
        {
            UpdateInfo = ex.Message;
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    [RelayCommand]
    private async Task DownloadSetupAsync()
    {
        if (IsDownloadingSetup) return;
        IsDownloadingSetup = true;
        SetupProgress = 0;
        try
        {
            var progress = new Progress<double>(p => SetupProgress = p);
            var path = await _updateService.DownloadSetupAsync(progress);
            if (!string.IsNullOrWhiteSpace(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            else
            {
                UpdateInfo = _updateService.LastError;
            }
        }
        catch (Exception ex)
        {
            UpdateInfo = ex.Message;
        }
        finally
        {
            IsDownloadingSetup = false;
        }
    }

    [RelayCommand]
    private async Task UpdateNowAsync()
    {
        if (IsUpdating) return;
        IsUpdating = true;
        UpdateProgress = 0;
        try
        {
            var progress = new Progress<double>(p => UpdateProgress = p);
            if (await _updateService.DownloadUpdateAsync(progress))
                _updateService.ApplyAndRestart();
            else
                UpdateInfo = _updateService.LastError;
        }
        catch (Exception ex)
        {
            UpdateInfo = ex.Message;
        }
        finally
        {
            IsUpdating = false;
        }
    }
}
