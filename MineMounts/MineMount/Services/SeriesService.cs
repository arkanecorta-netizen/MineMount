using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MineMount.Models;

namespace MineMount.Services;

public interface ISeriesService
{
    Task<List<SeriesInfo>> GetSeriesAsync();
    Task<SeriesInfo?> GetSeriesAsync(string id);
    Task<string> GetInstallRootAsync();
}

// Fachada de lectura: catálogo remoto + estado de instalación local.
// Las operaciones (instalar/actualizar/reparar) viven en ISeriesInstallService.
public class SeriesService : ISeriesService
{
    private readonly ISeriesCatalogService _catalogService;
    private readonly ISeriesStorageService _storageService;
    private readonly ILogService _logService;

    public SeriesService(
        ISeriesCatalogService catalogService,
        ISeriesStorageService storageService,
        ILogService logService)
    {
        _catalogService = catalogService;
        _storageService = storageService;
        _logService = logService;
    }

    public async Task<string> GetInstallRootAsync() => await _storageService.GetInstallRootAsync();

    public async Task<List<SeriesInfo>> GetSeriesAsync()
    {
        var catalog = await _catalogService.GetCatalogAsync();
        var root = await _storageService.GetInstallRootAsync();

        var list = new List<SeriesInfo>();
        foreach (var definition in catalog)
        {
            list.Add(await BuildInfoAsync(definition, root));
        }

        return list;
    }

    public async Task<SeriesInfo?> GetSeriesAsync(string id)
    {
        var definition = await _catalogService.GetDefinitionAsync(id);
        if (definition == null) return null;

        var root = await _storageService.GetInstallRootAsync();
        return await BuildInfoAsync(definition, root);
    }

    private async Task<SeriesInfo> BuildInfoAsync(SeriesDefinition definition, string installRoot)
    {
        var seriesDir = System.IO.Path.Combine(installRoot, definition.Id);
        var installation = await _storageService.ReadInstallationAsync(seriesDir);

        var status = await _storageService.ComputeStatusAsync(definition, seriesDir);

        return new SeriesInfo
        {
            Definition = definition,
            InstalledVersion = installation?.Version ?? string.Empty,
            InstallPath = installation?.Path ?? seriesDir,
            Status = status
        };
    }
}
