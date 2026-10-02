using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MineMount.Services;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

using MineMount.Models;

namespace MineMount.ViewModels;

public partial class GamesViewModel : ObservableObject
{
    private readonly ILogService _logService;
    private readonly IMinecraftService _minecraftService;
    private readonly IModpackService _modpackService;

    [ObservableProperty]
    private ObservableCollection<MinecraftVersion> _versions = new();

    [ObservableProperty]
    private ObservableCollection<ModpackInfo> _modpacks = new();

    [ObservableProperty]
    private MinecraftVersion? _selectedVersion;

    [ObservableProperty]
    private ModpackInfo? _selectedModpack;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _statusText = "Listo";

    public GamesViewModel(
        ILogService logService,
        IMinecraftService minecraftService,
        IModpackService modpackService)
    {
        _logService = logService;
        _minecraftService = minecraftService;
        _modpackService = modpackService;
        _ = LoadDataAsync();
    }

    private async Task LoadDataAsync()
    {
        try
        {
            IsLoading = true;
            StatusText = "Cargando versiones...";

            var versions = await _minecraftService.GetVersionsAsync();
            Versions = new ObservableCollection<MinecraftVersion>(versions);

            var modpacks = await _modpackService.GetModpacksAsync();
            Modpacks = new ObservableCollection<ModpackInfo>(modpacks);

            StatusText = "Datos cargados";
        }
        catch (Exception ex)
        {
            _logService.Error("Failed to load game data", ex);
            StatusText = "Error al cargar";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadDataAsync();
    }

    [RelayCommand]
    private async Task InstallVersionAsync(MinecraftVersion? version)
    {
        if (version == null) return;

        try
        {
            IsLoading = true;
            StatusText = $"Instalando {version.Name}...";

            await _minecraftService.InstallVersionAsync(version);

            StatusText = $"{version.Name} instalado";
        }
        catch (Exception ex)
        {
            _logService.Error($"Failed to install version {version.Name}", ex);
            StatusText = "Error al instalar";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task InstallModpackAsync(ModpackInfo? modpack)
    {
        if (modpack == null) return;

        try
        {
            IsLoading = true;
            StatusText = $"Instalando {modpack.Name}...";

            await _modpackService.InstallModpackAsync(modpack);

            StatusText = $"{modpack.Name} instalado";
        }
        catch (Exception ex)
        {
            _logService.Error($"Failed to install modpack {modpack.Name}", ex);
            StatusText = "Error al instalar";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void FilterVersions()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            _ = LoadDataAsync();
            return;
        }

        var filtered = Versions.Where(v =>
            v.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        Versions = new ObservableCollection<MinecraftVersion>(filtered);
    }
}