using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MineMount.Services;

public interface IModpackService
{
    Task<List<Models.ModpackInfo>> GetModpacksAsync();
    Task InstallModpackAsync(Models.ModpackInfo modpack);
}

public class ModpackService : IModpackService
{
    public Task<List<Models.ModpackInfo>> GetModpacksAsync()
    {
        var modpacks = new List<Models.ModpackInfo>
        {
            new()
            {
                Id = "vanilla",
                Name = "Vanilla",
                Description = "Minecraft sin modificaciones",
                Version = "1.21.4",
                Author = "Mojang",
                ModCount = 0,
                Downloads = 0,
                Category = "Vanilla",
                IsInstalled = true
            },
            new()
            {
                Id = "fabric-1.21.4",
                Name = "Fabric 1.21.4",
                Description = "Cargador de mods ligero y moderno",
                Version = "1.21.4",
                Author = "FabricMC",
                ModCount = 0,
                Downloads = 1500000,
                Category = "Modloader",
                IsInstalled = false
            },
            new()
            {
                Id = "neoforge-1.21.4",
                Name = "NeoForge 1.21.4",
                Description = "Fork de Forge con mejoras de rendimiento",
                Version = "1.21.4",
                Author = "NeoForge",
                ModCount = 0,
                Downloads = 800000,
                Category = "Modloader",
                IsInstalled = false
            },
            new()
            {
                Id = "atm9",
                Name = "All The Mods 9",
                Description = "Modpack de tecnología y magia con más de 300 mods",
                Version = "1.20.1",
                Author = "ATM Team",
                ModCount = 350,
                Downloads = 2500000,
                Category = "Modpack",
                IsInstalled = false
            },
            new()
            {
                Id = "skyfactory-4",
                Name = "SkyFactory 4",
                Description = "Modpack de skyblock con árboles y automatización",
                Version = "1.12.2",
                Author = "Darkosto",
                ModCount = 200,
                Downloads = 1800000,
                Category = "Skyblock",
                IsInstalled = false
            },
            new()
            {
                Id = "rlcraft",
                Name = "RLCraft",
                Description = "Modpack de supervivencia extrema y realista",
                Version = "1.12.2",
                Author = "Shivaxi",
                ModCount = 150,
                Downloads = 3000000,
                Category = "Hardcore",
                IsInstalled = false
            }
        };

        return Task.FromResult(modpacks);
    }

    public Task InstallModpackAsync(Models.ModpackInfo modpack)
    {
        return Task.Delay(2000);
    }
}