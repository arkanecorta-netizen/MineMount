using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MineMount.Models;

namespace MineMount.Services;

public interface ISeriesInstallService
{
    Task<SeriesOperationResult> InstallAsync(string id, IProgress<SeriesProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<SeriesOperationResult> UpdateAsync(string id, IProgress<SeriesProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<SeriesOperationResult> RepairAsync(string id, IProgress<SeriesProgress>? progress = null, CancellationToken cancellationToken = default);
}

public class SeriesInstallService : ISeriesInstallService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ISeriesCatalogService _catalogService;
    private readonly ISeriesStorageService _storageService;
    private readonly ISeriesResourceService _resourceService;
    private readonly ILogService _logService;

    private readonly string _cacheDir;
    private readonly string _stageDir;

    public SeriesInstallService(
        ISeriesCatalogService catalogService,
        ISeriesStorageService storageService,
        ISeriesResourceService resourceService,
        ILogService logService)
    {
        _catalogService = catalogService;
        _storageService = storageService;
        _resourceService = resourceService;
        _logService = logService;

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MineMount");
        _cacheDir = Path.Combine(appData, "cache", "series");
        _stageDir = Path.Combine(_cacheDir, "stage");
        Directory.CreateDirectory(_cacheDir);
    }

    public Task<SeriesOperationResult> InstallAsync(string id, IProgress<SeriesProgress>? progress = null, CancellationToken cancellationToken = default)
        => RunAsync(id, "Instalación", progress, cancellationToken);

    public Task<SeriesOperationResult> UpdateAsync(string id, IProgress<SeriesProgress>? progress = null, CancellationToken cancellationToken = default)
        => RunAsync(id, "Actualización", progress, cancellationToken);

    public Task<SeriesOperationResult> RepairAsync(string id, IProgress<SeriesProgress>? progress = null, CancellationToken cancellationToken = default)
        => RunAsync(id, "Reparación", progress, cancellationToken);

    // ---------------------------------------------------------------
    //  Pipeline: resolver → descargar → validar/extraer → verificar
    //            → fusionar → guardar installation.json
    // ---------------------------------------------------------------
    private async Task<SeriesOperationResult> RunAsync(
        string id,
        string verb,
        IProgress<SeriesProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = new SeriesOperationResult();

        void Report(int step, double percent, string message)
            => progress?.Report(new SeriesProgress
            {
                StepIndex = step,
                StepCount = 3,
                StepName = step switch { 1 => "Descargando", 2 => "Extrayendo", _ => "Instalando" },
                Percent = Math.Min(100, Math.Round(percent, 1)),
                Message = message
            });

        try
        {
            var definition = await _catalogService.GetDefinitionAsync(id);
            if (definition == null)
            {
                result.Success = false;
                result.Message = $"Serie \"{id}\" no encontrada en el catálogo";
                return result;
            }

            if (!definition.Available)
            {
                result.Success = false;
                result.Message = $"{definition.Name} estará disponible próximamente";
                return result;
            }

            cancellationToken.ThrowIfCancellationRequested();

            Report(1, 0, "Buscando release...");
            var package = await _resourceService.ResolvePackageAsync(definition);
            if (package == null)
            {
                result.Success = false;
                result.Message = "No se encontró el paquete de la serie en GitHub";
                result.Errors.Add($"Release no disponible para {definition.Id} v{definition.Version}");
                return result;
            }

            var seriesDir = await _storageService.GetSeriesDirAsync(definition.Id);
            Directory.CreateDirectory(seriesDir);

            var zipPath = Path.Combine(_cacheDir, $"{definition.Id}-{definition.Version}.zip");

            // 1. Descarga (o reutiliza caché si el hash ya coincide)
            if (File.Exists(zipPath) && IsCachedPackageValid(zipPath, package))
            {
                _logService.Info($"Paquete de {definition.Id} en caché, se omite la descarga");
                Report(1, 60, "Paquete en caché");
            }
            else
            {
                var downloadProgress = new Progress<SeriesProgress>(p => progress?.Report(p));
                await _resourceService.DownloadAsync(package, zipPath, downloadProgress, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // 2. Validar y extraer a staging
            Report(2, 60, "Validando paquete...");
            var stageRoot = Path.Combine(_stageDir, definition.Id);
            if (Directory.Exists(stageRoot)) Directory.Delete(stageRoot, recursive: true);
            Directory.CreateDirectory(stageRoot);

            var manifest = ExtractAndVerify(zipPath, stageRoot, (done, total) =>
            {
                var percent = total > 0 ? 60 + (done * 25.0 / total) : 85;
                Report(2, percent, $"Extrayendo {done}/{total} archivos...");
            }, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            // 3. Fusionar en la carpeta de la serie (sobrescribe, no borra ajenos)
            Report(3, 85, "Instalando archivos...");
            var stagedFiles = Directory.GetFiles(stageRoot, "*", SearchOption.AllDirectories);
            var copied = 0;
            foreach (var source in stagedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relative = Path.GetRelativePath(stageRoot, source);
                if (string.Equals(relative, "manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var dest = SeriesStorageService.ResolveInside(seriesDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(source, dest, overwrite: true);

                copied++;
                if (stagedFiles.Length > 0)
                {
                    Report(3, 85 + (copied * 14.0 / stagedFiles.Length), $"Instalando {copied}/{stagedFiles.Length}...");
                }
            }

            // 3b. Guardar metadatos (§18): installation.json + manifest.json
            var installation = new SeriesInstallation
            {
                Id = definition.Id,
                Version = definition.Version,
                InstalledAt = (await _storageService.ReadInstallationAsync(seriesDir))?.InstalledAt ?? DateTime.Now,
                UpdatedAt = DateTime.Now,
                Status = "installed",
                Path = seriesDir,
                PackageSha256 = string.IsNullOrWhiteSpace(package.Sha256)
                    ? SeriesStorageService.ComputeSha256(zipPath)
                    : package.Sha256
            };

            await _storageService.SaveInstallationAsync(seriesDir, installation);

            if (manifest != null)
            {
                await _storageService.SavePackageManifestAsync(seriesDir, manifest);
            }

            Report(3, 100, "Completado");

            TryDeleteDirectory(stageRoot);

            result.Success = true;
            result.Installed = copied;
            result.Message = manifest != null
                ? $"{verb} completada · {copied} archivos · v{definition.Version}"
                : $"{verb} completada · {copied} archivos · v{definition.Version} (sin manifest.json en el paquete)";

            _logService.Info($"{verb} de {definition.Id}: {result.Message}");
            return result;
        }
        catch (OperationCanceledException)
        {
            _logService.Warning($"{verb} de {id} cancelada por el usuario");
            result.Success = false;
            result.Message = $"{verb} cancelada";
            return result;
        }
        catch (InvalidDataException ex)
        {
            _logService.Error($"{verb} de {id}: paquete inválido", ex);
            result.Success = false;
            result.Message = $"Paquete inválido: {ex.Message}";
            result.Errors.Add(ex.Message);
            return result;
        }
        catch (Exception ex)
        {
            _logService.Error($"{verb} de {id} falló", ex);
            result.Success = false;
            result.Message = $"Error durante la {verb.ToLowerInvariant()}: {ex.Message}";
            result.Errors.Add(ex.Message);
            return result;
        }
    }

    private bool IsCachedPackageValid(string zipPath, SeriesPackageInfo package)
    {
        try
        {
            var info = new FileInfo(zipPath);
            if (package.Size > 0 && info.Length != package.Size) return false;

            if (string.IsNullOrWhiteSpace(package.Sha256)) return true;

            var actual = SeriesStorageService.ComputeSha256(zipPath);
            return string.Equals(actual, package.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------
    //  Extracción con protección zip-slip + verificación sha256
    // ---------------------------------------------------------------
    private static SeriesPackageManifest? ExtractAndVerify(
        string zipPath,
        string stageRoot,
        Action<int, int> onExtracted,
        CancellationToken cancellationToken)
    {
        SeriesPackageManifest? manifest = null;
        var extracted = 0;

        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count == 0)
        {
            throw new InvalidDataException("El paquete ZIP está vacío");
        }

        var fullStage = Path.GetFullPath(stageRoot);
        var stagePrefix = fullStage.EndsWith(Path.DirectorySeparatorChar)
            ? fullStage
            : fullStage + Path.DirectorySeparatorChar;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var target = Path.GetFullPath(Path.Combine(fullStage, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));

            if (!target.StartsWith(stagePrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Entrada fuera de la carpeta destino: {entry.FullName}");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
            extracted++;
            onExtracted(extracted, archive.Entries.Count);
        }

        var manifestPath = Path.Combine(fullStage, "manifest.json");
        if (File.Exists(manifestPath))
        {
            var json = File.ReadAllText(manifestPath);
            manifest = JsonSerializer.Deserialize<SeriesPackageManifest>(json, JsonOptions);

            if (manifest?.Files is { Count: > 0 })
            {
                foreach (var file in manifest.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var full = Path.GetFullPath(Path.Combine(fullStage, file.Path.Replace('/', Path.DirectorySeparatorChar)));
                    if (!full.StartsWith(stagePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"Manifest apunta fuera del paquete: {file.Path}");
                    }

                    if (!File.Exists(full))
                    {
                        throw new InvalidDataException($"El manifest lista un archivo ausente: {file.Path}");
                    }

                    var actual = SeriesStorageService.ComputeSha256(full);
                    if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"Hash incorrecto en {file.Path}");
                    }
                }
            }
            else
            {
                manifest = null;
            }
        }

        return manifest;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Limpieza best-effort
        }
    }
}
