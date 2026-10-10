using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MineMount.Models;

namespace MineMount.Services;

public class SeriesLaunchConfig
{
    public string SeriesId { get; set; } = string.Empty;
    public string SeriesDir { get; set; } = string.Empty;
    public string MinecraftVersion { get; set; } = string.Empty;
    public string Loader { get; set; } = string.Empty;
    public string JavaPath { get; set; } = string.Empty;
    public int MinRAM { get; set; } = 2048;
    public int MaxRAM { get; set; } = 4096;
    public string Classpath { get; set; } = string.Empty;
    /// <summary>
    /// Lanzamiento moderno (Forge &gt;= 1.17). Si es false se usa el
    /// launchwrapper clásico (series vanilla/antiguas).
    /// </summary>
    public bool UseBootstrap { get; set; }
    /// <summary>
    /// Argumentos JVM del version.json de Forge ya con placeholders
    /// resueltos (-p module-path, --add-modules, --add-opens...).
    /// </summary>
    public string ForgeJvmArgs { get; set; } = string.Empty;
    /// <summary>
    /// Argumentos de juego del version.json de Forge (--launchTarget, --fml.*).
    /// </summary>
    public string ForgeGameArgs { get; set; } = string.Empty;
    /// <summary>
    /// Id de versión de Forge (p.ej. 1.20.1-forge-47.2.0).
    /// </summary>
    public string ForgeVersionId { get; set; } = string.Empty;
    public string NativesDir { get; set; } = string.Empty;
    public string GameDir { get; set; } = string.Empty;
    public string AssetsDir { get; set; } = string.Empty;
    public string AssetIndex { get; set; } = string.Empty;
    public string Username { get; set; } = "Player";
    public string UUID { get; set; } = string.Empty;
    public string AccessToken { get; set; } = "0";
    public string UserType { get; set; } = "legacy";
    public string JvmArgs { get; set; } = string.Empty;
    public bool Fullscreen { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool MinimizeLauncher { get; set; }
}

public sealed class JavaInstall
{
    public string Path { get; set; } = string.Empty;
    public int Major { get; set; }
    public string Display { get; set; } = string.Empty;
}

public sealed class GameCrashedArgs : EventArgs
{
    public string SeriesId { get; set; } = string.Empty;
    public string SeriesName { get; set; } = string.Empty;
    public int ExitCode { get; set; }
    public string LogPath { get; set; } = string.Empty;
}

public class LaunchResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public Process? Process { get; set; }
}

public interface IGameLauncherService
{
    Task<string?> FindJavaAsync(int minMajorVersion = 0);
    Task<List<JavaInstall>> ListJavaAsync();
    int GetRequiredJava(string minecraftVersion);
    Task<bool> VerifyInstallationAsync(string seriesId);
    Task<LaunchResult> LaunchAsync(string seriesId, IProgress<double>? progress = null);
    bool IsGameRunning { get; }
    event EventHandler<GameCrashedArgs>? GameCrashed;
}

// Lanzador de series. El ViewModel NO ejecuta Minecraft directamente:
// pasa por este servicio, que verifica, instala lo necesario, construye
// la configuración y registra el proceso sin bloquear la interfaz.
public class GameLauncherService : IGameLauncherService
{
    private readonly ISeriesService _seriesService;
    private readonly ISeriesStorageService _storageService;
    private readonly ISettingsService _settingsService;
    private readonly ILogService _logService;
    private readonly INotificationService _notificationService;
    private readonly IMinecraftInstallService _minecraftInstall;
    private readonly IForgeInstallService _forgeInstall;
    private readonly IAuthService _authService;

    private Process? _runningProcess;
    private string _lastSeriesId = string.Empty;
    private string _lastSeriesName = string.Empty;
    private string _lastGameDir = string.Empty;

    public bool IsGameRunning => _runningProcess is { HasExited: false };

    public event EventHandler<GameCrashedArgs>? GameCrashed;

    public GameLauncherService(
        ISeriesService seriesService,
        ISeriesStorageService storageService,
        ISettingsService settingsService,
        ILogService logService,
        INotificationService notificationService,
        IMinecraftInstallService minecraftInstall,
        IForgeInstallService forgeInstall,
        IAuthService authService)
    {
        _seriesService = seriesService;
        _storageService = storageService;
        _settingsService = settingsService;
        _logService = logService;
        _notificationService = notificationService;
        _minecraftInstall = minecraftInstall;
        _forgeInstall = forgeInstall;
        _authService = authService;
    }

    // ---------------------------------------------------------------
    //  Detección de Java: ruta configurada → JAVA_HOME → PATH →
    //  ubicaciones comunes. Permite exigir una versión mínima
    //  (Forge 1.20.1 necesita Java 17).
    // ---------------------------------------------------------------
    public async Task<string?> FindJavaAsync(int minMajorVersion = 0)
    {
        var candidates = new List<string>();

        var settings = await _settingsService.GetSettingsAsync();
        if (!string.IsNullOrWhiteSpace(settings.JavaPath))
        {
            candidates.Add(ResolveJavaExecutable(settings.JavaPath));
        }

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            candidates.Add(Path.Combine(javaHome, "bin", "java.exe"));
        }

        candidates.Add("java");

        var programFiles = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
        };

        foreach (var baseDir in programFiles.Where(d => !string.IsNullOrWhiteSpace(d)))
        {
            foreach (var dir in SafeEnumerateDirectories(baseDir, "Java", "Eclipse Adoptium", "Microsoft", "Amazon Corretto", "Zulu", "BellSoft"))
            {
                candidates.Add(Path.Combine(dir, "bin", "java.exe"));
            }
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var version = await GetJavaVersionAsync(candidate);
            if (version < minMajorVersion) continue;

            if (candidate == "java")
            {
                if (version >= minMajorVersion) return "java";
                continue;
            }

            var full = Path.GetFullPath(candidate);
            if (File.Exists(full))
            {
                _logService.Info($"Java encontrado: {full} (v{version})");
                return full;
            }
        }

        return null;
    }

    // ---------------------------------------------------------------
    //  Listado de Javas para la UI + Java requerido por versión de MC:
    //  <=1.16 → 8 · 1.17–1.20.4 → 17 · >=1.20.5 → 21.
    // ---------------------------------------------------------------
    public int GetRequiredJava(string minecraftVersion)
    {
        try
        {
            var parts = (minecraftVersion ?? string.Empty).Trim().Split('.');
            if (parts.Length >= 2
                && int.TryParse(parts[0], out var major)
                && int.TryParse(parts[1], out var minor))
            {
                if (major > 1) return 21;
                if (major == 1 && minor > 20) return 21;
                if (major == 1 && minor == 20 && MinorPatchAtLeast(parts, 5))
                    return 21;
                if (major == 1 && minor >= 17)
                    return 17;
                return 8;
            }
        }
        catch
        {
            // Versión ilegible: pedir 17 (caso más común)
        }
        return 17;
    }

    private static bool MinorPatchAtLeast(string[] parts, int patch)
    {
        return parts.Length >= 3 && int.TryParse(parts[2], out var p) && p >= patch;
    }

    public async Task<List<JavaInstall>> ListJavaAsync()
    {
        var found = new List<JavaInstall>();
        foreach (var candidate in await GetJavaCandidates())
        {
            var (major, display) = await GetJavaDisplayAsync(candidate);
            if (major <= 0) continue;

            var path = candidate == "java" ? "java" : Path.GetFullPath(candidate);
            if (candidate != "java" && !File.Exists(path)) continue;
            if (found.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))) continue;

            found.Add(new JavaInstall
            {
                Path = path,
                Major = major,
                Display = $"Java {display}"
            });
        }

        return found
            .OrderBy(f => f.Major) // ascendente: el primero que cumpla es el más cercano
            .ThenBy(f => f.Path)
            .ToList();
    }

    private async Task<List<string>> GetJavaCandidates()
    {
        var candidates = new List<string>();

        var settings = await _settingsService.GetSettingsAsync();
        if (!string.IsNullOrWhiteSpace(settings.JavaPath))
        {
            candidates.Add(ResolveJavaExecutable(settings.JavaPath));
        }

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            candidates.Add(Path.Combine(javaHome, "bin", "java.exe"));
        }

        candidates.Add("java");

        var programFiles = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
        };

        foreach (var baseDir in programFiles.Where(d => !string.IsNullOrWhiteSpace(d)))
        {
            foreach (var dir in SafeEnumerateDirectories(baseDir, "Java", "Eclipse Adoptium", "Microsoft", "Amazon Corretto", "Zulu", "BellSoft", "Eclipse Foundation", "AdoptOpenJDK"))
            {
                candidates.Add(Path.Combine(dir, "bin", "java.exe"));
            }
        }

        // Javas descargados por el propio launcher
        var ownJava = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MineMount", "Java");
        if (Directory.Exists(ownJava))
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(ownJava))
                {
                    candidates.Add(Path.Combine(dir, "bin", "javaw.exe"));
                    candidates.Add(Path.Combine(dir, "bin", "java.exe"));
                }
            }
            catch
            {
                // Sin permiso: se ignora
            }
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<(int Major, string Display)> GetJavaDisplayAsync(string javaPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = javaPath == "java" ? "java" : javaPath,
                Arguments = "-version",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi);
            if (p == null) return (0, string.Empty);

            // Sin bloquear el hilo UI: WaitForExitAsync con timeout
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var outputTask = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync(cts.Token);
            var output = await outputTask;

            // openjdk version "17.0.11" ... / java version "1.8.0_412"
            var match = System.Text.RegularExpressions.Regex.Match(output, "version \"([\\d._]+)\"");
            if (!match.Success) return (0, string.Empty);

            var raw = match.Groups[1].Value;
            var first = raw.Split('.')[0];
            var major = first == "1" && raw.Contains('.')
                ? int.Parse(raw.Split('.')[1])
                : int.Parse(first);
            return (major, raw);
        }
        catch
        {
            return (0, string.Empty);
        }
    }
    private static string ResolveJavaExecutable(string path)
    {
        if (File.Exists(path)) return path;
        if (Directory.Exists(path)) return Path.Combine(path, "bin", "java.exe");
        return path;
    }

    private static async Task<int> GetJavaVersionAsync(string javaPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = javaPath == "java" ? "java" : javaPath,
                Arguments = "-version",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi);
            if (p == null) return 0;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var outputTask = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync(cts.Token);
            var output = await outputTask;

            // "17.0.2" o "1.8.0_351"
            var match = System.Text.RegularExpressions.Regex.Match(output, @"version ""(\d+)");
            if (match.Success)
            {
                return int.Parse(match.Groups[1].Value);
            }
        }
        catch
        {
            // Java no disponible
        }

        return 0;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string baseDir, params string[] names)
    {
        foreach (var name in names)
        {
            string? dir = null;
            try
            {
                dir = Path.Combine(baseDir, name);
            }
            catch
            {
                continue;
            }

            if (!Directory.Exists(dir)) continue;

            string[] subdirs;
            try
            {
                subdirs = Directory.EnumerateDirectories(dir).ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var sub in subdirs)
            {
                yield return sub;
            }
        }
    }

    // ---------------------------------------------------------------
    //  Verificación de la instalación antes de lanzar
    // ---------------------------------------------------------------
    public async Task<bool> VerifyInstallationAsync(string seriesId)
    {
        var info = await _seriesService.GetSeriesAsync(seriesId);
        if (info == null) return false;

        if (info.Status == SeriesStatus.NotInstalled) return false;
        if (info.Status == SeriesStatus.MissingFiles) return false;

        var manifest = await _storageService.ReadPackageManifestAsync(info.InstallPath);
        if (manifest?.Files is { Count: > 0 })
        {
            var missing = manifest.Files.Any(f =>
            {
                try
                {
                    return !File.Exists(SeriesStorageService.ResolveInside(info.InstallPath, f.Path));
                }
                catch
                {
                    return true;
                }
            });

            if (missing) return false;
        }

        return true;
    }

    // ---------------------------------------------------------------
    //  Lanzamiento: verifica, instala lo necesario, construye el
    //  classpath y arranca el proceso sin bloquear la interfaz
    // ---------------------------------------------------------------
    public async Task<LaunchResult> LaunchAsync(string seriesId, IProgress<double>? progress = null)
    {
        var result = new LaunchResult();

        try
        {
            if (IsGameRunning)
            {
                result.Message = "La serie ya está en ejecución.";
                return result;
            }

            var info = await _seriesService.GetSeriesAsync(seriesId);
            if (info == null)
            {
                result.Message = $"Serie \"{seriesId}\" no encontrada.";
                return result;
            }

            if (info.Status == SeriesStatus.NotInstalled)
            {
                result.Message = $"La serie {info.Name} no está instalada.";
                return result;
            }

            if (info.Status == SeriesStatus.MissingFiles)
            {
                result.Message = $"La instalación de {info.Name} necesita reparación.";
                _notificationService.NotifyWarning(
                    "Instalación incompleta",
                    $"La instalación de {info.Name} necesita reparación.");
                return result;
            }

            if (!await VerifyInstallationAsync(seriesId))
            {
                result.Message = $"Faltan archivos necesarios de {info.Name}.";
                _notificationService.NotifyWarning(
                    "Faltan archivos",
                    $"La instalación de {info.Name} necesita reparación.");
                return result;
            }

            var mcVersion = info.Definition.MinecraftVersion;
            var loader = info.Definition.Loader;

            if (string.IsNullOrWhiteSpace(mcVersion) || string.IsNullOrWhiteSpace(loader))
            {
                result.Message = $"Falta definir la versión de Minecraft/loader de {info.Name} en el catálogo.";
                _notificationService.NotifyError(
                    "Serie sin configurar",
                    $"{info.Name} todavía no define versión de Minecraft ni loader en el catálogo.");
                return result;
            }

            // Instalar Minecraft vanilla si falta
            progress?.Report(0);
            if (!await _minecraftInstall.IsVersionInstalledAsync(mcVersion))
            {
                _notificationService.NotifyInfo(
                    "Descargando Minecraft",
                    $"Instalando Minecraft {mcVersion}...");
                await _minecraftInstall.InstallVersionAsync(mcVersion, progress, CancellationToken.None);
            }

            // Instalar Forge si falta
            if (!await _forgeInstall.IsForgeInstalledAsync(mcVersion))
            {
                _notificationService.NotifyInfo(
                    "Descargando Forge",
                    $"Instalando Forge para {mcVersion}...");
                await _forgeInstall.InstallForgeAsync(mcVersion, progress, CancellationToken.None);
            }

            // Java según la versión de MC (<=1.16 → 8 · 1.17–1.20.4 → 17 · >=1.20.5 → 21)
            var java = await FindJavaAsync(GetRequiredJava(mcVersion));
            if (java == null)
            {
                result.Message = "Java 17 no fue encontrado. Instalá Java 17 o configurá su ruta.";
                _notificationService.NotifyError(
                    "Java 17 no encontrado",
                    "Forge 1.20.1 necesita Java 17. Instalá Java 17 o configurá su ruta en Configuración → Juego.");
                return result;
            }

            var config = await BuildLaunchConfigAsync(info, java);
            if (config == null)
            {
                result.Message = "No se pudo construir la configuración de lanzamiento.";
                return result;
            }

            var psi = new ProcessStartInfo
            {
                FileName = config.JavaPath,
                Arguments = BuildArguments(config),
                WorkingDirectory = config.GameDir,
                UseShellExecute = false,
                CreateNoWindow = false
            };

            var process = Process.Start(psi);
            if (process == null)
            {
                result.Message = "No se pudo iniciar el proceso.";
                _notificationService.NotifyError("Error de inicio", $"No se pudo iniciar {info.Name}.");
                return result;
            }

            _runningProcess = process;
            _runningProcess.EnableRaisingEvents = true;
            _runningProcess.Exited += OnGameExited;
            _lastSeriesId = info.Id;
            _lastSeriesName = info.Name;
            _lastGameDir = config.GameDir;

            _logService.Info(
                $"Lanzamiento de {info.Name}: PID {process.Id}, Java {config.JavaPath}, " +
                $"MC {config.MinecraftVersion}, loader {config.Loader}, RAM {config.MinRAM}-{config.MaxRAM} MB");

            if (config.MinimizeLauncher)
            {
                MinimizeMainWindow();
            }

            result.Success = true;
            result.Process = process;
            result.Message = $"{info.Name} iniciada.";
            _notificationService.NotifySuccess($"{info.Name} iniciada", "La serie se está ejecutando.");
            return result;
        }
        catch (OperationCanceledException)
        {
            result.Message = "Instalación cancelada.";
            _notificationService.NotifyInfo("Cancelado", "La instalación de Minecraft fue cancelada.");
            return result;
        }
        catch (Exception ex)
        {
            _logService.Error($"No se pudo iniciar {seriesId}", ex);
            result.Message = "No se pudo iniciar el proceso.";
            _notificationService.NotifyError("Error de inicio", $"No se pudo iniciar la serie: {ex.Message}");
            return result;
        }
    }

    private void OnGameExited(object? sender, EventArgs e)
    {
        int exitCode = -1;
        try
        {
            var process = sender as Process;
            exitCode = process?.ExitCode ?? -1;
            _logService.Info($"La serie terminó (PID {process?.Id}, código {exitCode})");
        }
        catch
        {
            // Proceso ya liberado
        }

        _runningProcess = null;

        if (exitCode != 0)
        {
            GameCrashed?.Invoke(this, new GameCrashedArgs
            {
                SeriesId = _lastSeriesId,
                SeriesName = _lastSeriesName,
                ExitCode = exitCode,
                LogPath = string.IsNullOrWhiteSpace(_lastGameDir)
                    ? string.Empty
                    : Path.Combine(_lastGameDir, "logs", "latest.log")
            });
        }
    }

    private static void MinimizeMainWindow()
    {
        try
        {
            var window = System.Windows.Application.Current.MainWindow;
            if (window != null) window.WindowState = System.Windows.WindowState.Minimized;
        }
        catch
        {
            // Ventana no disponible
        }
    }

    // ---------------------------------------------------------------
    //  Construcción de la configuración de lanzamiento.
    //  Los datos de Minecraft/loader vienen del catálogo.
    // ---------------------------------------------------------------
    private async Task<SeriesLaunchConfig?> BuildLaunchConfigAsync(SeriesInfo info, string javaPath)
    {
        var settings = await _settingsService.GetSettingsAsync();
        var mcVersion = info.Definition.MinecraftVersion;

        var maxRAM = settings.AllocatedRAM;
        var available = GetAvailablePhysicalMemoryMB();

        if (available > 0)
        {
            maxRAM = Math.Min(maxRAM, (int)(available * 0.75));
        }

        maxRAM = Math.Max(2048, maxRAM);

        var session = _authService.CurrentSession;

        // Classpath vanilla: client jar + libraries (con rules por SO).
        var clientJar = await _minecraftInstall.GetClientJarPathAsync(mcVersion);
        var libraries = await _minecraftInstall.GetLibraryPathsAsync(mcVersion);

        // Lanzamiento moderno (Forge >= 1.17, p.ej. 1.20.1): BootstrapLauncher
        // con module-path. Requiere el version.json de Forge; si no hay,
        // se usa el launchwrapper clásico.
        var forgeData = await _forgeInstall.TryGetForgeLaunchDataAsync(info.InstallPath, mcVersion);
        var useBootstrap = forgeData != null
            && File.Exists(forgeData.ForgeJar)
            && forgeData.JvmArgs.Count > 0;
        if (useBootstrap)
        {
            await _forgeInstall.EnsureForgeLibrariesAsync(forgeData!, null, CancellationToken.None);
        }

        var classpathParts = new List<string>();
        if (useBootstrap)
        {
            // OJO: el client jar vanilla NO va al classpath: el jar de Forge
            // ya provee el módulo minecraft y ambos contienen los mismos
            // paquetes (split-package → ResolutionException).
            classpathParts.AddRange(libraries);
            classpathParts.AddRange(forgeData!.ClasspathLibraries.Where(File.Exists));
            classpathParts.Add(forgeData.ForgeJar);
        }
        else
        {
            var forgeJar = await _forgeInstall.GetForgeJarPathAsync(mcVersion);
            classpathParts.Add(clientJar);
            classpathParts.AddRange(libraries);
            classpathParts.Add(forgeJar);
        }

        var nativesDir = await _minecraftInstall.GetNativesDirAsync(mcVersion);
        var assetsDir = await _minecraftInstall.GetAssetsDirAsync(mcVersion);

        var versionJsonPath = await _minecraftInstall.GetVersionJsonPathAsync(mcVersion);
        var assetIndex = "5";
        try
        {
            if (File.Exists(versionJsonPath))
            {
                var json = await File.ReadAllTextAsync(versionJsonPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("assetIndex", out var ai)
                    && ai.TryGetProperty("id", out var id))
                {
                    assetIndex = id.GetString() ?? "5";
                }
            }
        }
        catch
        {
            // Usar assetIndex por defecto
        }

        return new SeriesLaunchConfig
        {
            SeriesId = info.Id,
            SeriesDir = info.InstallPath,
            MinecraftVersion = mcVersion,
            Loader = info.Definition.Loader,
            JavaPath = javaPath,
            MinRAM = Math.Min(2048, maxRAM),
            MaxRAM = maxRAM,
            Classpath = string.Join(';', classpathParts),
            UseBootstrap = useBootstrap,
            ForgeJvmArgs = useBootstrap ? string.Join(' ', forgeData!.JvmArgs.Select(QuoteIfNeeded)) : string.Empty,
            ForgeGameArgs = useBootstrap ? string.Join(' ', forgeData!.GameArgs.Select(QuoteIfNeeded)) : string.Empty,
            ForgeVersionId = useBootstrap ? forgeData!.VersionId : string.Empty,
            NativesDir = nativesDir,
            GameDir = info.InstallPath,
            AssetsDir = assetsDir,
            AssetIndex = assetIndex,
            Username = string.IsNullOrWhiteSpace(session?.Name) ? "Player" : session.Name,
            UUID = string.IsNullOrWhiteSpace(session?.Uuid) ? Guid.NewGuid().ToString("N") : session.Uuid,
            AccessToken = string.IsNullOrWhiteSpace(session?.AccessToken) ? "0" : session.AccessToken,
            UserType = string.IsNullOrWhiteSpace(session?.UserType) ? "legacy" : session.UserType,
            JvmArgs = settings.JvmArgs?.Trim() ?? string.Empty,
            Fullscreen = settings.Fullscreen,
            Width = ParseResolution(settings.Resolution).Width,
            Height = ParseResolution(settings.Resolution).Height,
            MinimizeLauncher = settings.MinimizeOnLaunch
        };
    }

    private static (int Width, int Height) ParseResolution(string? resolution)
    {
        try
        {
            var parts = (resolution ?? string.Empty).Split('x');
            if (parts.Length == 2
                && int.TryParse(parts[0], out var w)
                && int.TryParse(parts[1], out var h)
                && w >= 640 && w <= 7680 && h >= 480 && h <= 4320)
            {
                return (w, h);
            }
        }
        catch
        {
            // Resolución inválida: se usan los valores por defecto del juego
        }
        return (0, 0);
    }

    private static string QuoteIfNeeded(string value)
        => string.IsNullOrEmpty(value) ? value
            : value.Contains(' ') || value.Contains('"')
                ? "\"" + value.Replace("\"", "\\\"") + "\""
                : value;

    private static string BuildArguments(SeriesLaunchConfig config)
    {
        var jvm = $"-Xms{config.MinRAM}M -Xmx{config.MaxRAM}M";
        if (!string.IsNullOrWhiteSpace(config.JvmArgs)) jvm += " " + config.JvmArgs;

        var gameArgs = $"--username {config.Username} " +
                $"--version {(config.UseBootstrap && !string.IsNullOrEmpty(config.ForgeVersionId) ? config.ForgeVersionId : config.MinecraftVersion + "-forge")} " +
                $"--gameDir \"{config.GameDir}\" " +
                $"--assetsDir \"{config.AssetsDir}\" " +
                $"--assetIndex {config.AssetIndex} " +
                $"--uuid {config.UUID} " +
                $"--accessToken {config.AccessToken} " +
                $"--userType {config.UserType}";

        if (config.Fullscreen) gameArgs += " --fullscreen";
        if (config.Width > 0 && config.Height > 0)
            gameArgs += $" --width {config.Width} --height {config.Height}";

        if (config.UseBootstrap)
        {
            // Forge >= 1.17 (probado con 1.20.1-47.2.0): el version.json de
            // Forge ya trae -p (module-path), --add-modules y --add-opens.
            // El client jar vanilla queda FUERA del classpath (split-package).
            return $"{jvm} " +
                    $"-Djava.library.path=\"{config.NativesDir}\" " +
                    $"{config.ForgeJvmArgs} " +
                    $"-cp \"{config.Classpath}\" " +
                    "cpw.mods.bootstraplauncher.BootstrapLauncher " +
                    gameArgs + " " + config.ForgeGameArgs;
        }

        // Clásico (vanilla / Forge <= 1.16 con launchwrapper).
        gameArgs += " --tweakClass net.minecraftforge.fml.common.launcher.FMLTweaker";

        return $"{jvm} " +
                $"-Djava.library.path=\"{config.NativesDir}\" " +
                $"-cp \"{config.Classpath}\" " +
                "net.minecraft.launchwrapper.Launch " +
                gameArgs;
    }

    private static long GetAvailablePhysicalMemoryMB()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -Command \"(Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory/1KB\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc == null) return 0;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);
            return long.TryParse(output, out var mb) ? mb : 0;
        }
        catch
        {
            return 0;
        }
    }
}
