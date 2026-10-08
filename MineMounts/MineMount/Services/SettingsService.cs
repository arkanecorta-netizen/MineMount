using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

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
    private Models.LauncherSettings _settings = new();

    public event EventHandler? SettingsChanged;

    public SettingsService()
    {
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
            catch
            {
                _settings = new Models.LauncherSettings();
            }
        }

        ApplyDownloadLimits();
    }

    public Task<Models.LauncherSettings> GetSettingsAsync()
    {
        return Task.FromResult(_settings);
    }

    public async Task SaveSettingsAsync(Models.LauncherSettings settings)
    {
        _settings = settings;
        var json = JsonSerializer.Serialize(_settings, JsonOptions);
        await File.WriteAllTextAsync(_settingsPath, json);
        ApplyDownloadLimits();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
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