using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;

namespace MineMount.Services;

/// <summary>
/// Click suave opcional en botones. Respeta el ajuste "Habilitar sonidos".
/// Usa el wav local (offline) y nunca rompe la app si falla el audio.
/// </summary>
public interface ISoundService
{
    void PlayClick();
    Task RefreshAsync();
}

public class SoundService : ISoundService
{
    private readonly ISettingsService _settingsService;
    private readonly ILogService _logService;
    private MediaPlayer? _player;
    private bool _enabled = true;
    private bool _loaded;

    public SoundService(ISettingsService settingsService, ILogService logService)
    {
        _settingsService = settingsService;
        _logService = logService;
        _settingsService.SettingsChanged += (_, _) => _ = RefreshAsync();
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            _enabled = settings.EnableSounds;
            if (_player != null)
                _player.Volume = Math.Clamp(settings.ClickVolume, 0, 100) / 100.0 * 0.5;
        }
        catch
        {
            _enabled = true;
        }
    }

    public void PlayClick()
    {
        if (!_enabled) return;

        try
        {
            _player ??= new MediaPlayer { Volume = 0.15 };

            if (!_loaded)
            {
                var uri = new Uri("pack://application:,,,/Assets/Sounds/click.wav", UriKind.Absolute);
                _player.Open(uri);
                _loaded = true;
            }

            _player.Position = TimeSpan.Zero;
            _player.Play();
        }
        catch (Exception ex)
        {
            // Audio es decorativo: se silencia el error para no spamear el log.
            _logService.Warning($"Sonido no disponible: {ex.Message}");
            _enabled = false;
        }
    }
}
