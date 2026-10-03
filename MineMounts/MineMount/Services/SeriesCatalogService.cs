using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using MineMount.Models;

namespace MineMount.Services;

public interface ISeriesCatalogService
{
    Task<List<SeriesDefinition>> GetCatalogAsync(bool forceRefresh = false);
    Task<SeriesDefinition?> GetDefinitionAsync(string id, bool forceRefresh = false);
    string CatalogSource { get; }
}

public class SeriesCatalogService : ISeriesCatalogService
{
    private const string CatalogUrl =
        "https://raw.githubusercontent.com/arkanecorta-netizen/MineMount/main/Series/catalog.json";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly SeriesDefinition[] FallbackCatalog =
    {
        new()
        {
            Id = "NSE6",
            Name = "NSE6",
            Version = "1.0.0",
            Description = "La serie principal de MineMount: mods, configuraciones, recursos y archivos necesarios.",
            Available = true,
            Resources = new SeriesResourceRef
            {
                Tag = "nse6-v1.0.0",
                Asset = "NSE6-Resources.zip"
            }
        }
    };

    private readonly ILogService _logService;
    private readonly string _cachePath;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _fetchGate = new(1, 1);

    private List<SeriesDefinition>? _cache;
    private DateTime _cacheAt = DateTime.MinValue;

    public SeriesCatalogService(ILogService logService)
    {
        _logService = logService;

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MineMount");
        Directory.CreateDirectory(dir);
        _cachePath = Path.Combine(dir, "series-catalog.json");
    }

    public string CatalogSource { get; private set; } = "local";

    public async Task<List<SeriesDefinition>> GetCatalogAsync(bool forceRefresh = false)
    {
        lock (_gate)
        {
            if (!forceRefresh && _cache != null && DateTime.UtcNow - _cacheAt < TimeSpan.FromMinutes(10))
            {
                return new List<SeriesDefinition>(_cache);
            }
        }

        // Single-flight: peticiones simultáneas comparten una única descarga
        await _fetchGate.WaitAsync();
        try
        {
            lock (_gate)
            {
                if (!forceRefresh && _cache != null && DateTime.UtcNow - _cacheAt < TimeSpan.FromMinutes(10))
                {
                    return new List<SeriesDefinition>(_cache);
                }
            }

            var catalog = await FetchCatalogAsync(forceRefresh);

            lock (_gate)
            {
                _cache = catalog;
                _cacheAt = DateTime.UtcNow;
            }

            return new List<SeriesDefinition>(catalog);
        }
        finally
        {
            _fetchGate.Release();
        }
    }

    public async Task<SeriesDefinition?> GetDefinitionAsync(string id, bool forceRefresh = false)
    {
        var catalog = await GetCatalogAsync(forceRefresh);
        return catalog.Find(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------
    //  remoto → caché local → fallback embebido
    // ---------------------------------------------------------------
    private async Task<List<SeriesDefinition>> FetchCatalogAsync(bool forceRefresh)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, CatalogUrl);
            req.Headers.UserAgent.ParseAdd("MineMount-Series/1.0");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await Http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                var remote = Parse(json);
                if (remote.Count > 0)
                {
                    await File.WriteAllTextAsync(_cachePath, json);
                    CatalogSource = "remoto";
                    _logService.Info($"Catálogo de series descargado: {remote.Count} entradas");
                    return remote;
                }
            }

            _logService.Warning($"Catálogo remoto no utilizable (HTTP {(int)resp.StatusCode}); se intenta caché local");
        }
        catch (Exception ex)
        {
            _logService.Warning($"No se pudo descargar el catálogo de series: {ex.Message}");
        }

        try
        {
            if (File.Exists(_cachePath))
            {
                var cached = Parse(await File.ReadAllTextAsync(_cachePath));
                if (cached.Count > 0)
                {
                    CatalogSource = "caché";
                    _logService.Info($"Catálogo de series desde caché: {cached.Count} entradas");
                    return cached;
                }
            }
        }
        catch (Exception ex)
        {
            _logService.Warning($"Caché de catálogo ilegible: {ex.Message}");
        }

        CatalogSource = "local";
        _logService.Warning("Catálogo de series usando valores locales de respaldo");
        return new List<SeriesDefinition>(FallbackCatalog);
    }

    private static List<SeriesDefinition> Parse(string json)
    {
        var list = JsonSerializer.Deserialize<List<SeriesDefinition>>(json, JsonOptions) ?? new();

        // El catálogo viene de Internet: descartar entradas con ids
        // inválidos (path traversal) y normalizar tag/asset inseguros
        // para que BuildTag/BuildAssetName usen valores derivados seguros.
        list.RemoveAll(d => !SeriesValidation.IsValidSeriesId(d.Id));

        foreach (var d in list)
        {
            if (!SeriesValidation.IsValidVersion(d.Version)) d.Version = "1.0.0";

            d.Resources ??= new SeriesResourceRef();
            if (!SeriesValidation.IsValidReleaseTag(d.Resources.Tag)) d.Resources.Tag = string.Empty;
            if (!SeriesValidation.IsValidAssetName(d.Resources.Asset)) d.Resources.Asset = string.Empty;
        }

        return list;
    }
}
