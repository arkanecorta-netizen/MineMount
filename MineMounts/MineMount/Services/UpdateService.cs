using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace MineMount.Services;

public interface IUpdateService
{
    Task<bool> CheckForUpdatesAsync();
    Task<bool> DownloadUpdateAsync(IProgress<double>? progress = null);
    bool ApplyAndRestart();
    string CurrentVersion { get; }
    string LatestVersion { get; }
    bool IsUpdateAvailable { get; }
    bool CanAutoApply { get; }
    string LastError { get; }
    string ReleaseNotes { get; }
}

public class UpdateService : IUpdateService
{
    private const string RepoOwner = "arkanecorta-netizen";
    private const string RepoName = "MineMount";
    private const string PackageName = "MineMount-Update.zip";
    private const int MaxApplyAttempts = 2;

    private static readonly HttpClient Http = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly ILogService _log;
    private readonly INotificationService _notifications;
    private readonly string _appDataDir;

    private Uri? _packageUrl;
    private string? _expectedSha256;

    public UpdateService(ILogService logService, INotificationService notifications)
    {
        _log = logService;
        _notifications = notifications;
        _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MineMount");
        CurrentVersion = ReadCurrentVersion();
    }

    // Override para pruebas: MINEMOUNT_UPDATE_API=http://127.0.0.1:port
    private static string ApiBase =>
        Environment.GetEnvironmentVariable("MINEMOUNT_UPDATE_API")?.TrimEnd('/')
        ?? "https://api.github.com";

    public string CurrentVersion { get; }
    public string LatestVersion { get; private set; } = string.Empty;
    public bool IsUpdateAvailable { get; private set; }
    public string LastError { get; private set; } = string.Empty;
    public string ReleaseNotes { get; private set; } = string.Empty;
    public bool CanAutoApply { get; private set; } = true;

    // ---------------------------------------------------------------
    //  1. Comprobación contra GitHub Releases
    // ---------------------------------------------------------------
    public async Task<bool> CheckForUpdatesAsync()
    {
        IsUpdateAvailable = false;
        LastError = string.Empty;
        _packageUrl = null;
        _expectedSha256 = null;

        var url = $"{ApiBase}/repos/{RepoOwner}/{RepoName}/releases/latest";

        try
        {
            _log.Info($"Buscando actualizaciones: {url} (actual: {CurrentVersion})");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("MineMount-Updater/1.0");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var resp = await Http.SendAsync(req, cts.Token);
            var status = (int)resp.StatusCode;

            if (!resp.IsSuccessStatusCode)
            {
                if (status == 404)
                {
                    LastError = "No hay releases publicadas en GitHub";
                    _log.Warning("Check updates: 404 — no hay releases publicadas");
                }
                else if (status >= 500)
                {
                    LastError = $"GitHub no responde (HTTP {status})";
                    _log.Warning($"Check updates: GitHub devolvió HTTP {status}");
                }
                else
                {
                    LastError = $"GitHub devolvió HTTP {status}";
                    _log.Warning($"Check updates: HTTP {status}");
                }

                return false;
            }

            var json = await resp.Content.ReadAsStringAsync(cts.Token);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagProp)
                ? tagProp.GetString() ?? string.Empty
                : string.Empty;

            ReleaseNotes = root.TryGetProperty("body", out var bodyProp)
                ? bodyProp.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(tag))
            {
                LastError = "Respuesta inválida de GitHub (sin tag)";
                _log.Warning("Check updates: respuesta sin tag_name");
                return false;
            }

            LatestVersion = NormalizeVersion(tag);

            if (root.TryGetProperty("assets", out var assets) &&
                assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (!string.Equals(name, PackageName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var downloadUrl = asset.TryGetProperty("browser_download_url", out var u)
                        ? u.GetString()
                        : null;

                    if (Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri))
                    {
                        _packageUrl = uri;
                    }

                    _expectedSha256 = asset.TryGetProperty("digest", out var d)
                        ? d.GetString()?.Replace("sha256:", string.Empty, StringComparison.OrdinalIgnoreCase)
                        : null;

                    break;
                }
            }

            if (_packageUrl is null)
            {
                LastError = $"La release {tag} no incluye {PackageName}";
                _log.Warning($"Check updates: release {tag} sin asset {PackageName}");
                return false;
            }

            IsUpdateAvailable = IsNewer(LatestVersion, CurrentVersion);
            CanAutoApply = IsUpdateAvailable && !ReachedApplyLimit(LatestVersion);

            _log.Info(IsUpdateAvailable
                ? $"Actualización disponible: {CurrentVersion} → {LatestVersion}"
                : $"Sin actualizaciones (actual: {CurrentVersion}, última: {LatestVersion})");

            return IsUpdateAvailable;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            LastError = "GitHub no responde (tiempo agotado)";
            _log.Warning("Check updates: timeout contra la API de GitHub");
            return false;
        }
        catch (HttpRequestException ex)
        {
            LastError = "Sin conexión a Internet";
            _log.Warning($"Check updates: sin conexión ({ex.Message})");
            return false;
        }
        catch (JsonException ex)
        {
            LastError = "Respuesta inválida de GitHub";
            _log.Warning($"Check updates: JSON inválido ({ex.Message})");
            return false;
        }
        catch (Exception ex)
        {
            LastError = "Error al buscar actualizaciones";
            _log.Error("Check updates failed", ex);
            return false;
        }
    }

    // ---------------------------------------------------------------
    //  2. Descarga con progreso real + verificación de integridad
    // ---------------------------------------------------------------
    public async Task<bool> DownloadUpdateAsync(IProgress<double>? progress = null)
    {
        if (_packageUrl is null)
        {
            LastError ??= $"No hay paquete {PackageName} disponible";
            _log.Warning("Download: no hay URL de paquete");
            return false;
        }

        try
        {
            Directory.CreateDirectory(StageDir);
            var zipPath = Path.Combine(StageDir, PackageName);

            _log.Info($"Descargando actualización desde {_packageUrl}");

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
            using var req = new HttpRequestMessage(HttpMethod.Get, _packageUrl);
            req.Headers.UserAgent.ParseAdd("MineMount-Updater/1.0");

            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"No se pudo descargar el paquete (HTTP {(int)resp.StatusCode})";
                _log.Warning($"Download: HTTP {(int)resp.StatusCode}");
                return false;
            }

            var total = resp.Content.Headers.ContentLength ?? -1;
            long received;
            var lastLoggedDecile = -1;

            // Copia en un ámbito propio: el archivo DEBE cerrarse antes de
            // abrirlo para verificar hash/ZIP (File.Create usa FileShare.None)
            await using (var input = await resp.Content.ReadAsStreamAsync(cts.Token))
            await using (var output = File.Create(zipPath))
            {
                var buffer = new byte[81920];
                int read;
                received = 0;

                using (await DownloadLimiter.AcquireAsync(cts.Token))
                {
                    while ((read = await input.ReadAsync(buffer, cts.Token)) > 0)
                    {
                        await DownloadLimiter.ThrottleAsync(read, cts.Token);
                        await output.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                        received += read;

                        if (total > 0)
                        {
                            var percent = received * 100.0 / total;
                            progress?.Report(percent);

                            var decile = (int)(percent / 10);
                            if (decile != lastLoggedDecile)
                            {
                                lastLoggedDecile = decile;
                                _log.Info($"Descargando actualización: {decile * 10}% ({received / 1024 / 1024} MB)");
                            }
                        }
                    }
                }

                await output.FlushAsync(cts.Token);
            }

            progress?.Report(100);
            _log.Info($"Descarga completa: {received / 1024 / 1024} MB");

            // Verificación 1: hash SHA256 publicado por GitHub (si existe)
            if (!string.IsNullOrWhiteSpace(_expectedSha256))
            {
                await using var fileStream = File.OpenRead(zipPath);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(fileStream, cts.Token));

                if (!string.Equals(actual, _expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    LastError = "El paquete está corrupto (hash incorrecto)";
                    _log.Warning($"Download: hash esperado {_expectedSha256[..16]}... != actual {actual[..16]}...");
                    File.Delete(zipPath);
                    return false;
                }

                _log.Info("Hash SHA256 verificado correctamente");
            }

            // Verificación 2: que sea un ZIP válido y contenga MineMount.exe
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);

                if (!zip.Entries.Any(e =>
                        e.FullName.Equals("MineMount.exe", StringComparison.OrdinalIgnoreCase) ||
                        e.Name.Equals("MineMount.exe", StringComparison.OrdinalIgnoreCase)))
                {
                    LastError = "El paquete no contiene MineMount.exe";
                    _log.Warning("Download: ZIP sin MineMount.exe");
                    File.Delete(zipPath);
                    return false;
                }
            }
            catch (InvalidDataException)
            {
                LastError = "El paquete está corrupto (ZIP inválido)";
                _log.Warning("Download: el archivo no es un ZIP válido");
                File.Delete(zipPath);
                return false;
            }

            _log.Info("Paquete de actualización verificado correctamente");
            return true;
        }
        catch (HttpRequestException)
        {
            LastError = "Sin conexión a Internet";
            _log.Warning("Download: sin conexión durante la descarga");
            return false;
        }
        catch (TaskCanceledException)
        {
            LastError = "Tiempo agotado al descargar";
            _log.Warning("Download: timeout");
            return false;
        }
        catch (InvalidDataException)
        {
            LastError = "El paquete está corrupto";
            return false;
        }
        catch (Exception ex)
        {
            LastError = "Error al descargar la actualización";
            _log.Error("Download failed", ex);
            return false;
        }
    }

    // ---------------------------------------------------------------
    //  3. Aplicar: extraer, lanzar script updater y reiniciar
    // ---------------------------------------------------------------
    public bool ApplyAndRestart()
    {
        try
        {
            var zipPath = Path.Combine(StageDir, PackageName);
            if (!File.Exists(zipPath))
            {
                LastError = "No hay paquete descargado";
                _log.Warning("Apply: no existe el paquete en staging");
                return false;
            }

            var filesDir = Path.Combine(StageDir, "files");
            if (Directory.Exists(filesDir))
            {
                Directory.Delete(filesDir, true);
            }

            ZipFile.ExtractToDirectory(zipPath, filesDir);

            var newExe = Directory.EnumerateFiles(filesDir, "MineMount.exe").FirstOrDefault()
                         ?? Directory.EnumerateFiles(filesDir, "*.exe").FirstOrDefault();

            if (newExe is null)
            {
                LastError = "El paquete no contiene MineMount.exe";
                _log.Warning("Apply: extracción sin .exe");
                return false;
            }

            var appExe = Environment.ProcessPath
                         ?? Path.Combine(AppContext.BaseDirectory, "MineMount.exe");
            var appDir = Path.GetDirectoryName(appExe) + Path.DirectorySeparatorChar;
            var pid = Environment.ProcessId;
            var scriptPath = Path.Combine(StageDir, "apply.cmd");

            // Si la carpeta no es escribible (Program Files), el script se
            // auto-eleva SOLO en ese caso. Nunca se muestra una consola.
            var needsElevation = !IsDirectoryWritable(appDir);
            if (needsElevation)
            {
                _notifications.NotifyWarning(
                    "Permiso necesario",
                    "La actualización necesita permiso de administrador (solo esta vez).");
            }

            var script = BuildApplyScript(pid, newExe, appDir, StageDir, needsElevation);
            File.WriteAllText(scriptPath, script);

            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = $"/c \"\"{scriptPath}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = StageDir
            });

            RegisterApplyAttempt(LatestVersion);
            _log.Info($"Actualización {LatestVersion} aplicándose (script {scriptPath}); reiniciando...");

            Application.Current.Shutdown();
            return true;
        }
        catch (InvalidDataException)
        {
            LastError = "El paquete está corrupto";
            _log.Warning("Apply: el ZIP no se pudo extraer");
            return false;
        }
        catch (Exception ex)
        {
            LastError = "No se pudo aplicar la actualización";
            _log.Error("Apply failed", ex);
            return false;
        }
    }

    // ---------------------------------------------------------------
    //  Utilidades
    // ---------------------------------------------------------------
    private string StageDir => Path.Combine(_appDataDir, "updates", "stage");

    private string StatePath => Path.Combine(_appDataDir, "update-state.json");

    private static string ReadCurrentVersion()
    {
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        return string.IsNullOrWhiteSpace(info) ? "1.0.0" : info.Split('+')[0];
    }

    private static string NormalizeVersion(string tag)
        => tag.Trim().TrimStart('v', 'V');

    private static bool IsNewer(string candidate, string current)
    {
        if (!Version.TryParse(NormalizeVersion(candidate), out var c)) return false;
        if (!Version.TryParse(NormalizeVersion(current), out var cur)) return false;
        return c > cur;
    }

    private bool ReachedApplyLimit(string target)
    {
        try
        {
            if (!File.Exists(StatePath)) return false;

            using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
            var root = doc.RootElement;

            var stateTarget = root.TryGetProperty("Target", out var t) ? t.GetString() : null;
            var attempts = root.TryGetProperty("Attempts", out var a) ? a.GetInt32() : 0;

            return string.Equals(stateTarget, target, StringComparison.OrdinalIgnoreCase)
                   && attempts >= MaxApplyAttempts;
        }
        catch
        {
            return false;
        }
    }

    private void RegisterApplyAttempt(string target)
    {
        try
        {
            Directory.CreateDirectory(_appDataDir);

            var attempts = 0;
            if (File.Exists(StatePath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
                    var root = doc.RootElement;
                    var stateTarget = root.TryGetProperty("Target", out var t) ? t.GetString() : null;
                    if (string.Equals(stateTarget, target, StringComparison.OrdinalIgnoreCase))
                    {
                        attempts = root.TryGetProperty("Attempts", out var a) ? a.GetInt32() : 0;
                    }
                }
                catch
                {
                    attempts = 0;
                }
            }

            var state = JsonSerializer.Serialize(new
            {
                Target = target,
                Attempts = attempts + 1,
                Timestamp = DateTime.UtcNow
            });

            File.WriteAllText(StatePath, state);
        }
        catch (Exception ex)
        {
            _log.Warning($"No se pudo registrar el intento de actualización: {ex.Message}");
        }
    }

    private static string BuildApplyScript(int pid, string newExe, string appDir, string stageDir, bool needsElevation)
    {
        // Script updater, siempre oculto (nunca muestra consola):
        //  1. Espera a que MineMount.exe (pid) termine
        //  2. Copia el nuevo exe al directorio de la app
        //  3. Solo si no hay permiso (Program Files) se relanza elevado,
        //     también oculto (el UAC lo pide Windows, no se puede evitar ahí)
        //  4. Reinicia MineMount y limpia el staging
        var lines = new List<string>
        {
            "@echo off",
            "setlocal",
            $"set \"PID={pid}\"",
            $"set \"NEW={newExe}\"",
            $"set \"APPDIR={appDir}\"",
            $"set \"STAGE={stageDir}\""
        };

        if (needsElevation)
        {
            lines.Add("powershell -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command \"Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', '%~f0' -Verb RunAs -WindowStyle Hidden\"");
            lines.Add("exit /b 0");
        }

        lines.AddRange(new[]
        {
            ":wait",
            "tasklist /FI \"PID eq %PID%\" 2>nul | find \"%PID%\" >nul",
            "if not errorlevel 1 (",
            "  timeout /t 1 /nobreak >nul",
            "  goto wait",
            ")",
            "copy /Y \"%NEW%\" \"%APPDIR%MineMount.exe\" >nul 2>&1",
            "if errorlevel 1 (",
            "  powershell -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command \"Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', '%~f0' -Verb RunAs -WindowStyle Hidden\"",
            "  exit /b 0",
            ")",
            "start \"\" \"%APPDIR%MineMount.exe\"",
            $"powershell -NoProfile -WindowStyle Hidden -Command \"Start-Sleep 5; Remove-Item -Recurse -Force -ErrorAction SilentlyContinue '{stageDir}'\""
        });

        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>Prueba de escritura real (sin elevar): archivo temporal.</summary>
    private static bool IsDirectoryWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".mm-write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
