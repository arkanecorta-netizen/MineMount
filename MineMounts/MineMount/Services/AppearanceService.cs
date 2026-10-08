using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace MineMount.Services;

/// <summary>
/// Tema claro/oscuro + idioma ES/EN aplicados en vivo: intercambia los
/// diccionarios de recursos (todos los pinceles y textos usan
/// DynamicResource, así la UI se actualiza sin reiniciar).
/// </summary>
public interface IAppearanceService
{
    event EventHandler? LanguageChanged;
    event EventHandler? ThemeChanged;
    Task ApplyAsync();
    Task SetThemeAsync(string theme);
    Task SetLanguageAsync(string language);
    void RaiseLanguageChanged();
}

public class AppearanceService : IAppearanceService
{
    private readonly ISettingsService _settingsService;
    private readonly ILogService _logService;

    public event EventHandler? LanguageChanged;
    public event EventHandler? ThemeChanged;

    public AppearanceService(ISettingsService settingsService, ILogService logService)
    {
        _settingsService = settingsService;
        _logService = logService;
    }

    public async Task ApplyAsync()
    {
        var s = await _settingsService.GetSettingsAsync();
        ApplyTheme(s.Theme);
        ApplyLanguage(s.Language);
    }

    public async Task SetThemeAsync(string theme)
    {
        // Solo modo oscuro: el claro se eliminó por experiencia.
        var s = await _settingsService.GetSettingsAsync();
        s.Theme = "Dark";
        await _settingsService.SaveSettingsAsync(s);
        ApplyTheme("Dark");
    }

    public async Task SetLanguageAsync(string language)
    {
        var s = await _settingsService.GetSettingsAsync();
        s.Language = language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en-US" : "es-ES";
        await _settingsService.SaveSettingsAsync(s);
        ApplyLanguage(s.Language);
    }

    public void RaiseLanguageChanged() => LanguageChanged?.Invoke(this, EventArgs.Empty);

    private void ApplyTheme(string theme)
    {
        try
        {
            // Solo oscuro: asegura que el diccionario claro no quede cargado.
            // Se crea primero y se reemplaza después: si algo falla, la UI
            // nunca queda sin diccionario (pantalla en blanco).
            var fresh = new ResourceDictionary
            {
                Source = new Uri("Styles/Colors.xaml", UriKind.Relative)
            };

            var dicts = Application.Current.Resources.MergedDictionaries;
            var light = dicts.FirstOrDefault(d =>
                d.Source != null && d.Source.OriginalString.Contains("Colors.Light"));
            if (light != null) dicts.Remove(light);

            var dark = dicts.FirstOrDefault(d =>
                d.Source != null && d.Source.OriginalString.EndsWith("Styles/Colors.xaml"));
            if (dark != null) dicts.Remove(dark);
            dicts.Insert(0, fresh);

            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logService.Warning($"No se pudo aplicar el tema: {ex.Message}");
        }
    }

    private void ApplyLanguage(string language)
    {
        try
        {
            var isEnglish = language.StartsWith("en", StringComparison.OrdinalIgnoreCase);
            var want = isEnglish ? "Strings/Strings.en.xaml" : "Strings/Strings.es.xaml";

            // Crear primero: si falla, se conserva el idioma actual.
            var fresh = new ResourceDictionary { Source = new Uri(want, UriKind.Relative) };

            var dicts = Application.Current.Resources.MergedDictionaries;
            var current = dicts.FirstOrDefault(d =>
                d.Source != null && d.Source.OriginalString.Contains("Strings/Strings."));
            if (current != null) dicts.Remove(current);

            dicts.Add(fresh);
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logService.Warning($"No se pudo aplicar el idioma: {ex.Message}");
        }
    }
}
