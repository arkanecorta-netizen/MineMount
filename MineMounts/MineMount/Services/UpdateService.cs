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
    Task<bool> DownloadUpdateAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    Task<string?> DownloadSetupAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    Task<bool> ApplyAndRestartAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default);
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
    private const string SetupAssetName = "MineMount-Setup.exe";
    private const int MaxApplyAttempts = 2;

    private static readonly HttpClient Http = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly ILogService _log;
    private readonly INotificationService _notifications;
    private readonly string _appDataDir;

    // Serializa check → download → apply: sin esto, dos llamadas
    // concurrentes (Home + Ajustes) pueden lanzar dos apply.cmd o perder
    // la URL del paquete a mitad de descarga.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Uri? _packageUrl;
    private Uri? _setupUrl;
    private string? _expectedSha256;
    private string? _setupSha256;

    public UpdateService(ILogService logService, INotificationService notifications)
    {
        _log = logService;
        _notifications = notifications;
        _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MineMount");
        CurrentVersion = ReadCurrentVersion();
        TryClearCompletedState();
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
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(40)))
        {
            LastError = "Ya hay una operación de actualización en curso";
            return false;
        }

        try
        {
            IsUpdateAvailable = false;
            LastError = string.Empty;
            _packageUrl = null;
            _setupUrl = null;
            _expectedSha256 = null;
            _setupSha256 = null;

            var url = $"{ApiBase}/repos/{RepoOwner}/{RepoName}/releases/latest";

            try
            {
                _log.Info($"Buscando actualizaciones: {url} (actual: {CurrentVersion})");

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd("MineMount-Updater/1.0");
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

                using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
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

                var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

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

                // Tag SIN normalizar (1.1.16c conserva la letra): IsNewer
                // compara sufijos; solo mostrar/almacenar se usa el tag crudo.
                LatestVersion = tag.Trim().TrimStart('v', 'V');

                if (root.TryGetProperty("assets", out var assets) &&
                    assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        var downloadUrl = asset.TryGetProperty("browser_download_url", out var u)
                            ? u.GetString()
                            : null;
                        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri)) continue;

                        var digest = asset.TryGetProperty("digest", out var d)
                            ? d.GetString()?.Replace("sha256:", string.Empty, StringComparison.OrdinalIgnoreCase)
                            : null;

                        if (string.Equals(name, PackageName, StringComparison.OrdinalIgnoreCase))
                        {
                            _packageUrl = uri;
                            _expectedSha256 = digest;
                        }
                        else if (string.Equals(name, SetupAssetName, StringComparison.OrdinalIgnoreCase))
                        {
                            _setupUrl = uri;
                            _setupSha256 = digest;
                        }
                    }
                }

                if (_packageUrl is null)
                {
                    LastError = $"La release {tag} no incluye {PackageName}";
                    _log.Warning($"Check updates: release {tag} sin asset {PackageName}");
                    return false;
                }

                // Fail-closed: sin hash publicado no se actualiza (el zip
                // descargado es código que se ejecuta después).
                if (string.IsNullOrWhiteSpace(_expectedSha256))
                {
                    LastError = "La release no incluye hash de integridad";
                    _log.Warning($"Check updates: {PackageName} sin digest sha256");
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
            catch (OperationCanceledException)
            {
                // El CTS de 30 s canceló (sin TimeoutException interna).
                LastError = "GitHub no responde (tiempo agotado)";
                _log.Warning("Check updates: cancelado por timeout de 30 s");
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
        finally
        {
            _gate.Release();
        }
    }

    // ---------------------------------------------------------------
    //  2. Descarga con progreso real + verificación de integridad
    // ---------------------------------------------------------------
    public async Task<bool> DownloadUpdateAsync(
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            LastError = "Ya hay una operación de actualización en curso";
            return false;
        }

        try
        {
            if (_packageUrl is null)
            {
                LastError = string.IsNullOrEmpty(LatestVersion)
                    ? $"No hay paquete {PackageName} disponible (buscá actualizaciones primero)"
                    : $"La release {LatestVersion} no incluye {PackageName}";
                _log.Warning("Download: no hay URL de paquete");
                return false;
            }

            // Fail-closed: sin hash no se ejecuta nada descargado.
            if (string.IsNullOrWhiteSpace(_expectedSha256))
            {
                LastError = "La release no incluye hash de integridad";
                _log.Warning("Download: sin hash esperado — se aborta");
                return false;
            }

            try
            {
                Directory.CreateDirectory(StageDir);
                var zipPath = Path.Combine(StageDir, PackageName);

                // Si un intento anterior ya dejó el ZIP válido en staging, no se
                // vuelve a descargar (evita el bucle "descarga → falla → descarga").
                if (File.Exists(zipPath))
                {
                    try
                    {
                        await using var existing = File.OpenRead(zipPath);
                        var existingHash = Convert.ToHexString(
                            await SHA256.HashDataAsync(existing, CancellationToken.None).ConfigureAwait(false));
                        if (string.Equals(existingHash, _expectedSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            _log.Info("Paquete ya en staging y verificado: se omite la descarga");
                            progress?.Report(100);
                            return true;
                        }

                        _log.Warning("ZIP en staging corrupto: se descarga de nuevo");
                        File.Delete(zipPath);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning($"No se pudo verificar el ZIP en staging: {ex.Message}");
                    }
                }

                _log.Info($"Descargando actualización desde {_packageUrl}");

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromMinutes(20));
                using var req = new HttpRequestMessage(HttpMethod.Get, _packageUrl);
                req.Headers.UserAgent.ParseAdd("MineMount-Updater/1.0");

                using var resp = await Http.SendAsync(
                    req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);

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
                await using (var input = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false))
                await using (var output = File.Create(zipPath))
                {
                    var buffer = new byte[81920];
                    int read;
                    received = 0;

                    using (await DownloadLimiter.AcquireAsync(cts.Token).ConfigureAwait(false))
                    {
                        while ((read = await input.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
                        {
                            await DownloadLimiter.ThrottleAsync(read, cts.Token).ConfigureAwait(false);
                            await output.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
                            received += read;

                            if (total > 0)
                            {
                                // 95 % máx.: el 100 % es recién con todo verificado.
                                var percent = received * 100.0 / total;
                                progress?.Report(percent * 0.95);

                                var decile = (int)(percent / 10);
                                if (decile != lastLoggedDecile)
                                {
                                    lastLoggedDecile = decile;
                                    _log.Info($"Descargando actualización: {decile * 10}% ({received / 1024 / 1024} MB)");
                                }
                            }
                        }
                    }

                    await output.FlushAsync(cts.Token).ConfigureAwait(false);
                }

                _log.Info($"Descarga completa: {received / 1024 / 1024} MB");

                // Verificación 1: hash SHA256 publicado por GitHub (obligatorio)
                await using (var fileStream = File.OpenRead(zipPath))
                {
                    var actual = Convert.ToHexString(
                        await SHA256.HashDataAsync(fileStream, cts.Token).ConfigureAwait(false));

                    if (!string.Equals(actual, _expectedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        LastError = "El paquete está corrupto (hash incorrecto)";
                        _log.Warning($"Download: hash esperado {_expectedSha256[..16]}... != actual {actual[..16]}...");
                        File.Delete(zipPath);
                        return false;
                    }
                }

                _log.Info("Hash SHA256 verificado correctamente");

                // Verificación 2: ZIP válido con MineMount.exe en la RAÍZ
                // (el apply asume raíz: un .exe en subcarpeta no sirve).
                try
                {
                    using var zip = ZipFile.OpenRead(zipPath);

                    if (!zip.Entries.Any(e =>
                            e.FullName.Equals("MineMount.exe", StringComparison.OrdinalIgnoreCase)))
                    {
                        LastError = "El paquete no contiene MineMount.exe";
                        _log.Warning("Download: ZIP sin MineMount.exe en la raíz");
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

                progress?.Report(100);
                _log.Info("Paquete de actualización verificado correctamente");
                return true;
            }
            catch (HttpRequestException)
            {
                LastError = "Sin conexión a Internet";
                _log.Warning("Download: sin conexión durante la descarga");
                return false;
            }
            catch (OperationCanceledException)
            {
                // Distinguir cancelación del usuario del timeout interno.
                LastError = cancellationToken.IsCancellationRequested
                    ? "Actualización cancelada"
                    : "Tiempo agotado al descargar";
                _log.Warning($"Download: {(cancellationToken.IsCancellationRequested ? "cancelado" : "timeout")}");
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
        finally
        {
            _gate.Release();
        }
    }

    // ---------------------------------------------------------------
    //  2b. Instalador completo como plan B (si el auto-update falla en
    //  bucle, el usuario lo ejecuta a mano: el instalador sí eleva bien)
    // ---------------------------------------------------------------
    public async Task<string?> DownloadSetupAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            LastError = "Ya hay una operación de actualización en curso";
            return null;
        }

        try
        {
            return await DownloadSetupCoreAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Descarga el setup SIN tomar el gate (ya tomado por el caller).</summary>
    private async Task<string?> DownloadSetupCoreAsync(
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (_setupUrl is null)
        {
            LastError = "La release no incluye el instalador";
            _log.Warning("DownloadSetup: sin URL de setup");
            return null;
        }

        var partialPath = (string?)null;
        try
        {
            var downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
            Directory.CreateDirectory(downloads);
            var destPath = Path.Combine(downloads, $"MineMount-Setup-{LatestVersion}.exe");
            partialPath = destPath + ".partial";

            _log.Info($"Descargando instalador desde {_setupUrl}");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(30));
            using var req = new HttpRequestMessage(HttpMethod.Get, _setupUrl);
            req.Headers.UserAgent.ParseAdd("MineMount-Updater/1.0");

            using var resp = await Http.SendAsync(
                req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"No se pudo descargar el instalador (HTTP {(int)resp.StatusCode})";
                return null;
            }

            var total = resp.Content.Headers.ContentLength ?? -1;
            long received = 0;

            await using (var input = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false))
            await using (var output = File.Create(partialPath))
            {
                var buffer = new byte[81920];
                int read;

                using (await DownloadLimiter.AcquireAsync(cts.Token).ConfigureAwait(false))
                {
                    while ((read = await input.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
                    {
                        await DownloadLimiter.ThrottleAsync(read, cts.Token).ConfigureAwait(false);
                        await output.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
                        received += read;
                        if (total > 0) progress?.Report(received * 100.0 / total * 0.95);
                    }
                }

                await output.FlushAsync(cts.Token).ConfigureAwait(false);
            }

            // Verificación del instalador (mismo fail-closed que el zip).
            if (!string.IsNullOrWhiteSpace(_setupSha256))
            {
                await using var fileStream = File.OpenRead(partialPath);
                var actual = Convert.ToHexString(
                    await SHA256.HashDataAsync(fileStream, cts.Token).ConfigureAwait(false));
                if (!string.Equals(actual, _setupSha256, StringComparison.OrdinalIgnoreCase))
                {
                    LastError = "El instalador está corrupto (hash incorrecto)";
                    _log.Warning("DownloadSetup: hash del setup no coincide");
                    return null;
                }

                _log.Info("Hash del instalador verificado");
            }

            // Solo un setup completo y verificado llega con su nombre final
            // (un .partial truncado no debería ejecutarse a mano).
            File.Move(partialPath, destPath, overwrite: true);
            partialPath = null;

            progress?.Report(100);
            _log.Info($"Instalador descargado: {destPath}");
            return destPath;
        }
        catch (OperationCanceledException)
        {
            LastError = cancellationToken.IsCancellationRequested
                ? "Descarga cancelada"
                : "Tiempo agotado al descargar el instalador";
            return null;
        }
        catch (Exception ex)
        {
            LastError = "No se pudo descargar el instalador";
            _log.Error("DownloadSetup failed", ex);
            return null;
        }
        finally
        {
            // Jamás dejar un instalador truncado con nombre de ejecutable.
            if (partialPath is not null)
            {
                try { File.Delete(partialPath); } catch { /* best effort */ }
            }
        }
    }

    // ---------------------------------------------------------------
    //  3. Aplicar: extraer y reiniciar.
    //  - Carpeta escribible (instalación por usuario): reemplazo
    //    silencioso con script oculto + reinicio automático. Sin UAC,
    //    sin consola visible.
    //  - Carpeta NO escribible (instalación vieja en Program Files):
    //    se descarga el instalador completo y se ejecuta visible
    //    (Inno pide el único UAC y migra a la carpeta por usuario).
    // ---------------------------------------------------------------
    public async Task<bool> ApplyAndRestartAsync(
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            LastError = "Ya hay una operación de actualización en curso";
            return false;
        }

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

            // Extracción (decenas de MB) en threadpool: en la UI congelaba
            // la ventana varios segundos.
            await Task.Run(() =>
            {
                if (Directory.Exists(filesDir))
                {
                    Directory.Delete(filesDir, true);
                }

                ZipFile.ExtractToDirectory(zipPath, filesDir);
            }, cancellationToken).ConfigureAwait(false);

            // Solo el .exe exacto en la RAÍZ del zip (el fallback "*.exe"
            // podía reemplazar MineMount.exe con cualquier otro binario).
            var newExe = Path.Combine(filesDir, "MineMount.exe");
            if (!File.Exists(newExe))
            {
                LastError = "El paquete no contiene MineMount.exe";
                _log.Warning("Apply: extracción sin MineMount.exe en la raíz");
                return false;
            }

            var appExe = Environment.ProcessPath
                         ?? Path.Combine(AppContext.BaseDirectory, "MineMount.exe");

            // En desarrollo (dotnet run / UiClickTest) no se auto-reemplaza:
            // ProcessPath no es MineMount.exe y "actualizar" destruiría el
            // proceso equivocado.
            if (!string.Equals(Path.GetFileName(appExe), "MineMount.exe", StringComparison.OrdinalIgnoreCase))
            {
                LastError = "La actualización automática solo está disponible en la aplicación instalada";
                _log.Warning($"Apply: proceso no desplegado ({appExe})");
                return false;
            }

            var appDir = (Path.GetDirectoryName(appExe) ?? string.Empty)
                .TrimEnd(Path.DirectorySeparatorChar);
            var pid = Environment.ProcessId;

            if (!IsDirectoryWritable(appDir))
            {
                // Instalación legacy en Program Files: delegar en el
                // instalador (un solo UAC familiar, sin CMD elevado).
                _log.Info("Carpeta no escribible: actualizando vía instalador completo");
                RunOnUiThread(() =>
                {
                    _notifications.NotifyInfo(
                        Loc.T("S.Update.NeedPerm"),
                        Loc.T("S.Update.NeedPermDesc"));
                });
                var setupPath = await DownloadSetupCoreAsync(progress, cancellationToken).ConfigureAwait(false);
                if (setupPath is null)
                {
                    return false;
                }

                RegisterApplyAttempt(LatestVersion);
                Process.Start(new ProcessStartInfo(setupPath) { UseShellExecute = true });
                RunOnUiThread(() => Application.Current?.Shutdown());
                return true;
            }

            var scriptPath = Path.Combine(StageDir, "apply.cmd");
            var script = BuildApplyScript(pid, newExe, appDir, StageDir);
            await File.WriteAllTextAsync(scriptPath, script, cancellationToken).ConfigureAwait(false);

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
            _log.Info($"Actualización {LatestVersion} aplicándose en segundo plano; reiniciando...");

            RunOnUiThread(() => Application.Current?.Shutdown());
            return true;
        }
        catch (InvalidDataException)
        {
            LastError = "El paquete está corrupto";
            _log.Warning("Apply: el ZIP no se pudo extraer");
            return false;
        }
        catch (OperationCanceledException)
        {
            LastError = "Actualización cancelada";
            _log.Warning("Apply: cancelado");
            return false;
        }
        catch (Exception ex)
        {
            LastError = "No se pudo aplicar la actualización";
            _log.Error("Apply failed", ex);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Ejecuta en el hilo del Dispatcher si existe (Shutdown/Notify son UI).</summary>
    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is { HasShutdownStarted: false, HasShutdownFinished: false })
        {
            if (dispatcher.CheckAccess()) action();
            else dispatcher.Invoke(action);
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
    {
        // Acepta tags como "v1.1.16", "1.1.16b", "1.1.16-beta": conserva solo
        // la parte numérica inicial para que Version.TryParse no falle.
        var t = tag.Trim().TrimStart('v', 'V');
        var m = System.Text.RegularExpressions.Regex.Match(t, @"^\d+(\.\d+){0,3}");
        return m.Success ? m.Value : t;
    }

    private static bool IsNewer(string candidate, string current)
    {
        if (!Version.TryParse(NormalizeVersion(candidate), out var c)) return false;
        if (!Version.TryParse(NormalizeVersion(current), out var cur)) return false;
        if (c != cur) return c > cur;
        // Misma base numérica (p.ej. 1.1.16 vs 1.1.16b): la letra manda.
        return SuffixRank(candidate) > SuffixRank(current);
    }

    /// <summary>Rango del sufijo de letra (1.1.16 → 0, 1.1.16a → 1, 1.1.16b → 2...).</summary>
    private static int SuffixRank(string tag)
    {
        var t = tag.Trim().TrimStart('v', 'V');
        var m = System.Text.RegularExpressions.Regex.Match(t, @"^\d+(\.\d+){0,3}([a-zA-Z])");
        if (!m.Success) return 0;
        return char.ToLowerInvariant(m.Groups[2].Value[0]) - 'a' + 1;
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

    /// <summary>
    /// Si el estado apunta a una versión que ya es la actual (update
    /// aplicado con éxito), se borra: resetea el contador de intentos y
    /// permite re-aplicar si se re-publica el mismo tag.
    /// </summary>
    private void TryClearCompletedState()
    {
        try
        {
            if (!File.Exists(StatePath)) return;

            using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
            var target = doc.RootElement.TryGetProperty("Target", out var t) ? t.GetString() : null;

            if (!string.IsNullOrWhiteSpace(target) && !IsNewer(target, CurrentVersion))
            {
                File.Delete(StatePath);
                _log.Info($"Estado de actualización limpiado (target {target} alcanzado)");
            }
        }
        catch
        {
            // Estado ilegible: no bloquea el arranque.
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

            // Escritura atómica: un corte a mitad de escritura no deja
            // JSON inválido (eso desactivaba en silencio el límite).
            var tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, state);
            File.Move(tmp, StatePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.Warning($"No se pudo registrar el intento de actualización: {ex.Message}");
        }
    }

    private static string BuildApplyScript(int pid, string newExe, string appDir, string stageDir)
    {
        // Script updater, siempre oculto (nunca muestra consola, nunca pide
        // permisos: solo se usa cuando la carpeta ya se verificó escribible):
        //  1. Espera a que MineMount.exe (pid) termine
        //  2. Copia con reintentos (antivirus/locks) hasta 30 s
        //  3. Reinicia MineMount
        // El staging lo limpia TempCleanupService en el próximo inicio
        // (con protección mientras la actualización está fresca); así se
        // evita PowerShell y los problemas con apóstrofos en la ruta.
        var lines = new[]
        {
            "@echo off",
            "setlocal",
            $"set \"PID={pid}\"",
            $"set \"NEW={newExe}\"",
            $"set \"APPDIR={appDir}\"",
            ":wait",
            "tasklist /FI \"PID eq %PID%\" 2>nul | findstr /R /C:\"^ *%PID% \" >nul",
            "if not errorlevel 1 (",
            "  timeout /t 1 /nobreak >nul",
            "  goto wait",
            ")",
            "set \"TRIES=0\"",
            ":copyretry",
            "copy /Y \"%NEW%\" \"%APPDIR%\\MineMount.exe\" >nul 2>&1",
            "if not errorlevel 1 goto started",
            "set /a TRIES+=1",
            "if %TRIES% GEQ 30 exit /b 1",
            "timeout /t 1 /nobreak >nul",
            "goto copyretry",
            ":started",
            "start \"\" \"%APPDIR%\\MineMount.exe\""
        };

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
