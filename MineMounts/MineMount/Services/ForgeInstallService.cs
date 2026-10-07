using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MineMount.Services;

public interface IForgeInstallService
{
    Task<bool> IsForgeInstalledAsync(string mcVersion);
    Task InstallForgeAsync(string mcVersion, IProgress<double>? progress, CancellationToken cancellationToken);
    Task<string> GetForgeJarPathAsync(string mcVersion);
}

// Instalación de Forge: descarga el universal jar desde el maven de Forge.
// Forge 1.20.1 usa launchwrapper (net.minecraft.launchwrapper.Launch)
// con el tweak class FMLTweaker, incluido en el universal jar.
public class ForgeInstallService : IForgeInstallService
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly ILogService _logService;
    private readonly string _forgeDir;

    // Forge 1.20.1 → 47.2.0 (estable, ampliamente usada)
    private const string ForgeVersion = "47.2.0";

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
        return Task.FromResult(File.Exists(jar));
    }

    public async Task InstallForgeAsync(string mcVersion, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var jar = GetForgeJarPath(mcVersion);
        if (File.Exists(jar))
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
                    await target.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                    received += read;

                    if (total > 0)
                    {
                        progress?.Report(10 + (received * 90.0 / total));
                    }
                }

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

    public Task<string> GetForgeJarPathAsync(string mcVersion)
        => Task.FromResult(GetForgeJarPath(mcVersion));

    private string GetForgeJarPath(string mcVersion)
        => Path.Combine(_forgeDir, $"forge-{mcVersion}-{ForgeVersion}-universal.jar");
}
