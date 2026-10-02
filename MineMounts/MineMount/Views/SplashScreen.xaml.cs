using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MineMount.Services;

namespace MineMount.Views;

public partial class SplashScreen : Window
{
    private const int MinDurationMs = 11_000;
    private const int MaxDurationMs = 18_000;

    private readonly ISettingsService _settingsService;
    private readonly IUpdateService _updateService;
    private readonly ISeriesService _seriesService;
    private readonly INewsService _newsService;
    private readonly ILogService _logService;

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public SplashScreen(
        ISettingsService settingsService,
        IUpdateService updateService,
        ISeriesService seriesService,
        INewsService newsService,
        ILogService logService)
    {
        _settingsService = settingsService;
        _updateService = updateService;
        _seriesService = seriesService;
        _newsService = newsService;
        _logService = logService;

        InitializeComponent();
    }

    public async Task RunAsync()
    {
        var steps = new (string Label, Func<Task> Work)[]
        {
            ("Cargando configuración...", LoadConfigurationAsync),
            ("Comprobando actualizaciones...", CheckUpdatesAsync),
            ("Comprobando instalación de series...", CheckSeriesAsync),
            ("Inicializando servicios...", InitializeServicesAsync),
            ("Cargando recursos...", LoadResourcesAsync)
        };

        var completed = 0;
        var displayed = 0.0;

        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };

        timer.Tick += (_, _) =>
        {
            var allDone = completed >= steps.Length;
            var minReached = _stopwatch.ElapsedMilliseconds >= MinDurationMs;
            var target = allDone && minReached ? 100.0 : Math.Min(96.0, completed * 19.0 + 8.0);

            displayed += (target - displayed) * 0.18;
            if (target >= 100.0 && displayed > 99.0) displayed = 100.0;

            UpdateProgress(displayed);
        };

        timer.Start();

        foreach (var (label, work) in steps)
        {
            if (_stopwatch.ElapsedMilliseconds >= MaxDurationMs)
            {
                _logService.Warning("Pantalla de carga: se alcanzó el tiempo máximo");
                break;
            }

            SetStatus(label);
            _logService.Info($"Splash: {label}");

            try
            {
                await work();
            }
            catch (Exception ex)
            {
                _logService.Error($"Splash step failed: {label}", ex);
            }

            completed++;
        }

        // Espera asíncrona hasta la duración mínima (la UI sigue viva y animada)
        while (_stopwatch.ElapsedMilliseconds < Math.Min(MinDurationMs, MaxDurationMs))
        {
            await Task.Delay(100);
        }

        // Completar la barra al 100%
        while (displayed < 99.5)
        {
            displayed = Math.Min(100.0, displayed + 4.0);
            UpdateProgress(displayed);
            await Task.Delay(50);
        }

        UpdateProgress(100);
        SetStatus("Listo");
        _logService.Info($"Splash completado en {_stopwatch.ElapsedMilliseconds} ms");

        await Task.Delay(300);
        timer.Stop();
    }

    // ---------------------------------------------------------------
    //  Tareas reales de inicialización
    // ---------------------------------------------------------------
    private async Task LoadConfigurationAsync()
    {
        await _settingsService.LoadAsync();
    }

    private async Task CheckUpdatesAsync()
    {
        var hasUpdate = await _updateService.CheckForUpdatesAsync();

        if (hasUpdate || _updateService.IsUpdateAvailable)
        {
            SetStatus("Actualizando el launcher...");
            await _updateService.DownloadUpdateAsync();
            SetStatus("Comprobando actualizaciones...");
        }
    }

    private async Task CheckSeriesAsync()
    {
        var series = await _seriesService.GetSeriesAsync();
        _logService.Info($"Splash: {series.Count} series encontradas");
    }

    private async Task InitializeServicesAsync()
    {
        var news = await _newsService.GetNewsAsync();
        _logService.Info($"Splash: {news.Count} noticias cargadas");
        await Task.Yield();
    }

    private async Task LoadResourcesAsync()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MineMount");
        Directory.CreateDirectory(appData);

        var root = await _seriesService.GetInstallRootAsync();
        Directory.CreateDirectory(root);

        await Task.Yield();
    }

    // ---------------------------------------------------------------
    //  UI
    // ---------------------------------------------------------------
    private void SetStatus(string text) => StatusLabel.Text = text;

    private void UpdateProgress(double value)
    {
        ProgressBar.Value = value;
        PercentLabel.Text = $"{value:F0} %";
    }
}
