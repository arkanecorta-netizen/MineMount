using System;
using System.Collections.Generic;

namespace MineMount.Services;

/// <summary>
/// Catálogo de fondos locales (offline) para el fondo animado a pantalla completa.
/// Las imágenes viven en Assets/Backgrounds y se empaquetan como Resource,
/// con fallback a color sólido (#0B0B0F) si alguna falla.
/// </summary>
public static class BackgroundCatalog
{
    public const int CrossfadeSeconds = 2; // ~1.5s de transición (WPF: 1.5s)
    public const int HoldSeconds = 8;      // 8s por imagen
    public const string FallbackColor = "#0B0B0F";

    public static readonly IReadOnlyList<BackgroundItem> Items = new List<BackgroundItem>
    {
        new("bg1", "pack://application:,,,/Assets/Backgrounds/bg1.png", "Cumbres moradas"),
        new("bg2", "pack://application:,,,/Assets/Backgrounds/bg2.jpg", "Valle al atardecer"),
        new("bg3", "pack://application:,,,/Assets/Backgrounds/bg3.jpg", "Aldea nocturna"),
        new("bg4", "pack://application:,,,/Assets/Backgrounds/bg4.jpg", "Montañas nevadas"),
        new("bg5", "pack://application:,,,/Assets/Backgrounds/bg5.jpg", "Castillo flotante"),
        new("bg6", "pack://application:,,,/Assets/Backgrounds/bg6.jpg", "Cueva luminosa"),
    };

    public static Uri UriAt(int index)
    {
        if (Items.Count == 0)
            return new Uri("pack://application:,,,/Assets/MineMount/banner.png", UriKind.Absolute);
        var safe = ((index % Items.Count) + Items.Count) % Items.Count;
        return new Uri(Items[safe].PackUri, UriKind.Absolute);
    }
}

public sealed record BackgroundItem(string Id, string PackUri, string DisplayName);
