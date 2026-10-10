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

    // Forge 1.20.1 → 47.4.20 (requerido por nse6_world mod)
    private const string ForgeVersion = "47.4.20";

    public ForgeInstallService(ILogService logService)
    {
        _logService = logService;
        _forgeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MineMount", "Games", "Forge");
        Directory.CreateDirectory(_forgeDir);
    }

    public Task<bool> IsForgeInstalledAsync(string mcVersion)
    {
        var jar = GetForgeJarPath(mcVersion);
        var versionJson = FindForgeVersionJsonForVersion(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MineMount", "Games", "ForgeMC"), mcVersion, ForgeVersion);
        return Task.FromResult(File.Exists(jar) && versionJson != null);
    }

    public async Task InstallForgeAsync(string mcVersion, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var jar = GetForgeJarPath(mcVersion);
        var versionJson = FindForgeVersionJsonForVersion(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MineMount", "Games", "ForgeMC"), mcVersion, ForgeVersion);
        
        if (File.Exists(jar) && versionJson != null)
        {
            progress?.Report(100);
            return;
        }

        var url = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{mcVersion}-{ForgeVersion}/forge-{mcVersion}-{ForgeVersion}-universal.jar";

        progress?.Report(10);
        _logService.Info($"Descargando Forge {mcVersion}-{ForgeVersion}...");

        const int MaxAttempts = 3;
        var attempt = 0;

        while (true)
        {
            attempt++;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromMinutes(10));

                using (await DownloadLimiter.AcquireAsync(cts.Token))
                {
                    using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    resp.EnsureSuccessStatusCode();

                    var total = resp.Content.Headers.ContentLength ?? -1;
                    long received = 0;

                    await using var source = await resp.Content.ReadAsStreamAsync(cts.Token);
                    await using var target = File.Create(jar);

                    var buffer = new byte[81920];
                    int read;
                    while ((read = await source.ReadAsync(buffer, cts.Token)) > 0)
                    {
                        await DownloadLimiter.ThrottleAsync(read, cts.Token);
                        await target.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                        received += read;

                        if (total > 0)
                        {
                            progress?.Report(10 + (received * 90.0 / total));
                        }
                    }
                }

                // Run Forge installer to create version.json and libraries
                await RunForgeInstallerAsync(mcVersion, jar, progress, cancellationToken);

                progress?.Report(100);
                _logService.Info($"Forge {mcVersion}-{ForgeVersion} instalado");
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

    private async Task RunForgeInstallerAsync(string mcVersion, string universalJar, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(90);
        _logService.Info($"Ejecutando instalador de Forge {mcVersion}-{ForgeVersion}...");

        var java = await FindJavaForInstallerAsync(cancellationToken);
        if (java == null)
            throw new InvalidOperationException("No se encontró Java para ejecutar el instalador de Forge");

        var targetDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MineMount", "Games", "ForgeMC");

        Directory.CreateDirectory(targetDir);

        var psi = new ProcessStartInfo
        {
            FileName = java,
            Arguments = $"-jar \"{universalJar}\" --installClient \"{targetDir}\"",
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

                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromMinutes(5));
                await process.WaitForExitAsync(cts.Token);

                if (process.ExitCode != 0)
                {
                    _logService.Error($"Forge installer falló (exit {process.ExitCode}): {error}\n{output}");
                    throw new InvalidOperationException($"Forge installer falló: {error}");
                }

                _logService.Info($"Instalador de Forge completado: {output}");

                // Copiar el universal jar como client jar en la versión instalada
                var versionDir = Path.Combine(targetDir, "versions", $"{mcVersion}-forge-{ForgeVersion}");
                Directory.CreateDirectory(versionDir);
                var targetJar = Path.Combine(versionDir, $"{mcVersion}-forge-{ForgeVersion}.jar");
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
                using var p = Process.Start(psi);
                if (p == null) continue;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                var output = await p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync(cts.Token);

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
                // Ignorar y probar siguiente
            }
        }
        return null;
    }

    public Task<string> GetForgeJarPathAsync(string mcVersion)
        => Task.FromResult(GetForgeJarPath(mcVersion));

    private string GetForgeJarPath(string mcVersion)
        => Path.Combine(_forgeDir, $"forge-{mcVersion}-{ForgeVersion}-universal.jar");

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
            // 1. Layout estándar bajo el gameDir de la serie.
            var jsonPath = FindForgeVersionJsonForVersion(gameDir, mcVersion, ForgeVersion);

            // 2. Instalación oficial reutilizable (la crea el instalador de Forge).
            if (jsonPath == null)
            {
                var shared = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MineMount", "Games", "ForgeMC");
                jsonPath = FindForgeVersionJsonForVersion(shared, mcVersion, ForgeVersion);
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
                    var rel = MavenNameToRelativePath(n);
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
        var moduleLibs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
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
                            moduleLibs.Add(n);
                        }
                    }
                }
            }
            catch { /* ignore */ }
        }

        var allLibPaths = new List<string>(data.ClasspathLibraries);
        foreach (var name in moduleLibs)
        {
            var rel = MavenNameToRelativePath(name);
            if (rel != null)
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
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = ex;
                    try { if (File.Exists(dest)) File.Delete(dest); } catch { /* reintento limpio */ }
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
        return Path.Combine(groupPath, artifact, version, $"{artifact}-{version}.jar");
    }
}
