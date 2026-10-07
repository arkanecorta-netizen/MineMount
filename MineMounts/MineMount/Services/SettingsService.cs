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
                _settings = JsonSerializer.Deserialize<Models.LauncherSettings>(json) ?? new();
            }
            catch
            {
                _settings = new Models.LauncherSettings();
            }
        }
    }

    public Task<Models.LauncherSettings> GetSettingsAsync()
    {
        return Task.FromResult(_settings);
    }

    public async Task SaveSettingsAsync(Models.LauncherSettings settings)
    {
        _settings = settings;
        var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_settingsPath, json);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }
}