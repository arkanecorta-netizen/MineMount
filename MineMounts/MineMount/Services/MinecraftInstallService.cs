using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MineMount.Models;

namespace MineMount.Services;

public interface IMinecraftInstallService
{
    Task<MinecraftVersionJson?> GetVersionAsync(string versionId);
    Task<List<Models.MinecraftVersion>> GetAvailableVersionsAsync();
    Task<bool> IsVersionInstalledAsync(string versionId);
    Task InstallVersionAsync(string versionId, IProgress<double>? progress, CancellationToken cancellationToken);
    Task<string> GetClientJarPathAsync(string versionId);
    Task<string> GetAssetsDirAsync(string versionId);
    Task<string> GetNativesDirAsync(string versionId);
    Task<List<string>> GetLibraryPathsAsync(string versionId);
    Task<string> GetVersionJsonPathAsync(string versionId);
}

// Instalación real de Minecraft vanilla:
//   client.jar + libraries + natives + assets
// Todo se guarda en %APPDATA%\MineMount\Games\Minecraft\<versionId>\
public class MinecraftInstallService : IMinecraftInstallService
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogService _logService;
    private readonly ISettingsService _settingsService;

    public MinecraftInstallService(ILogService logService, ISettingsService settingsService)
    {
        _logService = logService;
        _settingsService = settingsService;
        Directory.CreateDirectory(GamesRoot());
    }

    /// <summary>
    /// Raíz del runtime de Minecraft: la carpeta configurada en Juego
    /// (por defecto %APPDATA%\.minemount) o la ubicación histórica.
    /// </summary>
    private string GamesRoot()
    {
        try
        {
            var configured = _settingsService.GetSettingsAsync()
                .GetAwaiter().GetResult().GameDirectory?.Trim();
            if (!string.IsNullOrWhiteSpace(configured))
                return Path.Combine(configured, "Minecraft");
        }
        catch
        {
            // Configuración ilegible: ubicación histórica
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MineMount", "Games", "Minecraft");
    }

    public static string DefaultGameDirectory()
    {
        // Si ya hay un runtime instalado en la ubicación histórica, se
        // muestra esa (no se re-descarga nada). Si no, %APPDATA%\.minemount.
        try
        {
            var legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MineMount", "Games");
            if (Directory.Exists(Path.Combine(legacy, "Minecraft")))
                return legacy;
        }
        catch
        {
            // Sin acceso: valor por defecto directo
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ".minemount");
    }

    private string VersionDir(string versionId) => Path.Combine(GamesRoot(), versionId);

    // ---------------------------------------------------------------
    //  Version JSON (de Mojang, con caché local)
    // ---------------------------------------------------------------
    public async Task<MinecraftVersionJson?> GetVersionAsync(string versionId)
    {
        var jsonPath = Path.Combine(VersionDir(versionId), "version.json");

        if (File.Exists(jsonPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(jsonPath);
                return JsonSerializer.Deserialize<MinecraftVersionJson>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                // Caché corrupta: borrar y re-descargar (antes quedaba
                // clavada: cada intento leía el mismo JSON inválido).
                _logService.Warning($"version.json de {versionId} ilegible ({ex.Message}); se re-descarga");
                try { File.Delete(jsonPath); } catch { /* best effort */ }
            }
        }

        try
        {
            var manifest = await FetchVersionManifestAsync();
            var entry = manifest?.FirstOrDefault(v => string.Equals(v.Id, versionId, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return null;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var resp = await Http.GetAsync(entry.Url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            resp.EnsureSuccessStatusCode();
            var versionJson = await resp.Content.ReadAsStringAsync(cts.Token);

            Directory.CreateDirectory(VersionDir(versionId));

            // Escritura atómica: un corte no deja un version.json truncado.
            var tmp = jsonPath + ".tmp";
            await File.WriteAllTextAsync(tmp, versionJson, cts.Token);
            File.Move(tmp, jsonPath, overwrite: true);

            return JsonSerializer.Deserialize<MinecraftVersionJson>(versionJson, JsonOptions);
        }
        catch (Exception ex)
        {
            _logService.Error($"No se pudo obtener la versión {versionId}", ex);
            return null;
        }
    }

    private async Task<List<VersionManifestEntry>?> FetchVersionManifestAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
            req.Headers.UserAgent.ParseAdd("MineMount-Launcher/1.0");
            using var resp = await Http.SendAsync(req, cts.Token);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);
            var list = new List<VersionManifestEntry>();
            if (doc.RootElement.TryGetProperty("versions", out var versions))
            {
                foreach (var v in versions.EnumerateArray())
                {
                    if (!v.TryGetProperty("id", out var idProp) || idProp.GetString() is not string id)
                        continue;
                    list.Add(new VersionManifestEntry
                    {
                        Id = id,
                        Url = v.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty,
                        Type = v.TryGetProperty("type", out var t) ? t.GetString() ?? "release" : "release",
                        ReleaseTime = v.TryGetProperty("releaseTime", out var rt)
                                      && DateTime.TryParse(rt.GetString(), out var d)
                            ? d
                            : DateTime.MinValue
                    });
                }
            }
            return list;
        }
        catch (Exception ex)
        {
            _logService.Error("No se pudo obtener el manifest de versiones", ex);
            return null;
        }
    }

    /// <summary>
    /// Catálogo real de versiones de Mojang (releases + snapshots) para la
    /// página Juegos, ordenado del más reciente al más antiguo.
    /// </summary>
    public async Task<List<Models.MinecraftVersion>> GetAvailableVersionsAsync()
    {
        var manifest = await FetchVersionManifestAsync();
        if (manifest == null) return new List<Models.MinecraftVersion>();

        return manifest
            .Where(v => !string.IsNullOrWhiteSpace(v.Url))
            .Select(v => new Models.MinecraftVersion
            {
                Id = v.Id,
                Name = string.Equals(v.Type, "release", StringComparison.OrdinalIgnoreCase)
                    ? v.Id
                    : $"Snapshot {v.Id}",
                Type = v.Type,
                ReleaseDate = v.ReleaseTime
            })
            .OrderByDescending(v => v.ReleaseDate)
            .ToList();
    }

    // ---------------------------------------------------------------
    //  Verificación de instalación
    // ---------------------------------------------------------------
    public async Task<bool> IsVersionInstalledAsync(string versionId)
    {
        var dir = VersionDir(versionId);
        if (!Directory.Exists(dir)) return false;

        var version = await GetVersionAsync(versionId);
        if (version == null) return false;

        // Client jar
        var clientJar = Path.Combine(dir, "client.jar");
        if (!File.Exists(clientJar)) return false;

        // Libraries (solo las permitidas por rules en este SO)
        var librariesDir = Path.Combine(dir, "libraries");
        foreach (var lib in version.Libraries)
        {
            if (!lib.IsAllowedOnCurrentOs()) continue;
            var artifact = lib.Downloads?.Artifact;
            if (artifact == null) continue;

            var libPath = Path.Combine(librariesDir, artifact.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(libPath)) return false;
        }

        // Assets: índice + verificación de que los objetos existen
        // (antes solo se pedía el índice: instalaciones rotas pasaban
        // como válidas y el juego moría al arrancar)
        var assetsDir = Path.Combine(dir, "assets");
        var indexDir = Path.Combine(assetsDir, "indexes");
        var indexFile = Path.Combine(indexDir, $"{version.AssetIndex?.Id}.json");
        if (!File.Exists(indexFile)) return false;

        try
        {
            using var indexJson = JsonDocument.Parse(await File.ReadAllTextAsync(indexFile));
            if (indexJson.RootElement.TryGetProperty("objects", out var objs)
                && objs.ValueKind == JsonValueKind.Object)
            {
                var objectsDir = Path.Combine(assetsDir, "objects");
                foreach (var prop in objs.EnumerateObject())
                {
                    if (!prop.Value.TryGetProperty("hash", out var hashProp)) continue;
                    var hash = hashProp.GetString();
                    if (string.IsNullOrEmpty(hash) || hash.Length < 2) continue;

                    var assetPath = Path.Combine(objectsDir, hash.Substring(0, 2), hash);
                    if (!File.Exists(assetPath)) return false;
                }
            }
        }
        catch
        {
            return false;
        }

        return true;
    }

    // ---------------------------------------------------------------
    //  Instalación completa con progreso real
    // ---------------------------------------------------------------
    public async Task InstallVersionAsync(string versionId, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var version = await GetVersionAsync(versionId);
        if (version == null)
        {
            throw new InvalidOperationException($"No se encontró la versión {versionId}");
        }

        var dir = VersionDir(versionId);
        Directory.CreateDirectory(dir);

        var librariesDir = Path.Combine(dir, "libraries");
        var nativesDir = Path.Combine(dir, "natives");
        var assetsDir = Path.Combine(dir, "assets");
        Directory.CreateDirectory(librariesDir);
        Directory.CreateDirectory(nativesDir);
        Directory.CreateDirectory(assetsDir);

        // 1. Client jar (5%)
        progress?.Report(2);
        var clientJar = Path.Combine(dir, "client.jar");
        if (!File.Exists(clientJar))
        {
            await DownloadFileAsync(version.Downloads!.Client!.Url, clientJar, cancellationToken,
                version.Downloads.Client.Sha1, version.Downloads.Client.Size);
        }
        progress?.Report(5);

        // 2. Libraries (5% → 40%, solo las permitidas por rules en este SO)
        var libs = version.Libraries
            .Where(l => l.IsAllowedOnCurrentOs() && l.Downloads?.Artifact != null)
            .ToList();
        var libDone = 0;
        foreach (var lib in libs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var artifact = lib.Downloads!.Artifact!;
            var libPath = Path.Combine(librariesDir, artifact.Path.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(libPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(libPath)!);
                await DownloadFileAsync(artifact.Url, libPath, cancellationToken,
                    artifact.Sha1, artifact.Size);
            }

            libDone++;
            progress?.Report(5 + (libDone * 35.0 / libs.Count));
        }

        // 3. Natives (40% → 50%)
        var nativeLibs = version.Libraries
            .Where(l => l.IsAllowedOnCurrentOs() && l.Natives != null && l.Downloads?.Classifiers != null)
            .ToList();
        var nativeDone = 0;
        foreach (var lib in nativeLibs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nativeKey = ResolveNativeKey(lib);
            if (nativeKey == null || !lib.Downloads!.Classifiers!.TryGetValue(nativeKey, out var native))
            {
                nativeDone++;
                continue;
            }

            var nativeJar = Path.Combine(nativesDir, Path.GetFileName(native.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(nativeJar))
            {
                await DownloadFileAsync(native.Url, nativeJar, cancellationToken,
                    native.Sha1, native.Size);
            }

            // Extraer natives; si el jar está corrupto o bloqueado, se
            // re-descarga una vez y si aún falla se continúa (natives en
            // 1.19.3+ van en el classpath y muchos launchers lo ignoran).
            try
            {
                ExtractNatives(nativeJar, nativesDir, cancellationToken);
            }
            catch (Exception ex)
            {
                _logService.Warning($"Natives de {lib.Name} no se pudieron extraer ({ex.Message}); re-descarga");
                try { File.Delete(nativeJar); } catch { /* best effort */ }
                try
                {
                    await DownloadFileAsync(native.Url, nativeJar, cancellationToken,
                        native.Sha1, native.Size);
                    ExtractNatives(nativeJar, nativesDir, cancellationToken);
                }
                catch (Exception ex2)
                {
                    _logService.Warning($"Natives de {lib.Name} omitidos: {ex2.Message}");
                }
            }

            nativeDone++;
            progress?.Report(40 + (nativeDone * 10.0 / Math.Max(1, nativeLibs.Count)));
        }

        // 4. Assets (50% → 100%)
        await InstallAssetsAsync(version, assetsDir, progress, cancellationToken);

        progress?.Report(100);
        _logService.Info($"Minecraft {versionId} instalado correctamente");
    }

    private async Task InstallAssetsAsync(MinecraftVersionJson version, string assetsDir, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var indexDir = Path.Combine(assetsDir, "indexes");
        var objectsDir = Path.Combine(assetsDir, "objects");
        Directory.CreateDirectory(indexDir);
        Directory.CreateDirectory(objectsDir);

        var indexId = version.AssetIndex?.Id ?? "5";
        var indexFile = Path.Combine(indexDir, $"{indexId}.json");

        // Asset index (SHA-1 de Mojang sobre la descarga)
        if (!File.Exists(indexFile))
        {
            await DownloadFileAsync(version.AssetIndex!.Url, indexFile, cancellationToken,
                version.AssetIndex.Sha1, version.AssetIndex.Size);
        }

        var indexJson = await File.ReadAllTextAsync(indexFile);
        using var doc = JsonDocument.Parse(indexJson);
        var objects = new List<(string Hash, long Size)>();
        // El índice de Mojang es un MAPA {"ruta": {"hash":..,"size":..}}, no un array
        if (doc.RootElement.TryGetProperty("objects", out var objs)
            && objs.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in objs.EnumerateObject())
            {
                if (!prop.Value.TryGetProperty("hash", out var hashProp)) continue;
                var hash = hashProp.GetString() ?? string.Empty;
                var size = prop.Value.TryGetProperty("size", out var sizeProp)
                    && sizeProp.TryGetInt64(out var s) ? s : 0;
                if (!string.IsNullOrEmpty(hash))
                {
                    objects.Add((hash, size));
                }
            }
        }

        // Descargar assets con concurrencia configurable
        var maxConcurrent = 4;
        try
        {
            maxConcurrent = Math.Clamp((await _settingsService.GetSettingsAsync()).MaxConcurrentDownloads, 1, 8);
        }
        catch
        {
            // Configuración ilegible: valor por defecto
        }

        var semaphore = new SemaphoreSlim(maxConcurrent, 8);
        var downloaded = 0;
        var total = objects.Count;
        var lockObj = new object();

        var tasks = objects.Select(async asset =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var prefix = asset.Hash[..2];
                var destDir = Path.Combine(objectsDir, prefix);
                var dest = Path.Combine(destDir, asset.Hash);

                if (!File.Exists(dest))
                {
                    Directory.CreateDirectory(destDir);
                    var url = $"https://resources.download.minecraft.net/{prefix}/{asset.Hash}";
                    // hash del índice = SHA-1 del objeto de Mojang
                    await DownloadFileAsync(url, dest, cancellationToken,
                        asset.Hash, asset.Size);
                }

                lock (lockObj)
                {
                    downloaded++;
                    progress?.Report(50 + (downloaded * 50.0 / total));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Fallaron 3 intentos: abortar la instalación (un asset
                // ausente rompe el juego y una instalación "exitosa" con
                // huecos solo retrasaba el error al primer arranque).
                _logService.Warning($"No se pudo descargar el asset {asset.Hash}: {ex.Message}");
                throw;
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);
    }

    private static void ExtractNatives(string nativeJar, string nativesDir, CancellationToken cancellationToken)
    {
        try
        {
            using var archive = ZipFile.OpenRead(nativeJar);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(entry.Name)) continue;

                var dest = Path.Combine(nativesDir, entry.Name);
                var fullNatives = Path.GetFullPath(nativesDir) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(dest).StartsWith(fullNatives, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            // Los natives pueden fallar si el jar no es válido; el juego puede seguir
            throw new InvalidDataException($"No se pudieron extraer los natives: {ex.Message}");
        }
    }

    /// <summary>
    /// Clave de classifier nativa de Windows con ${arch} sustituido.
    /// Los version.json antiguos usan "windows" → "natives-windows-${arch}"
    /// (literal), lo que daba null y se instalaban sin natives.
    /// </summary>
    private static string? ResolveNativeKey(VersionLibrary lib)
    {
        var arch = Environment.Is64BitOperatingSystem ? "64" : "32";
        var key = lib.Natives!.GetValueOrDefault("windows")
                  ?? lib.Natives!.GetValueOrDefault("windows-64")
                  ?? lib.Natives!.GetValueOrDefault($"windows-{arch}");
        if (key != null)
        {
            key = key.Replace("${arch}", arch);
            if (lib.Downloads!.Classifiers!.ContainsKey(key)) return key;
        }

        foreach (var candidate in new[]
                 {
                     $"natives-windows-{arch}", "natives-windows-64", "natives-windows"
                 })
        {
            if (lib.Downloads!.Classifiers!.ContainsKey(candidate)) return candidate;
        }

        return key;
    }

    // ---------------------------------------------------------------
    //  Descarga con reintentos, verificación de SHA-1 y escritura
    //  atómica (.tmp + rename): un corte nunca deja un archivo
    //  trunco en el disco que "pase" la verificación de existencia.
    // ---------------------------------------------------------------
    private async Task DownloadFileAsync(string url, string destPath, CancellationToken cancellationToken,
        string? expectedSha1 = null, long expectedSize = 0)
    {
        const int MaxAttempts = 3;
        var attempt = 0;

        while (true)
        {
            attempt++;
            var tmpPath = destPath + ".tmp";
            try
            {
                var dir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromMinutes(10));

                using (await DownloadLimiter.AcquireAsync(cts.Token))
                {
                    using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    resp.EnsureSuccessStatusCode();

                    await using var source = await resp.Content.ReadAsStreamAsync(cts.Token);
                    await using (var target = File.Create(tmpPath))
                    {
                        var buffer = new byte[81920];
                        int read;
                        while ((read = await source.ReadAsync(buffer, cts.Token)) > 0)
                        {
                            await DownloadLimiter.ThrottleAsync(read, cts.Token);
                            await target.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                        }
                    }
                }

                if (expectedSize > 0 && new FileInfo(tmpPath).Length != expectedSize)
                    throw new InvalidDataException(
                        $"Tamaño incorrecto para {Path.GetFileName(destPath)}: " +
                        $"{new FileInfo(tmpPath).Length} != {expectedSize}");

                if (!string.IsNullOrEmpty(expectedSha1))
                {
                    string actualSha1;
                    await using (var fs = File.OpenRead(tmpPath))
                    {
                        actualSha1 = Convert.ToHexString(
                            await SHA1.HashDataAsync(fs, cts.Token));
                    }
                    if (!string.Equals(actualSha1, expectedSha1, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(
                            $"SHA-1 incorrecto para {Path.GetFileName(destPath)}");
                }

                File.Move(tmpPath, destPath, overwrite: true);
                return;
            }
            catch (OperationCanceledException)
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* best effort */ }
                throw;
            }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* best effort */ }
                _logService.Warning($"Descarga falló (intento {attempt}/{MaxAttempts}): {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
            catch (Exception)
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* best effort */ }
                throw;
            }
        }
    }

    // ---------------------------------------------------------------
    //  Rutas para el lanzamiento
    // ---------------------------------------------------------------
    public Task<string> GetClientJarPathAsync(string versionId)
        => Task.FromResult(Path.Combine(VersionDir(versionId), "client.jar"));

    public Task<string> GetAssetsDirAsync(string versionId)
        => Task.FromResult(Path.Combine(VersionDir(versionId), "assets"));

    public Task<string> GetNativesDirAsync(string versionId)
        => Task.FromResult(Path.Combine(VersionDir(versionId), "natives"));

    public Task<string> GetVersionJsonPathAsync(string versionId)
        => Task.FromResult(Path.Combine(VersionDir(versionId), "version.json"));

    public async Task<List<string>> GetLibraryPathsAsync(string versionId)
    {
        var version = await GetVersionAsync(versionId);
        if (version == null) return new List<string>();

        var librariesDir = Path.Combine(VersionDir(versionId), "libraries");
        var paths = new List<string>();

        foreach (var lib in version.Libraries)
        {
            if (!lib.IsAllowedOnCurrentOs()) continue;
            var artifact = lib.Downloads?.Artifact;
            if (artifact == null) continue;

            var libPath = Path.Combine(librariesDir, artifact.Path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(libPath))
            {
                paths.Add(libPath);
            }
        }

        return paths;
    }

    // ---------------------------------------------------------------
    //  Modelos del version JSON de Mojang
    // ---------------------------------------------------------------
    private class VersionManifestEntry
    {
        public string Id { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Type { get; set; } = "release";
        public DateTime ReleaseTime { get; set; }
    }
}

// Modelos JSON de Mojang (solo los campos necesarios)
public class MinecraftVersionJson
{
    public string Id { get; set; } = string.Empty;
    public string MainClass { get; set; } = string.Empty;
    public VersionDownloads Downloads { get; set; } = new();
    public VersionAssetIndex AssetIndex { get; set; } = new();
    public List<VersionLibrary> Libraries { get; set; } = new();
    public JavaVersion JavaVersion { get; set; } = new();
}

public class VersionDownloads
{
    public VersionDownload Client { get; set; } = new();
}

public class VersionDownload
{
    public string Path { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Sha1 { get; set; } = string.Empty;
    public long Size { get; set; }
}

public class VersionAssetIndex
{
    public string Id { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Sha1 { get; set; } = string.Empty;
    public long Size { get; set; }
}

public class VersionLibrary
{
    public string Name { get; set; } = string.Empty;
    public VersionLibraryDownloads Downloads { get; set; } = new();
    public Dictionary<string, string>? Natives { get; set; }
    public List<VersionRule>? Rules { get; set; }

    /// <summary>
    /// Reglas "rules" del version.json de Mojang. Sin reglas → permitida.
    /// Con reglas → la última regla cuyo filtro OS coincida decide.
    /// </summary>
    public bool IsAllowedOnCurrentOs()
    {
        if (Rules == null || Rules.Count == 0) return true;

        var allow = false;
        foreach (var rule in Rules)
        {
            var os = rule.Os?.Name?.Trim().ToLowerInvariant();
            var matches = string.IsNullOrEmpty(os)
                || os == "windows"
                || (os == "windows-64" && Environment.Is64BitOperatingSystem)
                || (os == "windows-32" && !Environment.Is64BitOperatingSystem);
            if (!matches) continue;

            allow = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
        }
        return allow;
    }
}

public class VersionRule
{
    public string Action { get; set; } = string.Empty;
    public VersionRuleOs? Os { get; set; }
}

public class VersionRuleOs
{
    public string? Name { get; set; }
}

public class VersionLibraryDownloads
{
    public VersionDownload? Artifact { get; set; }
    public Dictionary<string, VersionDownload>? Classifiers { get; set; }
}

public class JavaVersion
{
    public int MajorVersion { get; set; }
}
