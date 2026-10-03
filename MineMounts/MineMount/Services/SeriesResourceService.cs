using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MineMount.Models;

namespace MineMount.Services;

public interface ISeriesResourceService
{
    Task<SeriesPackageInfo?> ResolvePackageAsync(SeriesDefinition definition);
    Task DownloadAsync(SeriesPackageInfo package, string destPath, IProgress<SeriesProgress>? progress, CancellationToken cancellationToken);
}

public class SeriesResourceService : ISeriesResourceService
{
    private const string RepoOwner = "arkanecorta-netizen";
    private const string RepoName = "MineMount";

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogService _logService;

    public SeriesResourceService(ILogService logService)
    {
        _logService = logService;
    }

    // Override para pruebas: MINEMOUNT_SERIES_API=http://127.0.0.1:port
    private static string ApiBase =>
        Environment.GetEnvironmentVariable("MINEMOUNT_SERIES_API")?.TrimEnd('/')
        ?? "https://api.github.com";

    public static string BuildTag(SeriesDefinition definition)
    {
        if (!SeriesValidation.IsValidSeriesId(definition.Id)
            || !SeriesValidation.IsValidVersion(definition.Version))
        {
            return string.Empty;
        }

        var tag = definition.Resources?.Tag;
        if (SeriesValidation.IsValidReleaseTag(tag)) return tag!;

        return $"{definition.Id.ToLowerInvariant()}-v{definition.Version}";
    }

    public static string BuildAssetName(SeriesDefinition definition)
    {
        if (!SeriesValidation.IsValidSeriesId(definition.Id))
        {
            return string.Empty;
        }

        var asset = definition.Resources?.Asset;
        if (SeriesValidation.IsValidAssetName(asset)) return asset!;

        return $"{definition.Id}-Resources.zip";
    }

    // ---------------------------------------------------------------
    //  Resuelve el release {tag} y localiza el asset del paquete
    // ---------------------------------------------------------------
    public async Task<SeriesPackageInfo?> ResolvePackageAsync(SeriesDefinition definition)
    {
        var tag = BuildTag(definition);
        var assetName = BuildAssetName(definition);
        if (string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(assetName))
        {
            _logService.Warning($"Definición de serie inválida para {definition.Id}");
            return null;
        }

        var url = $"{ApiBase}/repos/{RepoOwner}/{RepoName}/releases/tags/{tag}";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("MineMount-Series/1.0");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var resp = await Http.SendAsync(req, cts.Token);

            if (!resp.IsSuccessStatusCode)
            {
                _logService.Warning($"Release de serie {tag}: HTTP {(int)resp.StatusCode}");
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(cts.Token);
            var release = JsonSerializer.Deserialize<GitHubRelease>(json, JsonOptions);
            var asset = release?.Assets?.FirstOrDefault(a =>
                string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase));

            if (asset == null)
            {
                _logService.Warning($"Release {tag} no contiene el asset {assetName}");
                return null;
            }

            if (string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
            {
                _logService.Warning($"Release {tag}: asset {assetName} sin URL de descarga");
                return null;
            }

            return new SeriesPackageInfo
            {
                DownloadUrl = asset.BrowserDownloadUrl,
                Size = asset.Size,
                Sha256 = ExtractSha256(asset.Digest)
            };
        }
        catch (Exception ex)
        {
            _logService.Error($"No se pudo resolver el paquete de {definition.Id} ({tag})", ex);
            return null;
        }
    }

    private static string ExtractSha256(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return string.Empty;
        var d = digest.Trim();
        if (d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            d = d["sha256:".Length..];
        }
        return d.Replace("-", string.Empty).ToUpperInvariant();
    }

    // ---------------------------------------------------------------
    //  Descarga con progreso real (bytes, %, velocidad)
    // ---------------------------------------------------------------
    public async Task DownloadAsync(
        SeriesPackageInfo package,
        string destPath,
        IProgress<SeriesProgress>? progress,
        CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tempPath = destPath + ".tmp";
        if (File.Exists(tempPath)) File.Delete(tempPath);

        long totalLength = package.Size;
        long totalRead = 0;
        var startedAt = DateTime.UtcNow;
        var lastReport = DateTime.MinValue;

        void Report(string message)
        {
            if (DateTime.UtcNow - lastReport < TimeSpan.FromMilliseconds(200)) return;
            lastReport = DateTime.UtcNow;

            var elapsed = (DateTime.UtcNow - startedAt).TotalSeconds;
            var percent = totalLength > 0
                ? Math.Min(60, Math.Round(totalRead * 60.0 / totalLength, 1))
                : 0;

            progress?.Report(new SeriesProgress
            {
                StepIndex = 1,
                StepCount = 3,
                StepName = "Descargando",
                Percent = percent,
                Message = message,
                BytesReceived = totalRead,
                TotalBytes = totalLength,
                SpeedBps = elapsed > 0.5 ? totalRead / elapsed : 0
            });
        }

        if (!Uri.TryCreate(package.DownloadUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeFile
                && uri.Scheme != Uri.UriSchemeHttp
                && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("URL de descarga no compatible");
        }

        // En producción el paquete SIEMPRE viene de GitHub (HTTPS).
        // HTTP y file:// sólo se permiten en modo de pruebas
        // (MINEMOUNT_SERIES_API apunta a un servidor local).
        var testMode = !string.Equals(ApiBase, "https://api.github.com", StringComparison.OrdinalIgnoreCase);
        if (!testMode && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("La URL del paquete debe ser HTTPS");
        }

        // Timeout global de descarga: sin esto una conexión truncada
        // cuelga la operación indefinidamente (solo cancelable por el usuario)
        using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        downloadCts.CancelAfter(TimeSpan.FromMinutes(20));

        if (uri.IsFile)
        {
            await using var source = File.OpenRead(uri.LocalPath);
            await using var target = File.Create(tempPath);
            totalLength = source.Length;
            await CopyStreamAsync(source, target, downloadCts.Token, read =>
            {
                totalRead = read;
                Report("Descargando...");
            });
        }
        else
        {
            using var resp = await Http.GetAsync(package.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, downloadCts.Token);
            resp.EnsureSuccessStatusCode();

            if (resp.Content.Headers.ContentLength is long len && len > 0)
            {
                totalLength = len;
            }

            await using var source = await resp.Content.ReadAsStreamAsync(downloadCts.Token);
            await using var target = File.Create(tempPath);
            await CopyStreamAsync(source, target, downloadCts.Token, read =>
            {
                totalRead = read;
                Report("Descargando...");
            });
        }

        var finalLength = new FileInfo(tempPath).Length;
        if (totalLength > 0 && finalLength != totalLength)
        {
            throw new IOException($"La descarga está incompleta ({finalLength}/{totalLength} bytes)");
        }

        if (!string.IsNullOrWhiteSpace(package.Sha256))
        {
            Report("Verificando hash...");
            var actual = SeriesStorageService.ComputeSha256(tempPath);
            if (!string.Equals(actual, package.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tempPath);
                throw new InvalidDataException("El hash SHA256 del paquete no coincide");
            }
        }

        File.Move(tempPath, destPath, overwrite: true);
        Report("Descarga completada");
    }

    private static async Task CopyStreamAsync(
        Stream source,
        Stream target,
        CancellationToken cancellationToken,
        Action<long> onProgress)
    {
        var buffer = new byte[81920];
        long totalRead = 0;

        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            totalRead += read;
            onProgress(totalRead);
        }

        onProgress(totalRead);
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("assets")]
        public GitHubAsset[] Assets { get; set; } = Array.Empty<GitHubAsset>();
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;

        [JsonPropertyName("digest")]
        public string? Digest { get; set; }
    }
}
