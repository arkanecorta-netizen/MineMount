using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MineMount.Models;

namespace MineMount.Services;

public interface ISeriesService
{
    Task<List<SeriesInfo>> GetSeriesAsync();
    Task<SeriesInfo?> GetSeriesAsync(string id);
    Task<SeriesOperationResult> InstallAsync(string id, IProgress<SeriesProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<SeriesOperationResult> UpdateAsync(string id, IProgress<SeriesProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<SeriesOperationResult> RepairAsync(string id, IProgress<SeriesProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<string> GetInstallRootAsync();
}

public class SeriesService : ISeriesService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ISettingsService _settingsService;
    private readonly ILogService _logService;

    private readonly List<SeriesManifest> _manifests = new();

    public SeriesService(ISettingsService settingsService, ILogService logService)
    {
        _settingsService = settingsService;
        _logService = logService;
    }

    private static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MineMount");

    private string DefinitionsDir => Path.Combine(AppDataDir, "SeriesDefs");

    private string ExeDefinitionsDir => Path.Combine(AppContext.BaseDirectory, "Series");

    private string SourcesDir => Path.Combine(AppDataDir, "SeriesSources");

    public async Task<string> GetInstallRootAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        var root = string.IsNullOrWhiteSpace(settings.SeriesInstallPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MineMount", "Series")
            : settings.SeriesInstallPath;
        Directory.CreateDirectory(root);
        return root;
    }

    public async Task<List<SeriesInfo>> GetSeriesAsync()
    {
        LoadManifests();
        var root = await GetInstallRootAsync();
        return _manifests.Select(m => BuildInfo(m, root)).ToList();
    }

    public async Task<SeriesInfo?> GetSeriesAsync(string id)
    {
        LoadManifests();
        var manifest = FindManifest(id);
        if (manifest == null) return null;
        var root = await GetInstallRootAsync();
        return BuildInfo(manifest, root);
    }

    public Task<SeriesOperationResult> InstallAsync(string id, IProgress<SeriesProgress>? progress = null, CancellationToken cancellationToken = default)
        => RunOperationAsync(id, "Instalación", progress, cancellationToken);

    public Task<SeriesOperationResult> UpdateAsync(string id, IProgress<SeriesProgress>? progress = null, CancellationToken cancellationToken = default)
        => RunOperationAsync(id, "Actualización", progress, cancellationToken);

    public Task<SeriesOperationResult> RepairAsync(string id, IProgress<SeriesProgress>? progress = null, CancellationToken cancellationToken = default)
        => RunOperationAsync(id, "Reparación", progress, cancellationToken);

    // ---------------------------------------------------------------
    //  Operación principal: instalar / actualizar / reparar
    // ---------------------------------------------------------------
    private async Task<SeriesOperationResult> RunOperationAsync(
        string id,
        string verb,
        IProgress<SeriesProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = new SeriesOperationResult();

        try
        {
            LoadManifests();
            var manifest = FindManifest(id);

            if (manifest == null)
            {
                result.Success = false;
                result.Message = $"Serie \"{id}\" no encontrada";
                return result;
            }

            if (!manifest.Available)
            {
                result.Success = false;
                result.Message = $"{manifest.Name} estará disponible próximamente";
                return result;
            }

            var root = await GetInstallRootAsync();
            var seriesDir = Path.Combine(root, manifest.Id);
            Directory.CreateDirectory(seriesDir);

            var previousState = LoadState(seriesDir);
            var files = manifest.Files ?? new List<SeriesFileManifest>();
            var total = files.Count;
            var requiredFailures = 0;

            if (total == 0)
            {
                SaveState(seriesDir, manifest, previousState, Array.Empty<string>());
                result.Success = true;
                result.Message = $"{verb} completada · la serie no define archivos";
                return result;
            }

            var index = 0;
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                index++;

                void Report(string message)
                {
                    progress?.Report(new SeriesProgress
                    {
                        FileIndex = index,
                        FileCount = total,
                        FileName = file.Name,
                        Percent = Math.Min(100, Math.Round(index * 100.0 / total, 1)),
                        Message = message
                    });
                }

                string dest;
                try
                {
                    dest = ResolveDestination(seriesDir, file.Path);
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    if (file.Required) requiredFailures++;
                    result.Errors.Add($"{file.Name}: {ex.Message}");
                    Report("Ruta inválida");
                    continue;
                }

                if (IsFileValid(dest, file))
                {
                    result.Skipped++;
                    Report("Ya instalado, se omite");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(file.Url))
                {
                    result.Pending++;
                    Report("Pendiente de configuración (sin URL)");
                    continue;
                }

                try
                {
                    Report("Descargando...");
                    await DownloadFileAsync(file.Url, dest, fraction =>
                    {
                        var overall = ((index - 1) + fraction) / total * 100.0;
                        progress?.Report(new SeriesProgress
                        {
                            FileIndex = index,
                            FileCount = total,
                            FileName = file.Name,
                            Percent = Math.Min(100, Math.Round(overall, 1)),
                            Message = "Descargando..."
                        });
                    }, cancellationToken);

                    result.Installed++;
                    Report("Instalado");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    if (file.Required) requiredFailures++;
                    result.Errors.Add($"{file.Name}: {ex.Message}");
                    _logService.Warning($"Descarga fallida para {manifest.Id}/{file.Path}: {ex.Message}");
                    Report($"Error: {ex.Message}");
                }
            }

            result.Success = requiredFailures == 0;

            if (result.Success)
            {
                SaveState(seriesDir, manifest, previousState,
                    files.Where(f => string.IsNullOrWhiteSpace(f.Url) == false)
                         .Select(f => f.Path)
                         .ToList());
            }

            var baseMessage = result.Success
                ? $"{verb} completada"
                : $"{verb} incompleta";

            if (result.Failed > 0)
            {
                result.Message = $"{baseMessage} · {result.Failed} archivos fallidos, " +
                                  $"{result.Installed} instalados, {result.Skipped} correctos, {result.Pending} pendientes";
            }
            else if (result.Pending > 0)
            {
                result.Message = $"{baseMessage} · {result.Pending} archivos pendientes de configuración";
            }
            else
            {
                result.Message = $"{baseMessage} · {result.Installed} instalados, {result.Skipped} ya correctos";
            }

            _logService.Info($"{verb} de {manifest.Id}: {result.Message}");
            return result;
        }
        catch (Exception ex)
        {
            _logService.Error($"{verb} de {id} falló", ex);
            result.Success = false;
            result.Message = $"Error durante la {verb.ToLowerInvariant()}: {ex.Message}";
            return result;
        }
    }

    // ---------------------------------------------------------------
    //  Descarga (http/https o file://) con progreso
    // ---------------------------------------------------------------
    private static async Task DownloadFileAsync(
        string url,
        string destPath,
        Action<double>? fileFraction,
        CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tempPath = destPath + ".tmp";

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            await using var source = File.OpenRead(uri.LocalPath);
            await using var target = File.Create(tempPath);
            await CopyStreamAsync(source, target, fileFraction, cancellationToken);
        }
        else
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var expectedLength = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(tempPath);
            await CopyStreamAsync(source, target, fileFraction, cancellationToken);

            if (expectedLength is long expected && new FileInfo(tempPath).Length != expected)
            {
                throw new IOException("La descarga está incompleta");
            }
        }

        File.Move(tempPath, destPath, overwrite: true);
    }

    private static async Task CopyStreamAsync(
        Stream source,
        Stream target,
        Action<double>? fileFraction,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long totalRead = 0;
        var totalLength = source.CanSeek ? source.Length : 0;

        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            totalRead += read;

            if (totalLength > 0)
            {
                fileFraction?.Invoke(Math.Min(1.0, (double)totalRead / totalLength));
            }
        }

        if (totalLength <= 0)
        {
            fileFraction?.Invoke(1.0);
        }
    }

    // ---------------------------------------------------------------
    //  Validación de archivos (tamaño + hash opcional)
    // ---------------------------------------------------------------
    private static bool IsFileValid(string destPath, SeriesFileManifest file)
    {
        if (!File.Exists(destPath)) return false;

        var info = new FileInfo(destPath);

        if (file.Size is long expectedSize && info.Length != expectedSize)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(file.Hash))
        {
            var actual = ComputeSha256(destPath);
            return string.Equals(NormalizeHash(actual), NormalizeHash(file.Hash), StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }

    private static string NormalizeHash(string hash)
    {
        var h = hash.Trim();
        if (h.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            h = h["sha256:".Length..];
        }

        return h.Replace("-", string.Empty).ToUpperInvariant();
    }

    private static string ResolveDestination(string seriesDir, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("Ruta vacía");
        }

        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(seriesDir);
        var fullDest = Path.GetFullPath(Path.Combine(fullRoot, normalized));

        var rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        if (!fullDest.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Ruta fuera de la serie: {relativePath}");
        }

        return fullDest;
    }

    // ---------------------------------------------------------------
    //  Estado instalado (versión + archivos)
    // ---------------------------------------------------------------
    private static string StatePath(string seriesDir)
        => Path.Combine(seriesDir, ".minemount", "state.json");

    private static SeriesInstallState? LoadState(string seriesDir)
    {
        try
        {
            var path = StatePath(seriesDir);
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SeriesInstallState>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveState(string seriesDir, SeriesManifest manifest, SeriesInstallState? previous, IEnumerable<string> installedFiles)
    {
        var state = new SeriesInstallState
        {
            Version = manifest.Version,
            InstalledAt = previous?.InstalledAt ?? DateTime.Now,
            InstalledFiles = installedFiles.ToList()
        };

        var path = StatePath(seriesDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOptions));
    }

    // ---------------------------------------------------------------
    //  Cálculo de estado para la UI
    // ---------------------------------------------------------------
    private static SeriesInfo BuildInfo(SeriesManifest manifest, string installRoot)
    {
        var info = new SeriesInfo
        {
            Manifest = manifest,
            Status = SeriesStatus.NotInstalled
        };

        if (!manifest.Available)
        {
            info.Status = SeriesStatus.ComingSoon;
            return info;
        }

        var seriesDir = Path.Combine(installRoot, manifest.Id);
        var state = LoadState(seriesDir);
        info.InstalledVersion = state?.Version ?? string.Empty;

        if (state == null)
        {
            info.Status = SeriesStatus.NotInstalled;
            return info;
        }

        if (!string.Equals(state.Version, manifest.Version, StringComparison.OrdinalIgnoreCase))
        {
            info.Status = SeriesStatus.UpdateAvailable;
            return info;
        }

        var missing = (manifest.Files ?? new List<SeriesFileManifest>())
            .Where(f => f.Required)
            .Any(f =>
            {
                try
                {
                    return !IsFileValid(ResolveDestination(seriesDir, f.Path), f);
                }
                catch
                {
                    return true;
                }
            });

        info.Status = missing ? SeriesStatus.MissingFiles : SeriesStatus.Installed;
        return info;
    }

    // ---------------------------------------------------------------
    //  Carga de manifestos (carpeta del exe + %APPDATA%)
    // ---------------------------------------------------------------
    private SeriesManifest? FindManifest(string id)
        => _manifests.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    private void LoadManifests()
    {
        _manifests.Clear();

        foreach (var dir in new[] { DefinitionsDir, ExeDefinitionsDir })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;

                foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var manifest = JsonSerializer.Deserialize<SeriesManifest>(json, JsonOptions);
                        if (manifest == null || string.IsNullOrWhiteSpace(manifest.Id)) continue;

                        var existing = FindManifest(manifest.Id);
                        if (existing != null)
                        {
                            _manifests.Remove(existing);
                        }

                        _manifests.Add(manifest);
                    }
                    catch (Exception ex)
                    {
                        _logService.Warning($"Manifest inválido {file}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.Warning($"No se pudo leer la carpeta de series {dir}: {ex.Message}");
            }
        }

        if (_manifests.Count == 0)
        {
            EnsureDefaultDefinitions();
            LoadManifestsOneShot();
        }
    }

    private void LoadManifestsOneShot()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(DefinitionsDir, "*.json"))
            {
                var json = File.ReadAllText(file);
                var manifest = JsonSerializer.Deserialize<SeriesManifest>(json, JsonOptions);
                if (manifest != null && !string.IsNullOrWhiteSpace(manifest.Id))
                {
                    _manifests.Add(manifest);
                }
            }
        }
        catch (Exception ex)
        {
            _logService.Error("No se pudieron leer los manifestos generados", ex);
        }
    }

    // ---------------------------------------------------------------
    //  Generación de ejemplos la primera vez
    //  (URLs locales de ejemplo: reemplazar por URLs reales después)
    // ---------------------------------------------------------------
    private void EnsureDefaultDefinitions()
    {
        try
        {
            Directory.CreateDirectory(DefinitionsDir);

            // NSE6 con fuentes locales de ejemplo para probar instalar/actualizar/reparar
            var samples = new (string RelativePath, string Content)[]
            {
                ("mods/nse6-core.jar", "NSE6 Core - archivo de ejemplo de MineMount"),
                ("mods/nse6-mobs.jar", "NSE6 Mobs - archivo de ejemplo de MineMount"),
                ("mods/nse6-tools.jar", "NSE6 Tools - archivo de ejemplo de MineMount"),
                ("config/nse6-server.cfg", "configuracion de ejemplo de NSE6 (servidor)"),
                ("config/nse6-client.cfg", "configuracion de ejemplo de NSE6 (cliente)"),
                ("resources/nse6-pack.txt", "recursos de ejemplo de NSE6")
            };

            var nse6SourceDir = Path.Combine(SourcesDir, "NSE6");
            Directory.CreateDirectory(nse6SourceDir);

            var nse6Files = new List<SeriesFileManifest>();

            foreach (var (relativePath, content) in samples)
            {
                var fullSourcePath = Path.Combine(nse6SourceDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(fullSourcePath)!);

                if (!File.Exists(fullSourcePath))
                {
                    File.WriteAllText(fullSourcePath, content);
                }

                nse6Files.Add(new SeriesFileManifest
                {
                    Name = Path.GetFileName(relativePath),
                    Url = new Uri(fullSourcePath).AbsoluteUri,
                    Path = relativePath,
                    Hash = "sha256:" + ComputeSha256(fullSourcePath),
                    Size = new FileInfo(fullSourcePath).Length,
                    Required = true
                });
            }

            var nse6 = new SeriesManifest
            {
                Id = "NSE6",
                Name = "NSE6",
                Version = "1.0.0",
                Description = "La serie principal de MineMount: muchos mods, configuraciones, recursos y archivos necesarios.",
                Image = string.Empty,
                Available = true,
                Files = nse6Files
            };

            WriteManifest(nse6);

            _logService.Info("Se generó el manifiesto de la serie NSE6");
        }
        catch (Exception ex)
        {
            _logService.Error("No se pudieron generar los manifestos de ejemplo", ex);
        }
    }

    private void WriteManifest(SeriesManifest manifest)
    {
        var path = Path.Combine(DefinitionsDir, $"{manifest.Id}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, JsonOptions));
    }
}
