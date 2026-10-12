using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MineMount.Services;

public interface IForgeInstallService
{
    Task<bool> IsForgeInstalledAsync(string mcVersion);
    Task InstallForgeAsync(string mcVersion, IProgress<double>? progress, CancellationToken cancellationToken);
    Task<string> GetForgeJarPathAsync(string mcVersion);

    /// <summary>
    /// Datos de lanzamiento BootstrapLauncher (Forge &gt;= 1.17) si hay un
    /// version.json de Forge con layout estándar bajo gameDir o en la
    /// instalación oficial reutilizable. Null si no se encuentra.
    /// </summary>
    Task<ForgeLaunchData?> TryGetForgeLaunchDataAsync(string gameDir, string mcVersion);

    /// <summary>
    /// Descarga las librerías del version.json de Forge que falten en disco.
    /// </summary>
    Task EnsureForgeLibrariesAsync(ForgeLaunchData data, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// Todo lo necesario para lanzar con cpw.mods.bootstraplauncher.BootstrapLauncher.
/// </summary>
public class ForgeLaunchData
{
    public string GameRoot { get; set; } = string.Empty;
    public string VersionId { get; set; } = string.Empty;
    public string ForgeJar { get; set; } = string.Empty;
    public string LibrariesDir { get; set; } = string.Empty;
    public List<string> ClasspathLibraries { get; set; } = new();
    public List<string> JvmArgs { get; set; } = new();
    public List<string> GameArgs { get; set; } = new();
}

// Instalación de Forge: descarga el universal jar desde el maven de Forge.
// Forge >= 1.17 (incluida 1.20.1) se lanza con
// cpw.mods.bootstraplauncher.BootstrapLauncher usando el version.json
// de Forge (module-path -p, --add-modules, --add-opens, --launchTarget).
// Forge <= 1.16 usa launchwrapper con el tweak class FMLTweaker.
public class ForgeInstallService : IForgeInstallService
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly ILogService _logService;
    private readonly string _forgeDir;

    // Pines conocidos por versión de MC (sin red). 1.20.1 → 47.4.20
    // (requerido por nse6_world mod).
    private static readonly IReadOnlyDictionary<string, string> KnownForgeVersions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["1.20.1"] = "47.4.20"
        };

    private const string ForgeVersion = "47.4.20"; // fallback del pin 1.20.1

    public ForgeInstallService(ILogService logService)
    {
        _logService = logService;
        _forgeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MineMount", "Games", "Forge");
        Directory.CreateDirectory(_forgeDir);
    }

    private string GamesRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MineMount", "Games", "ForgeMC");

    // ---------------------------------------------------------------
    //  Resolución de la versión de Forge por MC (antes: 47.4.20
    //  hardcodeada para TODO mcVersion → 404 en cualquier otra MC).
    // ---------------------------------------------------------------
    private string ForgeVersionCachePath => Path.Combine(_forgeDir, "forge-versions.json");

    private async Task<string> ResolveForgeVersionAsync(string mcVersion)
    {
        if (KnownForgeVersions.TryGetValue(mcVersion, out var known))
        {
            return known;
        }

        // Caché en disco de resoluciones ya hechas
        try
        {
            if (File.Exists(ForgeVersionCachePath))
            {
                using var cache = JsonDocument.Parse(await File.ReadAllTextAsync(ForgeVersionCachePath));
                if (cache.RootElement.TryGetProperty(mcVersion, out var cached)
                    && cached.GetString() is string v && !string.IsNullOrWhiteSpace(v))
                {
                    return v;
                }
            }
        }
        catch
        {
            // Caché ilegible: se resuelve de nuevo
        }

        // promotions_slim.json de files.minecraftforge.net:
        // { "promos": { "1.20.1-recommended": "47.4.20", ... } }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var resp = await Http.GetAsync(
                "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json",
                cts.Token);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(cts.Token);

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("promos", out var promos))
            {
                var recommended = $"{mcVersion}-recommended";
                var latest = $"{mcVersion}-latest";

                var resolved = promos.TryGetProperty(recommended, out var r) ? r.GetString() : null;
                if (string.IsNullOrWhiteSpace(resolved))
                    resolved = promos.TryGetProperty(latest, out var l) ? l.GetString() : null;

                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    await CacheForgeVersionAsync(mcVersion, resolved);
                    _logService.Info($"Forge para MC {mcVersion}: {resolved} (promotions)");
                    return resolved;
                }
            }
        }
        catch (Exception ex)
        {
            _logService.Warning($"No se pudo resolver la versión de Forge para {mcVersion}: {ex.Message}");
        }

        throw new InvalidOperationException(
            $"No hay una versión de Forge disponible para Minecraft {mcVersion}. " +
            "Revisá que la serie defina una versión de Minecraft soportada.");
    }

    private async Task CacheForgeVersionAsync(string mcVersion, string forgeVersion)
    {
        try
        {
            var data = new Dictionary<string, string>();
            if (File.Exists(ForgeVersionCachePath))
            {
                try
                {
                    using var existing = JsonDocument.Parse(await File.ReadAllTextAsync(ForgeVersionCachePath));
                    foreach (var prop in existing.RootElement.EnumerateObject())
                    {
                        if (prop.Value.GetString() is string v) data[prop.Name] = v;
                    }
                }
                catch
                {
                    // Caché corrupta: se reescribe
                }
            }

            data[mcVersion] = forgeVersion;
            var tmp = ForgeVersionCachePath + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, ForgeVersionCachePath, overwrite: true);
        }
        catch
        {
            // Caché best-effort: la resolución vuelve a funcionar sin ella
        }
    }

    public Task<bool> IsForgeInstalledAsync(string mcVersion)
    {
        // Cualquier Forge instalado para esta MC sirve (glob), no solo el pin.
        var jsonPath = FindForgeVersionJson(GamesRoot, mcVersion);
        if (jsonPath == null) return Task.FromResult(false);

        var fv = ExtractForgeVersion(Path.GetFileName(Path.GetDirectoryName(jsonPath)!), mcVersion);
        var jar = GetForgeJarPath(mcVersion, fv);
        return Task.FromResult(File.Exists(jar));
    }

    /// <summary>"1.20.1-forge-47.4.20" + mc "1.20.1" → "47.4.20".</summary>
    private static string ExtractForgeVersion(string versionId, string mcVersion)
    {
        var prefix = mcVersion + "-forge-";
        return versionId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? versionId[prefix.Length..]
            : ForgeVersion;
    }

    public async Task InstallForgeAsync(string mcVersion, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var fv = await ResolveForgeVersionAsync(mcVersion);

        var jar = GetForgeJarPath(mcVersion, fv);
        var versionJson = FindForgeVersionJsonForVersion(GamesRoot, mcVersion, fv);

        if (File.Exists(jar) && versionJson != null)
        {
            progress?.Report(100);
            return;
        }

        var url = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{mcVersion}-{fv}/forge-{mcVersion}-{fv}-universal.jar";

        progress?.Report(10);
        _logService.Info($"Descargando Forge {mcVersion}-{fv}...");

        const int MaxAttempts = 3;
        var attempt = 0;

        while (true)
        {
            attempt++;
            try
            {
                await DownloadVerifiedAsync(url, jar, progress,
                    p => 10 + (p * 0.8), cancellationToken);

                // Run Forge installer to create version.json and libraries
                await RunForgeInstallerAsync(mcVersion, fv, jar, progress, cancellationToken);

                progress?.Report(100);
                _logService.Info($"Forge {mcVersion}-{fv} instalado");
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                _logService.Warning($"Descarga de Forge falló (intento {attempt}/{MaxAttempts}): {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
        }
    }

    /// <summary>
    /// Descarga a .tmp + verificación SHA-1 (publicado en el maven) +
    /// rename atómico: un jar parcial o corrupto jamás queda en destino.
    /// </summary>
    private async Task DownloadVerifiedAsync(
        string url, string destPath, IProgress<double>? progress,
        Func<double, double> mapProgress, CancellationToken cancellationToken)
    {
        var tmpPath = destPath + ".tmp";
        try
        {
            // SHA-1 publicado por el maven (archivo .sha1 al lado del jar)
            string? expectedSha1 = null;
            try
            {
                using var shaCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                shaCts.CancelAfter(TimeSpan.FromSeconds(30));
                var shaText = await Http.GetStringAsync(url + ".sha1", shaCts.Token);
                var hex = shaText.Trim().Split(' ')[0];
                if (hex.Length == 40) expectedSha1 = hex;
            }
            catch
            {
                _logService.Warning($"Sin SHA-1 publicado para {url}: se descarga igualmente");
            }

            using (await DownloadLimiter.AcquireAsync(cancellationToken))
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromMinutes(10));

                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                resp.EnsureSuccessStatusCode();

                var total = resp.Content.Headers.ContentLength ?? -1;
                long received = 0;

                await using var source = await resp.Content.ReadAsStreamAsync(cts.Token);
                await using var target = File.Create(tmpPath);

                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, cts.Token)) > 0)
                {
                    await DownloadLimiter.ThrottleAsync(read, cts.Token);
                    await target.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                    received += read;

                    if (total > 0)
                    {
                        progress?.Report(mapProgress(received * 100.0 / total));
                    }
                }
            }

            if (expectedSha1 != null)
            {
                using var sha = System.Security.Cryptography.SHA1.Create();
                await using var fs = File.OpenRead(tmpPath);
                var actual = Convert.ToHexString(await sha.ComputeHashAsync(fs, cancellationToken));
                if (!string.Equals(actual, expectedSha1, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"SHA-1 de {Path.GetFileName(destPath)} no coincide");
                }
            }

            File.Move(tmpPath, destPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tmpPath)) File.Delete(tmpPath);
            }
            catch
            {
                // Temporal: lo limpia el servicio de temporales
            }
        }
    }

    private async Task RunForgeInstallerAsync(
        string mcVersion, string forgeVersion, string universalJar,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(90);

        // OJO: el universal jar NO es ejecutable (sin Main-Class). Hay que
        // descargar y correr el installer jar oficial.
        var installerPath = Path.Combine(_forgeDir, $"forge-{mcVersion}-{forgeVersion}-installer.jar");
        if (!File.Exists(installerPath) || new FileInfo(installerPath).Length < 1024 * 1024)
        {
            try { if (File.Exists(installerPath)) File.Delete(installerPath); } catch { /* reintento limpio */ }

            var installerUrl = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{mcVersion}-{forgeVersion}/forge-{mcVersion}-{forgeVersion}-installer.jar";
            _logService.Info($"Descargando instalador de Forge {mcVersion}-{forgeVersion}...");

            await DownloadVerifiedAsync(installerUrl, installerPath, progress,
                p => 70 + (p * 0.2), cancellationToken);

            if (new FileInfo(installerPath).Length < 1024 * 1024)
                throw new InvalidOperationException("El instalador de Forge descargado está incompleto");
        }

        _logService.Info($"Ejecutando instalador de Forge {mcVersion}-{forgeVersion}...");

        var java = await FindJavaForInstallerAsync(cancellationToken);
        if (java == null)
            throw new InvalidOperationException("No se encontró Java para ejecutar el instalador de Forge");

        var targetDir = GamesRoot;

        Directory.CreateDirectory(targetDir);

        // El instalador oficial exige un launcher_profiles.json con un perfil
        // para la versión vanilla ("you need to run the launcher first").
        EnsureLauncherProfile(targetDir, mcVersion);

        var psi = new ProcessStartInfo
        {
            FileName = java,
            Arguments = $"-jar \"{installerPath}\" --installClient \"{targetDir}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
                throw new InvalidOperationException("No se pudo iniciar el instalador de Forge");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(15));

            // AMBOS streams en paralelo ANTES de esperar: leerlos en serie
            // provocaba deadlock clásico de pipes (stderr lleno → stdout
            // nunca cierra → espera infinita con la UI en "Instalando Forge").
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Matar el instalador colgado (no dejar procesos zombi)
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ya salió
                }

                try { await Task.WhenAll(outputTask, errorTask); } catch { /* abortado */ }
                throw;
            }

            var output = await outputTask;
            var error = await errorTask;

            if (process.ExitCode != 0)
            {
                _logService.Error($"Forge installer falló (exit {process.ExitCode}): {error}\n{output}");
                // Instalador corrupto ≥1MB no se re-descarga solo: borrarlo
                // para que el próximo intento lo vuelva a bajar.
                try { File.Delete(installerPath); } catch { /* best effort */ }
                throw new InvalidOperationException($"Forge installer falló (exit {process.ExitCode}). Revisá tu conexión e intentá de nuevo.");
            }

            _logService.Info("Instalador de Forge completado correctamente");

            // El instalador crea el version.json + librerías, pero no el jar
            // de la versión: se usa el universal jar como client jar.
            var versionDir = Path.Combine(targetDir, "versions", $"{mcVersion}-forge-{forgeVersion}");
            Directory.CreateDirectory(versionDir);
            var targetJar = Path.Combine(versionDir, $"{mcVersion}-forge-{forgeVersion}.jar");
            if (!File.Exists(targetJar))
            {
                File.Copy(universalJar, targetJar, overwrite: true);
                _logService.Info($"Jar de Forge copiado a {targetJar}");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logService.Error($"Error ejecutando instalador Forge: {ex.Message}");
            throw;
        }
    }

    private static void EnsureLauncherProfile(string targetDir, string mcVersion)
    {
        try
        {
            var profilesPath = Path.Combine(targetDir, "launcher_profiles.json");
            if (File.Exists(profilesPath)) return;

            var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var json = "{\n"
                + "  \"profiles\": {\n"
                + $"    \"{mcVersion}\": {{\n"
                + $"      \"name\": \"{mcVersion}\",\n"
                + "      \"type\": \"custom\",\n"
                + $"      \"created\": \"{now}\",\n"
                + $"      \"lastUsed\": \"{now}\",\n"
                + "      \"icon\": \"Grass\",\n"
                + $"      \"lastVersionId\": \"{mcVersion}\"\n"
                + "    }\n"
                + "  },\n"
                + "  \"version\": 3\n"
                + "}";
            File.WriteAllText(profilesPath, json);
        }
        catch
        {
            // Si no se puede crear, el instalador dará su propio error.
        }
    }

    private async Task<string?> FindJavaForInstallerAsync(CancellationToken cancellationToken)
    {
        // Buscar Java 17+ (necesario para Forge 1.20.1)
        var candidates = new List<string>
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MineMount", "Java", "17", "bin", "java.exe"),
            "java"
        };

        foreach (var candidate in candidates)
        {
            Process? p = null;
            Task<string>? outputTask = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = candidate == "java" ? "java" : candidate,
                    Arguments = "-version",
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                p = Process.Start(psi);
                if (p == null) continue;

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                outputTask = p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync(cts.Token);
                var output = await outputTask;
                outputTask = null;

                var match = System.Text.RegularExpressions.Regex.Match(output, @"version ""(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out var major) && major >= 17)
                {
                    var full = candidate == "java" ? "java" : Path.GetFullPath(candidate);
                    if (File.Exists(full) || candidate == "java")
                        return full;
                }
            }
            catch
            {
                // Timeout o probe inválido: matar y probar siguiente
                try
                {
                    if (p is { HasExited: false }) p.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ya salió
                }

                if (outputTask is not null)
                {
                    try { await outputTask; } catch { /* abortado */ }
                }
            }
            finally
            {
                p?.Dispose();
            }
        }
        return null;
    }

    public Task<string> GetForgeJarPathAsync(string mcVersion)
        => Task.FromResult(GetForgeJarPath(mcVersion));

    private string GetForgeJarPath(string mcVersion, string forgeVersion)
        => Path.Combine(_forgeDir, $"forge-{mcVersion}-{forgeVersion}-universal.jar");

    /// <summary>
    /// Ruta del universal jar para la serie clásica: usa el Forge realmente
    /// instalado (glob) y solo cae al pin 1.20.1 si no hay nada instalado.
    /// </summary>
    private string GetForgeJarPath(string mcVersion)
    {
        var jsonPath = FindForgeVersionJson(GamesRoot, mcVersion);
        if (jsonPath != null)
        {
            var versionId = Path.GetFileName(Path.GetDirectoryName(jsonPath)!);
            return GetForgeJarPath(mcVersion, ExtractForgeVersion(versionId, mcVersion));
        }

        return GetForgeJarPath(mcVersion, KnownForgeVersions.TryGetValue(mcVersion, out var v) ? v : ForgeVersion);
    }

    // ---------------------------------------------------------------
    //  Lanzamiento moderno (BootstrapLauncher, Forge >= 1.17):
    //  el version.json de Forge trae las librerías y los argumentos
    //  JVM (-p module-path, --add-modules, --add-opens) ya resueltos
    //  con placeholders ${library_directory}, ${classpath_separator}
    //  y ${version_name}.
    // ---------------------------------------------------------------

    /// <summary>
    /// Detecta si una librería va al module-path (-p) basándose en su nombre.
    /// </summary>
    private static bool IsModulePathLibrary(string name)
        => name.StartsWith("cpw.mods:bootstraplauncher:", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("cpw.mods:securejarhandler:", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("org.ow2.asm:", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("net.minecraftforge:JarJarFileSystems:", StringComparison.OrdinalIgnoreCase);

    public async Task<ForgeLaunchData?> TryGetForgeLaunchDataAsync(string gameDir, string mcVersion)
    {
        try
        {
            // 1. Layout estándar bajo el gameDir de la serie (cualquier
            //    versión de Forge instalada para esta MC, no solo el pin).
            var jsonPath = FindForgeVersionJson(gameDir, mcVersion);

            // 2. Instalación oficial reutilizable (la crea el instalador de Forge).
            if (jsonPath == null)
            {
                jsonPath = FindForgeVersionJson(GamesRoot, mcVersion);
            }

            if (jsonPath == null) return null;

            // jsonPath = <gameRoot>\versions\<versionId>\<versionId>.json
            var versionDir = Path.GetDirectoryName(jsonPath)!;
            var versionsDir = Path.GetDirectoryName(versionDir)!;
            var gameRoot = Path.GetDirectoryName(versionsDir)!;
            var versionId = Path.GetFileNameWithoutExtension(jsonPath);
            var librariesDir = Path.Combine(gameRoot, "libraries");
            var forgeJar = Path.Combine(gameRoot, "versions", versionId, versionId + ".jar");
            if (!File.Exists(forgeJar)) return null;

            var json = await File.ReadAllTextAsync(jsonPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var classpath = new List<string>();

            if (root.TryGetProperty("libraries", out var libs))
            {
                foreach (var lib in libs.EnumerateArray())
                {
                    if (!lib.TryGetProperty("name", out var nameProp) ||
                        nameProp.GetString() is not string n || string.IsNullOrWhiteSpace(n))
                        continue;

                    if (IsModulePathLibrary(n)) continue;

                    // downloads.artifact.path es la fuente de verdad (incluye
                    // classifier, p.ej. mergetool-1.1.5-api.jar); parsear el
                    // nombre Maven perdía el classifier y daba 404/ruta mal.
                    var rel = GetLibraryRelativePath(lib, n);
                    if (rel != null)
                        classpath.Add(Path.Combine(librariesDir, rel));
                }
            }

            var jvmArgs = new List<string>();
            var gameArgs = new List<string>();
            if (root.TryGetProperty("arguments", out var args))
            {
                if (args.TryGetProperty("jvm", out var jvm))
                {
                    foreach (var a in jvm.EnumerateArray())
                    {
                        if (a.ValueKind == JsonValueKind.String)
                            jvmArgs.Add(SubstitutePlaceholders(a.GetString()!, librariesDir, versionId));
                    }
                }
                if (args.TryGetProperty("game", out var game))
                {
                    foreach (var a in game.EnumerateArray())
                    {
                        if (a.ValueKind == JsonValueKind.String)
                            gameArgs.Add(a.GetString()!);
                    }
                }
            }

            return new ForgeLaunchData
            {
                GameRoot = gameRoot,
                VersionId = versionId,
                ForgeJar = forgeJar,
                LibrariesDir = librariesDir,
                ClasspathLibraries = classpath,
                JvmArgs = jvmArgs,
                GameArgs = gameArgs
            };
        }
        catch (Exception ex)
        {
            _logService.Warning($"No se pudo leer el version.json de Forge para {mcVersion}: {ex.Message}");
            return null;
        }
    }

    public async Task EnsureForgeLibrariesAsync(ForgeLaunchData data, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // Obtener librerías de module-path del version.json
        var jsonPath = Path.Combine(data.LibrariesDir, "..", "versions", data.VersionId, data.VersionId + ".json");
        jsonPath = Path.GetFullPath(jsonPath);
        var moduleLibRels = new List<string>();

        if (File.Exists(jsonPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(jsonPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("libraries", out var libs))
                {
                    foreach (var lib in libs.EnumerateArray())
                    {
                        if (lib.TryGetProperty("name", out var nameProp) &&
                            nameProp.GetString() is string n && !string.IsNullOrWhiteSpace(n) &&
                            IsModulePathLibrary(n))
                        {
                            var rel = GetLibraryRelativePath(lib, n);
                            if (rel != null) moduleLibRels.Add(rel);
                        }
                    }
                }
            }
            catch { /* ignore */ }
        }

        var allLibPaths = new List<string>(data.ClasspathLibraries);
        foreach (var rel in moduleLibRels)
        {
            allLibPaths.Add(Path.Combine(data.LibrariesDir, rel));
        }

        var missing = allLibPaths
            .Where(p => !File.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missing.Count == 0)
        {
            progress?.Report(100);
            return;
        }

        var done = 0;
        foreach (var dest in missing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(data.LibrariesDir, dest).Replace(Path.DirectorySeparatorChar, '/');
            var tried = new List<string>
            {
                "https://maven.minecraftforge.net/" + rel,
                "https://repo1.maven.org/maven2/" + rel
            };

            Exception? lastError = null;
            var completed = false;
            try
            {
                foreach (var url in tried)
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        cts.CancelAfter(TimeSpan.FromMinutes(5));
                        using (await DownloadLimiter.AcquireAsync(cts.Token))
                        {
                            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                            resp.EnsureSuccessStatusCode();
                            await using var source = await resp.Content.ReadAsStreamAsync(cts.Token);
                            await using var target = File.Create(dest);
                            var buffer = new byte[81920];
                            int read;
                            while ((read = await source.ReadAsync(buffer, cts.Token)) > 0)
                            {
                                await DownloadLimiter.ThrottleAsync(read, cts.Token);
                                await target.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                            }
                        }
                        lastError = null;
                        completed = true;
                        break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        lastError = ex;
                        try { if (File.Exists(dest)) File.Delete(dest); } catch { /* reintento limpio */ }
                    }
                }
            }
            finally
            {
                // También en la ruta de cancelación (timeout del CTS):
                // antes un .jar a medias quedaba en el classpath para
                // siempre y FML crasheaba al arrancar.
                if (!completed)
                {
                    try { if (File.Exists(dest)) File.Delete(dest); } catch { /* best effort */ }
                }
            }

            if (lastError != null)
                throw new InvalidOperationException($"No se pudo descargar la librería de Forge: {rel}", lastError);

            done++;
            progress?.Report(done * 100.0 / missing.Count);
        }

        _logService.Info($"Librerías de Forge verificadas ({missing.Count} descargadas)");
    }

    private static string? FindForgeVersionJson(string gameRoot, string mcVersion)
    {
        try
        {
            var versionsDir = Path.Combine(gameRoot, "versions");
            if (!Directory.Exists(versionsDir)) return null;

            foreach (var dir in Directory.EnumerateDirectories(versionsDir, mcVersion + "-forge-*"))
            {
                var id = Path.GetFileName(dir);
                var json = Path.Combine(dir, id + ".json");
                if (File.Exists(json)) return json;
            }
        }
        catch
        {
            // Sin acceso: sin datos de Forge
        }
        return null;
    }

    private static string? FindForgeVersionJsonForVersion(string gameRoot, string mcVersion, string forgeVersion)
    {
        try
        {
            var versionsDir = Path.Combine(gameRoot, "versions");
            if (!Directory.Exists(versionsDir)) return null;

            var expectedDir = $"{mcVersion}-forge-{forgeVersion}";
            var dir = Path.Combine(versionsDir, expectedDir);
            if (Directory.Exists(dir))
            {
                var json = Path.Combine(dir, expectedDir + ".json");
                if (File.Exists(json)) return json;
            }
        }
        catch
        {
            // Sin acceso: sin datos de Forge
        }
        return null;
    }

    private static string SubstitutePlaceholders(string value, string librariesDir, string versionId)
        => value
            .Replace("${library_directory}", librariesDir)
            .Replace("${classpath_separator}", ";")
            .Replace("${version_name}", versionId);

    private static string? MavenNameToRelativePath(string name)
    {
        var parts = name.Split(':');
        if (parts.Length < 3) return null;
        var groupPath = parts[0].Replace('.', Path.DirectorySeparatorChar);
        var artifact = parts[1];
        var version = parts[2];
        // classifier (4ª parte) → artifact-version-classifier.jar
        var classifier = parts.Length >= 4 && !string.IsNullOrWhiteSpace(parts[3]) ? parts[3] : null;
        var fileName = classifier != null
            ? $"{artifact}-{version}-{classifier}.jar"
            : $"{artifact}-{version}.jar";
        return Path.Combine(groupPath, artifact, version, fileName);
    }

    /// <summary>
    /// Ruta relativa de una librería: prefiere downloads.artifact.path del
    /// version.json (fuente de verdad, con classifier) y cae al parseo del
    /// nombre Maven.
    /// </summary>
    private static string? GetLibraryRelativePath(JsonElement lib, string name)
    {
        try
        {
            if (lib.TryGetProperty("downloads", out var downloads)
                && downloads.TryGetProperty("artifact", out var artifact)
                && artifact.TryGetProperty("path", out var pathProp)
                && pathProp.GetString() is string path
                && !string.IsNullOrWhiteSpace(path))
            {
                return path.Replace('/', Path.DirectorySeparatorChar);
            }
        }
        catch
        {
            // JSON raro: fallback al nombre Maven
        }

        return MavenNameToRelativePath(name);
    }
}
