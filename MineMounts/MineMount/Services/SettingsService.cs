using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace MineMount.Services;

public interface ISettingsService
{
    Task<Models.LauncherSettings> GetSettingsAsync();
    Task SaveSettingsAsync(Models.LauncherSettings settings);
    Task LoadAsync();
    event EventHandler? SettingsChanged;
}

public class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private readonly string _settingsPath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ILogService? _log;
    private Models.LauncherSettings _settings = new();

    public event EventHandler? SettingsChanged;

    public SettingsService() : this(null)
    {
    }

    public SettingsService(ILogService? log)
    {
        _log = log;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var mineMountDir = Path.Combine(appData, "MineMount");
        Directory.CreateDirectory(mineMountDir);
        _settingsPath = Path.Combine(mineMountDir, "settings.json");
    }

    public async Task LoadAsync()
    {
        if (File.Exists(_settingsPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(_settingsPath);
                _settings = JsonSerializer.Deserialize<Models.LauncherSettings>(json, JsonOptions) ?? new();
            }
            catch (Exception ex)
            {
                // Preservar el archivo ilegible para diagnóstica en vez de
                // destruirlo en silencio (y avisar en el log).
                try
                {
                    var backup = $"{_settingsPath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
                    File.Move(_settingsPath, backup, overwrite: true);
                    _log?.Warning($"settings.json ilegible ({ex.Message}); respaldado en {backup}");
                }
                catch
                {
                    _log?.Warning($"settings.json ilegible y no se pudo respaldar: {ex.Message}");
                }

                _settings = new Models.LauncherSettings();
            }
        }

        ApplyDownloadLimits();
    }

    public Task<Models.LauncherSettings> GetSettingsAsync()
    {
        // Instancia compartida a propósito: los llamadores mutan y luego
        // guardan con SaveSettingsAsync (que serializa bajo el lock).
        return Task.FromResult(_settings);
    }

    public async Task SaveSettingsAsync(Models.LauncherSettings settings)
    {
        // Lock + tmp+rename: escrituras concurrentes (autosave, geometría
        // de ventana, login) ya no se pisan ni dejan JSON truncado si la
        // app muere a mitad de escritura.
        await _writeLock.WaitAsync();
        try
        {
            _settings = settings;
            var json = JsonSerializer.Serialize(_settings, JsonOptions);
            var tmp = _settingsPath + ".tmp";
            await File.WriteAllTextAsync(tmp, json);
            File.Move(tmp, _settingsPath, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }

        ApplyDownloadLimits();

        // El evento puede subirse desde un hilo de fondo (guardado de
        // geometría): los suscriptores (SoundService toca DispatcherObject)
        // deben recibirlo en el hilo UI.
        var handler = SettingsChanged;
        if (handler is null) return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is { HasShutdownStarted: false, HasShutdownFinished: false }
            && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(() => handler(this, EventArgs.Empty));
        }
        else
        {
            handler(this, EventArgs.Empty);
        }
    }

    private void ApplyDownloadLimits()
    {
        try
        {
            DownloadLimiter.Configure(
                _settings.MaxConcurrentDownloads,
                (long)_settings.DownloadSpeedLimitKBps * 1024);
        }
        catch
        {
            // Límites inválidos: se ignoran sin romper el guardado
        }
    }
}
