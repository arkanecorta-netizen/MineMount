using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MineMount.Services;

public interface IMinecraftService
{
    Task<List<Models.MinecraftVersion>> GetVersionsAsync();
    Task InstallVersionAsync(Models.MinecraftVersion version, IProgress<double>? progress = null);
    Task<bool> IsVersionInstalledAsync(string versionId);
}

// Página Juegos: delega en el instalador REAL de Minecraft. Antes era una
// lista hardcodeada con "instalación" simulada (Task.Delay) que decía
// "instalado" sin instalar nada.
public class MinecraftService : IMinecraftService
{
    private readonly IMinecraftInstallService _install;
    private readonly ILogService _log;

    public MinecraftService(IMinecraftInstallService install, ILogService log)
    {
        _install = install;
        _log = log;
    }

    public Task<List<Models.MinecraftVersion>> GetVersionsAsync()
        => _install.GetAvailableVersionsAsync();

    public async Task InstallVersionAsync(Models.MinecraftVersion version, IProgress<double>? progress = null)
    {
        _log.Info($"Instalación de Minecraft {version.Id} desde la página Juegos");
        await _install.InstallVersionAsync(version.Id, progress, CancellationToken.None);
    }

    public Task<bool> IsVersionInstalledAsync(string versionId)
        => _install.IsVersionInstalledAsync(versionId);
}
