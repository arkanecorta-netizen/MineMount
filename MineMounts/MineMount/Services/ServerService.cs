using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MineMount.Services;

public interface IServerService
{
    Task<List<Models.ServerInfo>> GetServersAsync();
    Task AddServerAsync(Models.ServerInfo server);
    Task RemoveServerAsync(Models.ServerInfo server);
    Task JoinServerAsync(Models.ServerInfo server);
}

public class ServerService : IServerService
{
    public Task<List<Models.ServerInfo>> GetServersAsync()
    {
        var servers = new List<Models.ServerInfo>
        {
            new()
            {
                Id = "hypixel",
                Name = "Hypixel Network",
                Address = "mc.hypixel.net",
                Port = 25565,
                Description = "El servidor de Minecraft más grande del mundo",
                IsOnline = true,
                PlayerCount = 45000,
                MaxPlayers = 100000,
                Version = "1.8-1.21",
                IconUrl = string.Empty,
                Category = "Minijuegos"
            },
            new()
            {
                Id = "mineplex",
                Name = "Mineplex",
                Address = "us.mineplex.com",
                Port = 25565,
                Description = "Servidor de minijuegos clásicos",
                IsOnline = true,
                PlayerCount = 3500,
                MaxPlayers = 10000,
                Version = "1.8-1.21",
                IconUrl = string.Empty,
                Category = "Minijuegos"
            },
            new()
            {
                Id = "2b2t",
                Name = "2b2t",
                Address = "2b2t.org",
                Port = 25565,
                Description = "El servidor anarchy más antiguo de Minecraft",
                IsOnline = true,
                PlayerCount = 800,
                MaxPlayers = 100000,
                Version = "1.12-1.21",
                IconUrl = string.Empty,
                Category = "Anarchy"
            },
            new()
            {
                Id = "grian",
                Name = "Grian's Server",
                Address = "play.grianserver.com",
                Port = 25565,
                Description = "Servidor de supervivencia amigable",
                IsOnline = true,
                PlayerCount = 120,
                MaxPlayers = 200,
                Version = "1.21",
                IconUrl = string.Empty,
                Category = "Supervivencia"
            },
            new()
            {
                Id = "pixelmon",
                Name = "Pixelmon Reforged",
                Address = "play.pixelmonmod.com",
                Port = 25565,
                Description = "Minecraft con Pokémon",
                IsOnline = true,
                PlayerCount = 2500,
                MaxPlayers = 5000,
                Version = "1.16.5",
                IconUrl = string.Empty,
                Category = "Modded"
            }
        };

        return Task.FromResult(servers);
    }

    public Task AddServerAsync(Models.ServerInfo server)
    {
        return Task.CompletedTask;
    }

    public Task RemoveServerAsync(Models.ServerInfo server)
    {
        return Task.CompletedTask;
    }

    public Task JoinServerAsync(Models.ServerInfo server)
    {
        return Task.CompletedTask;
    }
}