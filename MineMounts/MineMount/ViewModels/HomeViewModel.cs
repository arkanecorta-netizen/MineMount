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
    private readonly IAppearanceService _appearanceService;

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

    // Hero tipográfico sobre el fondo: etiqueta + título + descripción + estado
    [ObservableProperty]
    private string _heroEyebrow = "MINEMOUNT · LAUNCHER";

    [ObservableProperty]
    private string _heroLogo = "pack://application:,,,/Assets/MineMount/logo.png";

    [ObservableProperty]
    private string _heroStatusLine = string.Empty;

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
        INotificationService notificationService,
        IAppearanceService appearanceService)
    {
        _logService = logService;
        _newsService = newsService;
        _seriesService = seriesService;
        _installService = installService;
        _gameLauncher = gameLauncher;
        _notificationService = notificationService;
        _appearanceService = appearanceService;
        _appearanceService.LanguageChanged += (_, _) =>
        {
            UpdateHero();
            OnPropertyChanged(nameof(SeriesVersionLine));
            if (!BannerIsNews && FeaturedSeries == null)
            {
                BannerTitle = Loc.T("S.Home.Welcome");
                BannerSubtitle = Loc.T("S.Home.Subtitle");
                HeroEyebrow = Loc.T("S.Home.Launcher");
            }
        };

        _newsService.OnNewsUpdated += OnNewsUpdated;
        _ = LoadAsync();
    }

    partial void OnFeaturedSeriesChanged(SeriesInfo? value) => UpdateHero();

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
            UpdateHero();
            ApplySeriesBanner();
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
            news.Where(n => n != FeaturedNews).Take(6));

        if (FeaturedNews != null && !string.IsNullOrEmpty(FeaturedNews.ImageUrl))
        {
            BannerIsNews = true;
            BannerTitle = FeaturedNews.Title;
            BannerSubtitle = FeaturedNews.Description;
            BannerImageUrl = FeaturedNews.ImageUrl;
            BannerActionText = string.Empty;
            HeroEyebrow = string.IsNullOrWhiteSpace(FeaturedNews.Category)
                ? "NOTICIA DESTACADA"
                : $"{FeaturedNews.Category.ToUpperInvariant()} · DESTACADO";
            HeroLogo = "pack://application:,,,/Assets/MineMount/logo.png";
        }
        else if (FeaturedSeries != null)
        {
            BannerIsNews = false;
            BannerTitle = FeaturedSeries.Name;
            BannerSubtitle = FeaturedSeries.Description;
            BannerImageUrl = FeaturedSeries.Banner;
            BannerActionText = FeaturedActionText;
            UpdateHero();
        }
        else
        {
            BannerIsNews = false;
            BannerTitle = Loc.T("S.Home.Welcome");
            BannerSubtitle = Loc.T("S.Home.Subtitle");
            BannerImageUrl = DefaultBannerUri;
            BannerActionText = string.Empty;
            HeroEyebrow = Loc.T("S.Home.Launcher");
            HeroLogo = "pack://application:,,,/Assets/MineMount/logo.png";
        }
    }

    /// <summary>
    /// Línea del selector de serie/versión: "NSE6 · 1.20.1 · Forge" (datos reales del catálogo).
    /// </summary>
    public string SeriesVersionLine => FeaturedSeries == null
        ? "Sin series"
        : string.Join(" · ", new[]
        {
            FeaturedSeries.Name,
            string.IsNullOrWhiteSpace(FeaturedSeries.Definition.MinecraftVersion)
                ? $"v{FeaturedSeries.Version}"
                : FeaturedSeries.Definition.MinecraftVersion,
            FeaturedSeries.Definition.Loader
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

    private void UpdateHero()
    {
        if (FeaturedSeries == null) return;

        var parts = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrWhiteSpace(FeaturedSeries.Definition.MinecraftVersion))
            parts.Add(FeaturedSeries.Definition.MinecraftVersion);
        if (!string.IsNullOrWhiteSpace(FeaturedSeries.Definition.Loader))
            parts.Add(FeaturedSeries.Definition.Loader.ToUpperInvariant());
        parts.Add($"v{FeaturedSeries.Version}");

        HeroEyebrow = $"{FeaturedSeries.Name.ToUpperInvariant()} · {string.Join(" · ", parts)}";
        HeroLogo = string.IsNullOrWhiteSpace(FeaturedSeries.Logo)
            ? "pack://application:,,,/Assets/MineMount/logo.png"
            : FeaturedSeries.Logo;
        HeroStatusLine = $"{FeaturedSeries.StatusText.ToUpperInvariant()} · {string.Join(" · ", parts)}";
        OnPropertyChanged(nameof(SeriesVersionLine));
    }

    /// <summary>
    /// Si no hay noticia destacada con imagen, el hero muestra la serie (nombre,
    /// descripción y arte) en vez de un banner genérico vacío.
    /// </summary>
    private void ApplySeriesBanner()
    {
        if (FeaturedNews != null && !string.IsNullOrEmpty(FeaturedNews.ImageUrl)) return;
        if (FeaturedSeries == null) return;

        BannerIsNews = false;
        BannerTitle = FeaturedSeries.Name;
        BannerSubtitle = FeaturedSeries.Description;
        BannerImageUrl = FeaturedSeries.Banner;
        BannerActionText = FeaturedActionText;
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
        UpdateHero();
        ApplySeriesBanner();
    }

    [RelayCommand]
    private async Task PlayAsync()
    {
        if (FeaturedSeries == null) return;

        var progress = new Progress<double>(p =>
        {
            FeaturedProgress = p;
            FeaturedProgressText = $"Preparando... {p:F0}%";
        });

        var result = await _gameLauncher.LaunchAsync(FeaturedSeries.Id, progress);
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
