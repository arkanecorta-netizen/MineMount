using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MineMount.Services;

public interface IMinecraftService
{
    Task<List<Models.MinecraftVersion>> GetVersionsAsync();
    Task InstallVersionAsync(Models.MinecraftVersion version);
    Task<bool> IsVersionInstalledAsync(string versionId);
}

public class MinecraftService : IMinecraftService
{
    public Task<List<Models.MinecraftVersion>> GetVersionsAsync()
    {
        var versions = new List<Models.MinecraftVersion>
        {
            new() { Id = "1.21.4", Name = "1.21.4", Type = "release", ReleaseDate = new DateTime(2024, 12, 3) },
            new() { Id = "1.21.3", Name = "1.21.3", Type = "release", ReleaseDate = new DateTime(2024, 10, 23) },
            new() { Id = "1.21.2", Name = "1.21.2", Type = "release", ReleaseDate = new DateTime(2024, 10, 22) },
            new() { Id = "1.21.1", Name = "1.21.1", Type = "release", ReleaseDate = new DateTime(2024, 8, 8) },
            new() { Id = "1.21", Name = "1.21", Type = "release", ReleaseDate = new DateTime(2024, 6, 13) },
            new() { Id = "1.20.6", Name = "1.20.6", Type = "release", ReleaseDate = new DateTime(2024, 4, 29) },
            new() { Id = "1.20.4", Name = "1.20.4", Type = "release", ReleaseDate = new DateTime(2023, 12, 7) },
            new() { Id = "1.20.1", Name = "1.20.1", Type = "release", ReleaseDate = new DateTime(2023, 6, 12) },
            new() { Id = "1.19.4", Name = "1.19.4", Type = "release", ReleaseDate = new DateTime(2023, 3, 14) },
            new() { Id = "1.18.2", Name = "1.18.2", Type = "release", ReleaseDate = new DateTime(2022, 2, 28) },
            new() { Id = "24w44a", Name = "Snapshot 24w44a", Type = "snapshot", ReleaseDate = new DateTime(2024, 10, 30) },
            new() { Id = "24w40a", Name = "Snapshot 24w40a", Type = "snapshot", ReleaseDate = new DateTime(2024, 10, 2) }
        };

        return Task.FromResult(versions);
    }

    public Task InstallVersionAsync(Models.MinecraftVersion version)
    {
        return Task.Delay(1000);
    }

    public Task<bool> IsVersionInstalledAsync(string versionId)
    {
        return Task.FromResult(false);
    }
}