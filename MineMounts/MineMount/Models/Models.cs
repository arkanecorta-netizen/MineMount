using System;
using System.Collections.Generic;

namespace MineMount.Models;

public class LauncherSettings
{
    public int AllocatedRAM { get; set; } = 4096;
    public string JavaPath { get; set; } = string.Empty;
    public string GameDirectory { get; set; } = string.Empty;
    public string SeriesInstallPath { get; set; } = string.Empty;
    public bool AutoUpdate { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public bool ShowNews { get; set; } = true;
    public bool EnableAnimations { get; set; } = true;
    public bool EnableSounds { get; set; } = true;
    public bool MinimizeOnLaunch { get; set; } = false;
    public string NewsFeedUrl { get; set; } = string.Empty;
    public string UserName { get; set; } = "Jugador";
    public string Theme { get; set; } = "Dark";
    public string Language { get; set; } = "es-ES";

    // Fondo animado: "Auto" (rotación), "Fijo" o "Desactivado" (PCs flojas)
    public string BackgroundMode { get; set; } = "Auto";
    public int SelectedBackground { get; set; } = 0;

    // Detalles que le dan vida
    public bool EnableParticles { get; set; } = true;

    // Comunidad (invitación de Discord configurable)
    public string DiscordUrl { get; set; } = string.Empty;

    // Geometría de la ventana (se restaura entre sesiones)
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 760;
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public bool WindowMaximized { get; set; } = false;

    // Cuenta: "None" | "Microsoft" | "Offline"
    public string AccountType { get; set; } = "None";
    public string OfflineUuid { get; set; } = string.Empty;

    // Fondo: intervalo, oscurecido extra y desenfoque
    public int BackgroundIntervalSeconds { get; set; } = 8;
    public int BackgroundDim { get; set; } = 25;
    public int BackgroundBlur { get; set; } = 0;
    public List<string> CustomBackgrounds { get; set; } = new();

    // Sonido de click (0-100)
    public int ClickVolume { get; set; } = 30;

    // Descargas: 0 = sin límite; simultáneas 1-4
    public int DownloadSpeedLimitKBps { get; set; } = 0;
    public int MaxConcurrentDownloads { get; set; } = 2;

    // Lanzamiento
    public string JvmArgs { get; set; } = string.Empty;
    public bool Fullscreen { get; set; } = false;
    public string Resolution { get; set; } = "1280x720";
    public bool CloseOnLaunch { get; set; } = false;

    // Comunidad
    public string DiscordClientId { get; set; } = string.Empty;
}

public class UserInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string AvatarUrl { get; set; } = string.Empty;
    public DateTime LastLogin { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public class MinecraftVersion
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "release";
    public DateTime ReleaseDate { get; set; }
    public bool IsInstalled { get; set; }
    public string? Modloader { get; set; }
}

public class ModpackInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public int ModCount { get; set; }
    public long Downloads { get; set; }
    public string Category { get; set; } = string.Empty;
    public bool IsInstalled { get; set; }
    public string? IconUrl { get; set; }
}

public class ServerInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public int Port { get; set; } = 25565;
    public string Description { get; set; } = string.Empty;
    public bool IsOnline { get; set; }
    public int PlayerCount { get; set; }
    public int MaxPlayers { get; set; }
    public string Version { get; set; } = string.Empty;
    public string? IconUrl { get; set; }
    public string Category { get; set; } = string.Empty;

    public string PlayersText => $"{PlayerCount}/{MaxPlayers} jugadores";
    public string StatusText => IsOnline ? "En línea" : "Desconectado";
}

public class UserProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string? MinecraftUsername { get; set; }
    public string? AvatarUrl { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime LastUsed { get; set; } = DateTime.Now;
    public string? SelectedVersion { get; set; }
    public string? SelectedModpack { get; set; }
}

public enum SeriesStatus
{
    ComingSoon,
    NotInstalled,
    Installed,
    UpdateAvailable,
    MissingFiles
}

public class SeriesResourceRef
{
    public string Tag { get; set; } = string.Empty;
    public string Asset { get; set; } = string.Empty;
}

public class SeriesDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = string.Empty;
    public string Logo { get; set; } = string.Empty;
    public string Banner { get; set; } = string.Empty;
    public bool Available { get; set; } = true;
    public SeriesResourceRef Resources { get; set; } = new();

    // Datos de lanzamiento (los define el catálogo; no se inventan)
    public string MinecraftVersion { get; set; } = string.Empty;
    public string Loader { get; set; } = string.Empty;
    public string JVMArgs { get; set; } = string.Empty;
    public string GameArgs { get; set; } = string.Empty;

    // Novedades de esta versión (visible antes de actualizar)
    public string Changelog { get; set; } = string.Empty;
}

public class SeriesInstallation
{
    public string Id { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTime InstalledAt { get; set; } = DateTime.Now;
    public DateTime? UpdatedAt { get; set; }
    public string Status { get; set; } = "installed";
    public string Path { get; set; } = string.Empty;
    public string PackageSha256 { get; set; } = string.Empty;
}

public class SeriesPackageInfo
{
    public string DownloadUrl { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public class SeriesManifestEntry
{
    public string Path { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
}

public class SeriesPackageManifest
{
    public string Id { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public List<SeriesManifestEntry> Files { get; set; } = new();
}

public class SeriesInfo
{
    public SeriesDefinition Definition { get; set; } = new();
    public string InstalledVersion { get; set; } = string.Empty;
    public string InstallPath { get; set; } = string.Empty;
    public SeriesStatus Status { get; set; } = SeriesStatus.NotInstalled;

    public string Name => Definition.Name;
    public string Id => Definition.Id;
    public string Version => Definition.Version;
    public string Description => Definition.Description;
    public string Logo => Definition.Logo;
    public string Banner => Definition.Banner;
    public bool IsAvailable => Definition.Available;

    public string StatusText
    {
        get
        {
            var key = Status switch
            {
                SeriesStatus.ComingSoon => "S.Status.ComingSoon",
                SeriesStatus.NotInstalled => "S.Status.NotInstalled",
                SeriesStatus.Installed => "S.Status.Installed",
                SeriesStatus.UpdateAvailable => "S.Status.UpdateAvailable",
                SeriesStatus.MissingFiles => "S.Status.MissingFiles",
                _ => string.Empty
            };
            if (string.IsNullOrEmpty(key)) return string.Empty;

            var text = Services.Loc.T(key);
            // Fallback en español: nunca mostrar una clave cruda en la UI
            if (text.StartsWith("S.", StringComparison.Ordinal))
            {
                return Status switch
                {
                    SeriesStatus.ComingSoon => "Próximamente",
                    SeriesStatus.NotInstalled => "NO INSTALADO",
                    SeriesStatus.Installed => "INSTALADA",
                    SeriesStatus.UpdateAvailable => "ACTUALIZACIÓN DISPONIBLE",
                    SeriesStatus.MissingFiles => "FALTAN ARCHIVOS",
                    _ => string.Empty
                };
            }
            return text;
        }
    }

    public bool IsInstalled => Status == SeriesStatus.Installed
        || Status == SeriesStatus.UpdateAvailable
        || Status == SeriesStatus.MissingFiles;
}

public class SeriesProgress
{
    public int StepIndex { get; set; }
    public int StepCount { get; set; } = 3;
    public string StepName { get; set; } = string.Empty;
    public double Percent { get; set; }
    public string Message { get; set; } = string.Empty;
    public long BytesReceived { get; set; }
    public long TotalBytes { get; set; }
    public double SpeedBps { get; set; }
}

public class SeriesOperationResult
{
    public bool Success { get; set; }
    public int Installed { get; set; }
    public int Skipped { get; set; }
    public int Pending { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}