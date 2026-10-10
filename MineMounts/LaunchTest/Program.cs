using System;
using System.Threading.Tasks;
using MineMount.Services;

// Arnés headless que reproduce el click de JUGAR:
// mismos servicios reales + GameLauncherService.LaunchAsync.
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var seriesId = args.Length > 0 ? args[0] : "NSE6";

        var log = new LogService();
        var settings = new SettingsService();
        await settings.LoadAsync();
        var auth = new AuthService(settings, log);
        var catalog = new SeriesCatalogService(log);
        var storage = new SeriesStorageService(settings, log);
        var series = new SeriesService(catalog, storage, log);
        var notifications = new NotificationService();
        var mcInstall = new MinecraftInstallService(log, settings);
        var forgeInstall = new ForgeInstallService(log);
        var launcher = new GameLauncherService(series, storage, settings, log, notifications, mcInstall, forgeInstall, auth);

        Console.WriteLine($"=== Series disponibles ===");
        try
        {
            foreach (var s in await series.GetSeriesAsync())
            {
                Console.WriteLine($"id={s.Id} name={s.Name} status={s.Status} mc={s.Definition?.MinecraftVersion} loader={s.Definition?.Loader} path={s.InstallPath}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR listando series: {ex.Message}");
        }

        Console.WriteLine($"=== VerifyInstallation({seriesId}) ===");
        try
        {
            Console.WriteLine("Verify: " + await launcher.VerifyInstallationAsync(seriesId));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR verify: {ex}");
        }

        Console.WriteLine($"=== LaunchAsync({seriesId}) ===");
        var progress = new Progress<double>(p => Console.WriteLine($"progress: {p:F1}%"));
        try
        {
            var result = await launcher.LaunchAsync(seriesId, progress);
            Console.WriteLine($"Success={result.Success} Message={result.Message} PID={result.Process?.Id}");
            if (result.Success && result.Process != null)
            {
                Console.WriteLine("Esperando salida del juego (máx 15 min)...");
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                try { await result.Process.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException) { Console.WriteLine("TIMEOUT: el juego sigue corriendo, se deja en marcha."); }
                Console.WriteLine($"HasExited={result.Process.HasExited} ExitCode={(result.Process.HasExited ? result.Process.ExitCode.ToString() : "n/a")}");
            }
            return result.Success ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"EXCEPCION: {ex}");
            return 1;
        }
    }
}
