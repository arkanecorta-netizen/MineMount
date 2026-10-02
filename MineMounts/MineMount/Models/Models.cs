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
    public string Theme { get; set; } = "Dark";
    public string Language { get; set; } = "es-ES";
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

public class SeriesFileManifest
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string? Hash { get; set; }
    public long? Size { get; set; }
    public bool Required { get; set; } = true;
}

public class SeriesManifest
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public bool Available { get; set; } = true;
    public List<SeriesFileManifest> Files { get; set; } = new();
}

public class SeriesInstallState
{
    public string Version { get; set; } = string.Empty;
    public DateTime InstalledAt { get; set; } = DateTime.Now;
    public List<string> InstalledFiles { get; set; } = new();
}

public class SeriesInfo
{
    public SeriesManifest Manifest { get; set; } = new();
    public string InstalledVersion { get; set; } = string.Empty;
    public SeriesStatus Status { get; set; } = SeriesStatus.NotInstalled;

    public string Name => Manifest.Name;
    public string Id => Manifest.Id;
    public string Version => Manifest.Version;
    public string Description => Manifest.Description;
    public string Image => Manifest.Image;
    public bool IsAvailable => Manifest.Available;

    public string StatusText => Status switch
    {
        SeriesStatus.ComingSoon => "Próximamente",
        SeriesStatus.NotInstalled => "No instalado",
        SeriesStatus.Installed => "TODO INSTALADO",
        SeriesStatus.UpdateAvailable => "ACTUALIZACIÓN DISPONIBLE",
        SeriesStatus.MissingFiles => "FALTAN ARCHIVOS",
        _ => string.Empty
    };

    public bool IsInstalled => Status == SeriesStatus.Installed
        || Status == SeriesStatus.UpdateAvailable
        || Status == SeriesStatus.MissingFiles;
}

public class SeriesProgress
{
    public int FileIndex { get; set; }
    public int FileCount { get; set; }
    public string FileName { get; set; } = string.Empty;
    public double Percent { get; set; }
    public string Message { get; set; } = string.Empty;
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