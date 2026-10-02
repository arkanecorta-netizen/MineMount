using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MineMount.Services;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace MineMount.ViewModels;

public partial class HomeViewModel : ObservableObject
{
    private readonly ILogService _logService;
    private readonly INewsService _newsService;

    [ObservableProperty]
    private string _welcomeMessage = "Bienvenido a MineMount";

    [ObservableProperty]
    private string _gameVersion = "1.21.4";

    [ObservableProperty]
    private string _modpackName = "Vanilla";

    [ObservableProperty]
    private string _statusText = "Listo";

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private ObservableCollection<NewsItem> _newsItems = new();

    [ObservableProperty]
    private string _bannerText = "MINECRAFT";

    [ObservableProperty]
    private string _bannerSubtitle = "La aventura comienza aquí";

    public HomeViewModel(ILogService logService, INewsService newsService)
    {
        _logService = logService;
        _newsService = newsService;
        _ = LoadNewsAsync();
    }

    private async Task LoadNewsAsync()
    {
        try
        {
            var news = await _newsService.GetNewsAsync();
            NewsItems = new ObservableCollection<NewsItem>(news);
        }
        catch (Exception ex)
        {
            _logService.Error("Failed to load news", ex);
        }
    }

    [RelayCommand]
    private async Task PlayAsync()
    {
        try
        {
            IsPlaying = true;
            StatusText = "Iniciando Minecraft...";
            _logInfo("Starting Minecraft...");

            await Task.Delay(2000);

            StatusText = "Minecraft en ejecución";
            _logInfo("Minecraft started");
        }
        catch (Exception ex)
        {
            _logError("Failed to start Minecraft", ex);
            StatusText = "Error al iniciar";
        }
        finally
        {
            IsPlaying = false;
        }
    }

    [RelayCommand]
    private void RefreshNews()
    {
        _ = LoadNewsAsync();
    }

    private void _logInfo(string message) => _logService.Info(message);
    private void _logError(string message, Exception ex) => _logService.Error(message, ex);
}