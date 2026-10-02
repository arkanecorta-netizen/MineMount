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
    [NotifyPropertyChangedFor(nameof(HasNews))]
    private ObservableCollection<NewsItem> _newsItems = new();

    public bool HasNews => NewsItems.Count > 0;

    [ObservableProperty]
    private string _bannerText = "BIENVENIDO A MINEMOUNT";

    [ObservableProperty]
    private string _bannerSubtitle = "Descubrí, instalá y gestioná tus series desde un solo lugar";

    [ObservableProperty]
    private bool _isLoadingNews;

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
            IsLoadingNews = true;
            var news = await _newsService.GetNewsAsync();
            NewsItems = new ObservableCollection<NewsItem>(news);
        }
        catch (Exception ex)
        {
            _logService.Error("Failed to load news", ex);
        }
        finally
        {
            IsLoadingNews = false;
        }
    }

    [RelayCommand]
    private void RefreshNews()
    {
        _ = LoadNewsAsync();
    }
}
