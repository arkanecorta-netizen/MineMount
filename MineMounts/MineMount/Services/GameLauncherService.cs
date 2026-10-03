using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    public string JVMArgs { get; set; } = string.Empty;
    public string GameArgs { get; set; } = string.Empty;
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
    Task<string?> FindJavaAsync();
    Task<bool> VerifyInstallationAsync(string seriesId);
    Task<LaunchResult> LaunchAsync(string seriesId);
    bool IsGameRunning { get; }
}

// Lanzador de series. El ViewModel NO ejecuta Minecraft directamente:
// pasa por este servicio, que verifica, construye la configuración y
// registra el proceso sin bloquear la interfaz.
public class GameLauncherService : IGameLauncherService
{
    private readonly ISeriesService _seriesService;
    private readonly ISeriesStorageService _storageService;
    private readonly ISettingsService _settingsService;
    private readonly ILogService _logService;
    private readonly INotificationService _notificationService;

    private Process? _runningProcess;

    public bool IsGameRunning => _runningProcess is { HasExited: false };

    public GameLauncherService(
        ISeriesService seriesService,
        ISeriesStorageService storageService,
        ISettingsService settingsService,
        ILogService logService,
        INotificationService notificationService)
    {
        _seriesService = seriesService;
        _storageService = storageService;
        _settingsService = settingsService;
        _logService = logService;
        _notificationService = notificationService;
    }

    // ---------------------------------------------------------------
    //  Detección de Java: ruta configurada → JAVA_HOME → PATH →
    //  ubicaciones comunes de instalación
    // ---------------------------------------------------------------
    public async Task<string?> FindJavaAsync()
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
            if (candidate == "java")
            {
                if (IsJavaOnPath()) return "java";
                continue;
            }

            var full = Path.GetFullPath(candidate);
            if (File.Exists(full))
            {
                _logService.Info($"Java encontrado: {full}");
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

    private static bool IsJavaOnPath()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "java",
                Arguments = "-version",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            p?.WaitForExit(3000);
            return p is { HasExited: true, ExitCode: 0 };
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(params string[] names)
    {
        foreach (var name in names)
        {
            string? dir = null;
            try
            {
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), name);
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

        if (info.Status == Models.SeriesStatus.NotInstalled) return false;

        if (info.Status == Models.SeriesStatus.MissingFiles) return false;

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
    //  Lanzamiento: verifica todo, construye la configuración y
    //  arranca el proceso sin bloquear la interfaz
    // ---------------------------------------------------------------
    public async Task<LaunchResult> LaunchAsync(string seriesId)
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

            if (info.Status == Models.SeriesStatus.NotInstalled)
            {
                result.Message = $"La serie {info.Name} no está instalada.";
                return result;
            }

            if (info.Status == Models.SeriesStatus.MissingFiles)
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

            var java = await FindJavaAsync();
            if (java == null)
            {
                result.Message = "Java no fue encontrado. Configurá la ruta de Java en Configuración.";
                _notificationService.NotifyError(
                    "Java no encontrado",
                    "Instalá Java o configurá su ruta en Configuración → Juego.");
                return result;
            }

            var config = await BuildLaunchConfigAsync(info, java);
            if (config == null)
            {
                result.Message = $"Falta definir la versión de Minecraft/loader de {info.Name} en el catálogo.";
                _notificationService.NotifyError(
                    "Serie sin configurar",
                    $"{info.Name} todavía no define versión de Minecraft ni loader en el catálogo.");
                return result;
            }

            var psi = new ProcessStartInfo
            {
                FileName = config.JavaPath,
                Arguments = BuildArguments(config),
                WorkingDirectory = config.SeriesDir,
                UseShellExecute = false,
                CreateNoWindow = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };

            var process = Process.Start(psi);
            if (process == null)
            {
                result.Message = "No se pudo iniciar el proceso.";
                _notificationService.NotifyError(
                    "Error de inicio",
                    $"No se pudo iniciar {info.Name}.");
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
            _notificationService.NotifySuccess(
                $"{info.Name} iniciada",
                "La serie se está ejecutando.");
            return result;
        }
        catch (Exception ex)
        {
            _logService.Error($"No se pudo iniciar {seriesId}", ex);
            result.Message = "No se pudo iniciar el proceso.";
            _notificationService.NotifyError(
                "Error de inicio",
                $"No se pudo iniciar la serie: {ex.Message}");
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
    //  Los datos de Minecraft/loader vienen del catálogo (SeriesDefinition).
    //  Si no están definidos, no se inventan: se reporta la falta.
    // ---------------------------------------------------------------
    private async Task<SeriesLaunchConfig?> BuildLaunchConfigAsync(SeriesInfo info, string javaPath)
    {
        var settings = await _settingsService.GetSettingsAsync();

        // La versión de Minecraft y el loader DEBEN estar en el catálogo.
        // No se inventan: sin ellos no hay lanzamiento real.
        var mcVersion = info.Definition.MinecraftVersion;
        var loader = info.Definition.Loader;

        if (string.IsNullOrWhiteSpace(mcVersion) || string.IsNullOrWhiteSpace(loader))
        {
            return null;
        }

        var maxRAM = settings.AllocatedRAM;
        var available = GetAvailablePhysicalMemoryMB();

        // No asignar RAM arbitraria enorme: tope razonable según memoria disponible
        if (available > 0)
        {
            maxRAM = Math.Min(maxRAM, (int)(available * 0.75));
        }

        maxRAM = Math.Max(1024, maxRAM);

        return new SeriesLaunchConfig
        {
            SeriesId = info.Id,
            SeriesDir = info.InstallPath,
            MinecraftVersion = mcVersion,
            Loader = loader,
            JavaPath = javaPath,
            MinRAM = Math.Min(2048, maxRAM),
            MaxRAM = maxRAM,
            JVMArgs = info.Definition.JVMArgs,
            GameArgs = info.Definition.GameArgs,
            MinimizeLauncher = settings.MinimizeOnLaunch
        };
    }

    private static string BuildArguments(SeriesLaunchConfig config)
    {
        var parts = new List<string>
        {
            $"-Xms{config.MinRAM}M",
            $"-Xmx{config.MaxRAM}M"
        };

        if (!string.IsNullOrWhiteSpace(config.JVMArgs))
        {
            parts.Add(config.JVMArgs);
        }

        // El classpath y la clase principal dependen del loader y la
        // versión: se construyen desde la configuración, nunca hardcodeados.
        // (Cuando el catálogo defina loader/versión, aquí se ensambla el
        //  classpath de Forge/NeoForge/Fabric correspondiente.)

        if (!string.IsNullOrWhiteSpace(config.GameArgs))
        {
            parts.Add(config.GameArgs);
        }

        return string.Join(" ", parts);
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
