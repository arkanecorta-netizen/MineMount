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
    public string NativesDir { get; set; } = string.Empty;
    public string GameDir { get; set; } = string.Empty;
    public string AssetsDir { get; set; } = string.Empty;
    public string AssetIndex { get; set; } = string.Empty;
    public string Username { get; set; } = "Player";
    public string UUID { get; set; } = string.Empty;
    public bool MinimizeLauncher { get; set; }
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
    Task<bool> VerifyInstallationAsync(string seriesId);
    Task<LaunchResult> LaunchAsync(string seriesId, IProgress<double>? progress = null);
    bool IsGameRunning { get; }
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

    private Process? _runningProcess;

    public bool IsGameRunning => _runningProcess is { HasExited: false };

    public GameLauncherService(
        ISeriesService seriesService,
        ISeriesStorageService storageService,
        ISettingsService settingsService,
        ILogService logService,
        INotificationService notificationService,
        IMinecraftInstallService minecraftInstall,
        IForgeInstallService forgeInstall)
    {
        _seriesService = seriesService;
        _storageService = storageService;
        _settingsService = settingsService;
        _logService = logService;
        _notificationService = notificationService;
        _minecraftInstall = minecraftInstall;
        _forgeInstall = forgeInstall;
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

            var output = await p.StandardError.ReadToEndAsync();
            p.WaitForExit(5000);

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

            // Java (Forge 1.20.1 necesita Java 17)
            var java = await FindJavaAsync(17);
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
        try
        {
            var process = sender as Process;
            _logService.Info($"La serie terminó (PID {process?.Id}, código {process?.ExitCode})");
        }
        catch
        {
            // Proceso ya liberado
        }

        _runningProcess = null;
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

        // Classpath: client jar + libraries + forge universal jar
        var clientJar = await _minecraftInstall.GetClientJarPathAsync(mcVersion);
        var libraries = await _minecraftInstall.GetLibraryPathsAsync(mcVersion);
        var forgeJar = await _forgeInstall.GetForgeJarPathAsync(mcVersion);

        var classpathParts = new List<string> { clientJar };
        classpathParts.AddRange(libraries);
        classpathParts.Add(forgeJar);

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
            NativesDir = nativesDir,
            GameDir = info.InstallPath,
            AssetsDir = assetsDir,
            AssetIndex = assetIndex,
            Username = string.IsNullOrWhiteSpace(settings.UserName) ? "Player" : settings.UserName,
            UUID = Guid.NewGuid().ToString("N"),
            MinimizeLauncher = settings.MinimizeOnLaunch
        };
    }

    private static string BuildArguments(SeriesLaunchConfig config)
    {
        // Forge 1.20.1 usa launchwrapper con FMLTweaker.
        // El classpath y los args se construyen desde la configuración.
        return $"-Xms{config.MinRAM}M " +
               $"-Xmx{config.MaxRAM}M " +
               $"-Djava.library.path=\"{config.NativesDir}\" " +
               $"-cp \"{config.Classpath}\" " +
               "net.minecraft.launchwrapper.Launch " +
               $"--username {config.Username} " +
               $"--version {config.MinecraftVersion}-forge " +
               $"--gameDir \"{config.GameDir}\" " +
               $"--assetsDir \"{config.AssetsDir}\" " +
               $"--assetIndex {config.AssetIndex} " +
               $"--uuid {config.UUID} " +
               "--accessToken 0 " +
               "--userType legacy " +
               "--tweakClass net.minecraftforge.fml.common.launcher.FMLTweaker";
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
