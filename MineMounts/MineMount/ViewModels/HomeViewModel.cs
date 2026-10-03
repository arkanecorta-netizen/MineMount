using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MineMount.Models;
using MineMount.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace MineMount.ViewModels;

public partial class HomeViewModel : ObservableObject
{
    private const string DefaultBannerUri = "pack://application:,,,/Assets/MineMount/banner.png";

    private readonly ILogService _logService;
    private readonly INewsService _newsService;
    private readonly ISeriesService _seriesService;
    private readonly ISeriesInstallService _installService;
    private readonly IGameLauncherService _gameLauncher;
    private readonly INotificationService _notificationService;

    [ObservableProperty]
    private ObservableCollection<NewsItem> _newsItems = new();

    [ObservableProperty]
    private ObservableCollection<NewsItem> _recentNews = new();

    [ObservableProperty]
    private NewsItem? _featuredNews;

    [ObservableProperty]
    private SeriesInfo? _featuredSeries;

    [ObservableProperty]
    private string _bannerTitle = "BIENVENIDO A MINEMOUNT";

    [ObservableProperty]
    private string _bannerSubtitle = "Descubrí, instalá y gestioná tus series desde un solo lugar";

    [ObservableProperty]
    private string _bannerImageUrl = string.Empty;

    [ObservableProperty]
    private bool _bannerIsNews;

    [ObservableProperty]
    private string _bannerActionText = string.Empty;

    [ObservableProperty]
    private string _featuredActionText = "INSTALAR";

    [ObservableProperty]
    private bool _isFeaturedBusy;

    [ObservableProperty]
    private double _featuredProgress;

    [ObservableProperty]
    private string _featuredProgressText = string.Empty;

    [ObservableProperty]
    private bool _isLoadingNews;

    [ObservableProperty]
    private bool _hasNews;

    public HomeViewModel(
        ILogService logService,
        INewsService newsService,
        ISeriesService seriesService,
        ISeriesInstallService installService,
        IGameLauncherService gameLauncher,
        INotificationService notificationService)
    {
        _logService = logService;
        _newsService = newsService;
        _seriesService = seriesService;
        _installService = installService;
        _gameLauncher = gameLauncher;
        _notificationService = notificationService;

        _newsService.OnNewsUpdated += OnNewsUpdated;
        _ = LoadAsync();
    }

    private void OnNewsUpdated(object? sender, System.Collections.Generic.List<NewsItem> news)
    {
        ApplyNews(news);
    }

    private async Task LoadAsync()
    {
        try
        {
            IsLoadingNews = true;

            var news = await _newsService.GetNewsAsync();
            ApplyNews(news);

            var series = await _seriesService.GetSeriesAsync();
            FeaturedSeries = series.FirstOrDefault(s => !string.IsNullOrEmpty(s.Banner))
                ?? series.FirstOrDefault();
            UpdateFeaturedAction();
        }
        catch (Exception ex)
        {
            _logService.Error("Failed to load home", ex);
        }
        finally
        {
            IsLoadingNews = false;
        }
    }

    private void ApplyNews(System.Collections.Generic.List<NewsItem> news)
    {
        NewsItems = new ObservableCollection<NewsItem>(news);
        HasNews = news.Count > 0;

        // 1 noticia destacada con imagen → banner principal
        FeaturedNews = news.FirstOrDefault(n => n.Featured && !string.IsNullOrEmpty(n.ImageUrl))
            ?? news.FirstOrDefault(n => n.Featured)
            ?? news.FirstOrDefault(n => !string.IsNullOrEmpty(n.ImageUrl));

        RecentNews = new ObservableCollection<NewsItem>(
            news.Where(n => n != FeaturedNews).Take(4));

        if (FeaturedNews != null && !string.IsNullOrEmpty(FeaturedNews.ImageUrl))
        {
            BannerIsNews = true;
            BannerTitle = FeaturedNews.Title;
            BannerSubtitle = FeaturedNews.Description;
            BannerImageUrl = FeaturedNews.ImageUrl;
            BannerActionText = string.Empty;
        }
        else if (FeaturedSeries != null)
        {
            BannerIsNews = false;
            BannerTitle = FeaturedSeries.Name;
            BannerSubtitle = FeaturedSeries.Description;
            BannerImageUrl = FeaturedSeries.Banner;
            BannerActionText = FeaturedActionText;
        }
        else
        {
            BannerIsNews = false;
            BannerTitle = "BIENVENIDO A MINEMOUNT";
            BannerSubtitle = "Descubrí, instalá y gestioná tus series desde un solo lugar";
            BannerImageUrl = DefaultBannerUri;
            BannerActionText = string.Empty;
        }
    }

    private void UpdateFeaturedAction()
    {
        if (FeaturedSeries == null) return;

        FeaturedActionText = FeaturedSeries.Status switch
        {
            SeriesStatus.NotInstalled => "INSTALAR",
            SeriesStatus.Installed => "JUGAR",
            SeriesStatus.UpdateAvailable => "ACTUALIZAR",
            SeriesStatus.MissingFiles => "REPARAR",
            SeriesStatus.ComingSoon => "PRÓXIMAMENTE",
            _ => string.Empty
        };
    }

    [RelayCommand]
    private async Task FeaturedActionAsync()
    {
        if (FeaturedSeries == null || IsFeaturedBusy) return;

        if (FeaturedSeries.Status == SeriesStatus.Installed)
        {
            await PlayAsync();
            return;
        }

        if (FeaturedSeries.Status == SeriesStatus.ComingSoon) return;

        IsFeaturedBusy = true;
        FeaturedProgress = 0;
        FeaturedProgressText = "Preparando...";

        try
        {
            var progress = new Progress<SeriesProgress>(p =>
            {
                FeaturedProgress = p.Percent;
                FeaturedProgressText = $"Paso {p.StepIndex}/{p.StepCount} · {p.StepName}";
            });

            SeriesOperationResult result;
            if (FeaturedSeries.Status == SeriesStatus.UpdateAvailable)
            {
                result = await _installService.UpdateAsync(FeaturedSeries.Id, progress);
            }
            else if (FeaturedSeries.Status == SeriesStatus.MissingFiles)
            {
                result = await _installService.RepairAsync(FeaturedSeries.Id, progress);
            }
            else
            {
                result = await _installService.InstallAsync(FeaturedSeries.Id, progress);
            }

            if (result.Success)
            {
                _notificationService.NotifySuccess(
                    $"{FeaturedSeries.Name} lista",
                    result.Message);
            }
            else
            {
                _notificationService.NotifyError(
                    "Operación fallida",
                    result.Message);
            }
        }
        catch (Exception ex)
        {
            _logService.Error("Featured series operation failed", ex);
            _notificationService.NotifyError("Error", ex.Message);
        }
        finally
        {
            IsFeaturedBusy = false;
            FeaturedProgressText = string.Empty;
        }

        var series = await _seriesService.GetSeriesAsync();
        FeaturedSeries = series.FirstOrDefault(s => s.Id == FeaturedSeries?.Id)
            ?? series.FirstOrDefault();
        UpdateFeaturedAction();
    }

    [RelayCommand]
    private async Task PlayAsync()
    {
        if (FeaturedSeries == null) return;

        var result = await _gameLauncher.LaunchAsync(FeaturedSeries.Id);
        if (!result.Success && string.IsNullOrEmpty(result.Message))
        {
            _notificationService.NotifyError("Error", "No se pudo iniciar la serie.");
        }
    }

    [RelayCommand]
    private void RefreshNews()
    {
        _ = LoadAsync();
    }
}
