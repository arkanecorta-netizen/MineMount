using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MineMount.Services;

/// <summary>
/// Descarga automática de Java (Temurin/Adoptium) cuando no hay un Java
/// compatible en la PC. JRE Windows x64 del major pedido, extraído a
/// %APPDATA%\MineMount\Java\&lt;major&gt;. Verifica el SHA-256 oficial antes
/// de extraer y publica de forma atómica (staging → rename).
/// </summary>
public interface IJavaDownloaderService
{
    Task<string?> DownloadJavaAsync(int major, IProgress<double>? progress, CancellationToken ct);
}

public class JavaDownloaderService : IJavaDownloaderService
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly ILogService _logService;

    public JavaDownloaderService(ILogService logService)
    {
        _logService = logService;
    }

    public async Task<string?> DownloadJavaAsync(int major, IProgress<double>? progress, CancellationToken ct)
    {
        var javaRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MineMount", "Java");
        var targetDir = Path.Combine(javaRoot, major.ToString());
        var javaw = Path.Combine(targetDir, "bin", "javaw.exe");
        if (File.Exists(javaw)) return javaw;

        var tmpZip = Path.Combine(Path.GetTempPath(), "MineMount", $"jre-{major}.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(tmpZip)!);
        var staging = Path.Combine(javaRoot, $"staging-{major}-{Guid.NewGuid():N}");

        try
        {
            // 1. URL directa + SHA-256 oficial de Adoptium (fail-closed:
            //    sin checksum no se ejecuta un JRE descargado).
            var packageInfo = await GetPackageInfoAsync(major, ct).ConfigureAwait(false);
            if (packageInfo is null)
            {
                _logService.Warning($"Adoptium no devolvió paquete/checksum para Java {major}");
                return null;
            }

            // 2. Descarga del zip
            _logService.Info($"Descargando Java {major} desde Adoptium...");
            using (await DownloadLimiter.AcquireAsync(ct).ConfigureAwait(false))
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMinutes(20));
                using var resp = await Http.GetAsync(
                    packageInfo.Value.Url, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                var total = resp.Content.Headers.ContentLength ?? -1;
                long received = 0;
                await using var source = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                await using var target = File.Create(tmpZip);
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
                {
                    await DownloadLimiter.ThrottleAsync(read, cts.Token).ConfigureAwait(false);
                    await target.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
                    received += read;
                    if (total > 0) progress?.Report(received * 100.0 / total * 0.95);
                }
            }

            // 3. Verificación de integridad (SIEMPRE)
            var actualHash = await ComputeSha256HexAsync(tmpZip, ct).ConfigureAwait(false);
            if (!string.Equals(actualHash, packageInfo.Value.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _logService.Warning(
                    $"Java {major}: checksum no coincide (esperado {packageInfo.Value.Sha256[..16]}..., " +
                    $"actual {actualHash[..16]}...)");
                return null;
            }

            _logService.Info("Checksum de Java verificado");

            // 4. Extraer a staging y publicar con rename: un crash a mitad
            //    de extracción nunca deja targetDir a medias (antes un
            //    javaw.exe parcial se aceptaba como instalado).
            Directory.CreateDirectory(javaRoot);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);

            await Task.Run(() =>
            {
                ZipFile.ExtractToDirectory(tmpZip, staging);

                // El zip trae una subcarpeta (jdk-XX): aplanar un nivel
                var stagedJavaw = Path.Combine(staging, "bin", "javaw.exe");
                if (!File.Exists(stagedJavaw))
                {
                    var subdirs = Directory.GetDirectories(staging);
                    if (subdirs.Length == 1)
                    {
                        var inner = subdirs[0];
                        var flat = staging + "_flat";
                        Directory.Move(inner, flat);
                        Directory.Delete(staging, recursive: true);
                        Directory.Move(flat, staging);
                    }
                }
            }, ct).ConfigureAwait(false);

            javaw = Path.Combine(staging, "bin", "javaw.exe");
            if (!File.Exists(javaw))
            {
                _logService.Warning("El paquete de Java no trajo javaw.exe");
                return null;
            }

            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true);
            Directory.Move(staging, targetDir);

            javaw = Path.Combine(targetDir, "bin", "javaw.exe");
            _logService.Info($"Java {major} instalado en {targetDir}");
            progress?.Report(100);
            return javaw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logService.Error("No se pudo descargar Java", ex);
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(tmpZip)) File.Delete(tmpZip);
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
            catch
            {
                // Temporal huérfano: lo limpia el servicio de temporales
            }
        }
    }

    /// <summary>
    /// Resuelve la URL directa del JRE y su SHA-256 oficial vía la API de
    /// assets de Adoptium. Null si no hay paquete verificable.
    /// </summary>
    private async Task<(string Url, string Sha256)?> GetPackageInfoAsync(int major, CancellationToken ct)
    {
        var url = $"https://api.adoptium.net/v3/assets/latest/{major}/hotspot" +
                  "?architecture=x64&image_type=jre&os=windows&vendor=eclipse";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
        {
            return null;
        }

        foreach (var asset in doc.RootElement.EnumerateArray())
        {
            if (!asset.TryGetProperty("binary", out var binary)) continue;
            if (!binary.TryGetProperty("package", out var package)) continue;

            var link = package.TryGetProperty("link", out var l) ? l.GetString() : null;
            var checksum = package.TryGetProperty("checksum", out var c) ? c.GetString() : null;

            if (Uri.TryCreate(link, UriKind.Absolute, out var uri)
                && !string.IsNullOrWhiteSpace(checksum))
            {
                return (uri.ToString(), checksum);
            }
        }

        return null;
    }

    private static async Task<string> ComputeSha256HexAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}
