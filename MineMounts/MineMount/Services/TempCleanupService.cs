using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MineMount.Services;

public interface ITempCleanupService
{
    Task CleanupStartupAsync();
    Task CleanupAfterOperationAsync(string? tempPath = null);
}

// Limpieza de temporales de MineMount. NUNCA toca:
// series instaladas, configuraciones, archivos personales ni
// archivos de Minecraft que no sean temporales de MineMount.
// Solo opera sobre %TEMP%\MineMount y carpetas claramente
// identificadas como temporales de MineMount.
public class TempCleanupService : ITempCleanupService
{
    private readonly ILogService _logService;

    public TempCleanupService(ILogService logService)
    {
        _logService = logService;
    }

    public static string TempRoot => Path.Combine(Path.GetTempPath(), "MineMount");

    // ---------------------------------------------------------------
    //  Limpieza al iniciar: operaciones temporales antiguas
    // ---------------------------------------------------------------
    public Task CleanupStartupAsync()
    {
        // Todo el cuerpo en threadpool: borrar árboles grandes de forma
        // sincrónica congelaba el splash (corría en el hilo UI).
        return Task.Run(() =>
        {
            try
            {
                var cleaned = 0;

                // 1. Raíz temporal de MineMount
                if (Directory.Exists(TempRoot))
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(TempRoot))
                    {
                        if (TryDelete(entry)) cleaned++;
                    }
                }

                // 2. Staging de series abandonado (instalaciones canceladas)
                var appData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MineMount");
                var stageDir = Path.Combine(appData, "cache", "series", "stage");
                if (Directory.Exists(stageDir))
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(stageDir))
                    {
                        if (TryDelete(entry)) cleaned++;
                    }
                }

                // 3. ZIPs .tmp huérfanos de descargas interrumpidas
                var cacheDir = Path.Combine(appData, "cache", "series");
                if (Directory.Exists(cacheDir))
                {
                    foreach (var file in Directory.EnumerateFiles(cacheDir, "*.tmp", SearchOption.AllDirectories))
                    {
                        if (TryDelete(file)) cleaned++;
                    }
                }

                // 4. Staging de actualizaciones del launcher.
                //    Protegido: si apply.cmd está copiando (estado fresco),
                //    borrar el staging ahora rompería la actualización.
                var updateStage = Path.Combine(appData, "updates", "stage");
                if (Directory.Exists(updateStage) && !IsUpdateApplyPending(appData))
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(updateStage))
                    {
                        if (TryDelete(entry)) cleaned++;
                    }
                }

                if (cleaned > 0)
                {
                    _logService.Info($"Limpieza de temporales: {cleaned} elementos eliminados");
                }
            }
            catch (Exception ex)
            {
                _logService.Warning($"Limpieza de temporales incompleta: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// True si hay una aplicación de actualización reciente (&lt; 10 min):
    /// en ese caso el staging es de un apply.cmd en curso y no se toca.
    /// </summary>
    private static bool IsUpdateApplyPending(string appData)
    {
        try
        {
            var statePath = Path.Combine(appData, "update-state.json");
            if (!File.Exists(statePath)) return false;

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(statePath));
            var root = doc.RootElement;

            if (!root.TryGetProperty("Timestamp", out var tsProp)) return false;
            if (!DateTimeOffset.TryParse(tsProp.GetString(), out var ts)) return false;

            return DateTimeOffset.UtcNow - ts < TimeSpan.FromMinutes(10);
        }
        catch
        {
            // Estado ilegible: se limpia con normalidad.
            return false;
        }
    }

    // ---------------------------------------------------------------
    //  Limpieza tras una operación: elimina el temporal indicado
    //  si es seguro (siempre bajo %TEMP%\MineMount o staging)
    // ---------------------------------------------------------------
    public Task CleanupAfterOperationAsync(string? tempPath = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(tempPath) && IsSafeTempPath(tempPath))
            {
                TryDelete(tempPath);
            }
        }
        catch (Exception ex)
        {
            _logService.Warning($"No se pudo limpiar {tempPath}: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    // Solo rutas bajo %TEMP%\MineMount o bajo el staging de MineMount
    private static bool IsSafeTempPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var tempRoot = Path.GetFullPath(TempRoot) + Path.DirectorySeparatorChar;
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MineMount");
            var stageRoot = Path.GetFullPath(Path.Combine(appData, "cache", "series", "stage")) + Path.DirectorySeparatorChar;

            return full.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(stageRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }

            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                return true;
            }
        }
        catch
        {
            // Archivo en uso o sin permisos: se reintenta en el próximo inicio
        }

        return false;
    }
}
