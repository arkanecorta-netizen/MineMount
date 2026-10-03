using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using MineMount.Models;

namespace MineMount.Services;

public interface ISeriesStorageService
{
    Task<string> GetInstallRootAsync();
    Task<string> GetSeriesDirAsync(string id);
    Task<SeriesInstallation?> ReadInstallationAsync(string seriesDir);
    Task SaveInstallationAsync(string seriesDir, SeriesInstallation installation);
    Task<SeriesPackageManifest?> ReadPackageManifestAsync(string seriesDir);
    Task SavePackageManifestAsync(string seriesDir, SeriesPackageManifest manifest);
    Task<SeriesStatus> ComputeStatusAsync(SeriesDefinition definition, string seriesDir);
}

public class SeriesStorageService : ISeriesStorageService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ISettingsService _settingsService;
    private readonly ILogService _logService;

    public SeriesStorageService(ISettingsService settingsService, ILogService logService)
    {
        _settingsService = settingsService;
        _logService = logService;
    }

    public async Task<string> GetInstallRootAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        var root = string.IsNullOrWhiteSpace(settings.SeriesInstallPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MineMount", "Series")
            : settings.SeriesInstallPath;
        Directory.CreateDirectory(root);
        return root;
    }

    public async Task<string> GetSeriesDirAsync(string id)
    {
        if (!SeriesValidation.IsValidSeriesId(id))
        {
            throw new ArgumentException($"Identificador de serie inválido: {id}");
        }

        var root = await GetInstallRootAsync();
        var dir = Path.Combine(root, id);
        return dir;
    }

    private static string MetaDir(string seriesDir) => Path.Combine(seriesDir, ".minemount");

    private static string InstallationPath(string seriesDir) => Path.Combine(MetaDir(seriesDir), "installation.json");

    private static string ManifestPath(string seriesDir) => Path.Combine(MetaDir(seriesDir), "manifest.json");

    public async Task<SeriesInstallation?> ReadInstallationAsync(string seriesDir)
    {
        try
        {
            var path = InstallationPath(seriesDir);
            if (!File.Exists(path)) return null;
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<SeriesInstallation>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _logService.Warning($"No se pudo leer installation.json de {seriesDir}: {ex.Message}");
            return null;
        }
    }

    public async Task SaveInstallationAsync(string seriesDir, SeriesInstallation installation)
    {
        var path = InstallationPath(seriesDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(installation, JsonOptions));
    }

    public async Task<SeriesPackageManifest?> ReadPackageManifestAsync(string seriesDir)
    {
        try
        {
            var path = ManifestPath(seriesDir);
            if (!File.Exists(path)) return null;
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<SeriesPackageManifest>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public async Task SavePackageManifestAsync(string seriesDir, SeriesPackageManifest manifest)
    {
        var path = ManifestPath(seriesDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, JsonOptions));
    }

    // ---------------------------------------------------------------
    //  Estado para la UI: existence + tamaño (rápido), sin hashear
    //  todo el contenido en cada refresco. El sha256 completo solo
    //  se valida durante la instalación/reparación.
    // ---------------------------------------------------------------
    public async Task<SeriesStatus> ComputeStatusAsync(SeriesDefinition definition, string seriesDir)
    {
        if (!definition.Available) return SeriesStatus.ComingSoon;

        var installation = await ReadInstallationAsync(seriesDir);
        if (installation == null) return SeriesStatus.NotInstalled;

        if (!string.Equals(installation.Version, definition.Version, StringComparison.OrdinalIgnoreCase))
        {
            return SeriesStatus.UpdateAvailable;
        }

        var manifest = await ReadPackageManifestAsync(seriesDir);
        if (manifest != null && manifest.Files.Count > 0)
        {
            var missing = manifest.Files.Any(f =>
            {
                try
                {
                    var full = ResolveInside(seriesDir, f.Path);
                    if (!File.Exists(full)) return true;
                    if (f.Size > 0 && new FileInfo(full).Length != f.Size) return true;
                    return false;
                }
                catch
                {
                    return true;
                }
            });

            if (missing) return SeriesStatus.MissingFiles;
        }

        return SeriesStatus.Installed;
    }

    internal static string ResolveInside(string seriesDir, string relativePath)
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

    internal static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }
}
