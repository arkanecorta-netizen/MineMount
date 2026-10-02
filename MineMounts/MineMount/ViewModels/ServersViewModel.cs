using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MineMount.Services;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

using MineMount.Models;

namespace MineMount.ViewModels;

public partial class ServersViewModel : ObservableObject
{
    private readonly ILogService _logService;
    private readonly IServerService _serverService;

    [ObservableProperty]
    private ObservableCollection<ServerInfo> _servers = new();

    [ObservableProperty]
    private ServerInfo? _selectedServer;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _statusText = "Listo";

    [ObservableProperty]
    private int _playerCount;

    [ObservableProperty]
    private int _maxPlayers;

    public ServersViewModel(ILogService logService, IServerService serverService)
    {
        _logService = logService;
        _serverService = serverService;
        _ = LoadServersAsync();
    }

    private async Task LoadServersAsync()
    {
        try
        {
            IsLoading = true;
            StatusText = "Cargando servidores...";

            var servers = await _serverService.GetServersAsync();
            Servers = new ObservableCollection<ServerInfo>(servers);

            StatusText = $"{Servers.Count} servidores encontrados";
        }
        catch (Exception ex)
        {
            _logService.Error("Failed to load servers", ex);
            StatusText = "Error al cargar servidores";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadServersAsync();
    }

    [RelayCommand]
    private async Task AddServerAsync()
    {
        try
        {
            IsLoading = true;
            StatusText = "Agregando servidor...";

            var newServer = new ServerInfo
            {
                Name = "Nuevo Servidor",
                Address = "play.ejemplo.com",
                Port = 25565,
                Description = "Servidor de ejemplo",
                IsOnline = true,
                PlayerCount = 0,
                MaxPlayers = 20,
                Version = "1.21.4",
                IconUrl = string.Empty
            };

            await _serverService.AddServerAsync(newServer);
            Servers.Add(newServer);

            StatusText = "Servidor agregado";
        }
        catch (Exception ex)
        {
            _logService.Error("Failed to add server", ex);
            StatusText = "Error al agregar servidor";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RemoveServerAsync(ServerInfo? server)
    {
        if (server == null) return;

        try
        {
            IsLoading = true;
            StatusText = "Eliminando servidor...";

            await _serverService.RemoveServerAsync(server);
            Servers.Remove(server);

            StatusText = "Servidor eliminado";
        }
        catch (Exception ex)
        {
            _logService.Error("Failed to remove server", ex);
            StatusText = "Error al eliminar servidor";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task JoinServerAsync(ServerInfo? server)
    {
        if (server == null) return;

        try
        {
            IsLoading = true;
            StatusText = $"Conectando a {server.Name}...";

            await _serverService.JoinServerAsync(server);

            StatusText = "Conectado";
        }
        catch (Exception ex)
        {
            _logService.Error($"Failed to join server {server.Name}", ex);
            StatusText = "Error al conectar";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void FilterServers()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            _ = LoadServersAsync();
            return;
        }

        var filtered = Servers.Where(s =>
            s.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
            s.Address.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        Servers = new ObservableCollection<ServerInfo>(filtered);
    }
}