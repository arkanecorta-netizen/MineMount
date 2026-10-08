using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MineMount.Services;

/// <summary>
/// Descarga automática de Java (Temurin/Adoptium) cuando no hay un Java
/// compatible en la PC. JRE Windows x64 del major pedido, extraído a
/// %APPDATA%\MineMount\Java\&lt;major&gt;. Respeta el límite de velocidad.
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
        var targetDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MineMount", "Java", major.ToString());
        var javaw = Path.Combine(targetDir, "bin", "javaw.exe");
        if (File.Exists(javaw)) return javaw;

        var url = $"https://api.adoptium.net/v3/binary/latest/{major}/ga/windows/x64/jre/hotspot/normal/eclipse";
        var tmpZip = Path.Combine(Path.GetTempPath(), $"MineMount", $"jre-{major}.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(tmpZip)!);

        try
        {
            _logService.Info($"Descargando Java {major} desde Adoptium...");
            using (await DownloadLimiter.AcquireAsync(ct))
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMinutes(20));
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                resp.EnsureSuccessStatusCode();

                var total = resp.Content.Headers.ContentLength ?? -1;
                long received = 0;
                await using var source = await resp.Content.ReadAsStreamAsync(cts.Token);
                await using var target = File.Create(tmpZip);
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, cts.Token)) > 0)
                {
                    await DownloadLimiter.ThrottleAsync(read, cts.Token);
                    await target.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                    received += read;
                    if (total > 0) progress?.Report(received * 100.0 / total);
                }
            }

            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true);
            ZipFile.ExtractToDirectory(tmpZip, targetDir);

            // El zip trae una subcarpeta (jdk-XX): aplanar un nivel si hace falta
            if (!File.Exists(javaw))
            {
                var subdirs = Directory.GetDirectories(targetDir);
                if (subdirs.Length == 1)
                {
                    var inner = subdirs[0];
                    var tmp = targetDir + "_new";
                    Directory.Move(inner, tmp);
                    Directory.Delete(targetDir, recursive: true);
                    Directory.Move(tmp, targetDir);
                }
            }

            javaw = Path.Combine(targetDir, "bin", "javaw.exe");
            if (File.Exists(javaw))
            {
                _logService.Info($"Java {major} instalado en {targetDir}");
                progress?.Report(100);
                return javaw;
            }

            _logService.Warning("El paquete de Java no trajo javaw.exe");
            return null;
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
            }
            catch
            {
                // Temporal huérfano: lo limpia el servicio de temporales
            }
        }
    }
}
