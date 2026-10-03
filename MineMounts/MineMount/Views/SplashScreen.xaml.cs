using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MineMount.Services;

namespace MineMount.Views;

public partial class SplashScreen : Window
{
    private const int MinDurationMs = 6_000;
    private const int MaxDurationMs = 20_000;

    private readonly ISettingsService _settingsService;
    private readonly IUpdateService _updateService;
    private readonly ISeriesService _seriesService;
    private readonly INewsService _newsService;
    private readonly ILogService _logService;
    private readonly ITempCleanupService _tempCleanupService;

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public SplashScreen(
        ISettingsService settingsService,
        IUpdateService updateService,
        ISeriesService seriesService,
        INewsService newsService,
        ILogService logService,
        ITempCleanupService tempCleanupService)
    {
        _settingsService = settingsService;
        _updateService = updateService;
        _seriesService = seriesService;
        _newsService = newsService;
        _logService = logService;
        _tempCleanupService = tempCleanupService;

        InitializeComponent();
        VersionLabel.Text = updateService.CurrentVersion;
    }

    public async Task RunAsync()
    {
        // Cada paso tiene un peso sobre el progreso total. Los pasos
        // instantáneos animan suavemente; la descarga usa progreso real.
        var steps = new (string Label, double Weight, Func<IProgress<double>, Task> Work)[]
        {
            ("Cargando configuración...", 5, LoadConfigurationAsync),
            ("Comprobando actualizaciones...", 25, CheckUpdatesAsync),
            ("Comprobando instalación de series...", 10, CheckSeriesAsync),
            ("Inicializando servicios...", 10, InitializeServicesAsync),
            ("Cargando recursos...", 10, LoadResourcesAsync),
            ("Finalizando...", 40, FinalizeAsync)
        };

        var totalWeight = 0.0;
        foreach (var (_, weight, _) in steps) totalWeight += weight;

        var completedWeight = 0.0;
        var currentProgress = 0.0;

        foreach (var (label, weight, work) in steps)
        {
            if (_stopwatch.ElapsedMilliseconds >= MaxDurationMs)
            {
                _logService.Warning("Pantalla de carga: se alcanzó el tiempo máximo");
                break;
            }

            SetStatus(label);
            _logService.Info($"Splash: {label}");

            var stepProgress = 0.0;
            var progress = new Progress<double>(p => stepProgress = p);

            try
            {
                await work(progress);
            }
            catch (Exception ex)
            {
                _logService.Error($"Splash step failed: {label}", ex);
            }

            // Asegurar que el paso llega al 100% aunque no lo reporte
            if (stepProgress < 100)
            {
                await AnimateStepProgress(() => stepProgress, p => stepProgress = p, 300);
            }

            completedWeight += weight;
            currentProgress = completedWeight * 100.0 / totalWeight;
            UpdateProgress(currentProgress);
        }

        // Espera asíncrona hasta la duración mínima (la UI sigue viva)
        while (_stopwatch.ElapsedMilliseconds < MinDurationMs)
        {
            var remaining = MinDurationMs - _stopwatch.ElapsedMilliseconds;
            var target = 100.0;
            var step = Math.Max(0.5, (target - currentProgress) * 0.08);
            currentProgress = Math.Min(target, currentProgress + step);
            UpdateProgress(currentProgress);
            await Task.Delay((int)Math.Min(100, remaining));
        }

        UpdateProgress(100);
        SetStatus("Listo");
        _logService.Info($"Splash completado en {_stopwatch.ElapsedMilliseconds} ms");

        await Task.Delay(300);
    }

    // ---------------------------------------------------------------
    //  Tareas reales de inicialización
    // ---------------------------------------------------------------
    private async Task LoadConfigurationAsync(IProgress<double> progress)
    {
        await _settingsService.LoadAsync();
        progress.Report(100);
    }

    private async Task CheckUpdatesAsync(IProgress<double> progress)
    {
        var hasUpdate = await _updateService.CheckForUpdatesAsync();

        if (!hasUpdate)
        {
            if (!string.IsNullOrEmpty(_updateService.LastError))
            {
                _logService.Info($"Splash: sin actualización — {_updateService.LastError}");
            }

            progress.Report(100);
            return;
        }

        _logService.Info(
            $"Splash: actualización disponible {_updateService.CurrentVersion} → {_updateService.LatestVersion}");

        var settings = await _settingsService.GetSettingsAsync();
        if (!settings.AutoUpdate)
        {
            _logService.Info("Splash: AutoUpdate desactivado; se continúa con la versión actual");
            progress.Report(100);
            return;
        }

        if (!_updateService.CanAutoApply)
        {
            _logService.Warning("Splash: límite de intentos de actualización alcanzado; se continúa");
            progress.Report(100);
            return;
        }

        SetStatus($"Descargando v{_updateService.LatestVersion}...");
        var downloadProgress = new Progress<double>(p =>
        {
            SetStatus($"Descargando v{_updateService.LatestVersion}... {p:F0}%");
            progress.Report(p);
        });

        var ok = await _updateService.DownloadUpdateAsync(downloadProgress);

        if (!ok)
        {
            _logService.Warning(
                $"Splash: descarga falló ({_updateService.LastError}); se continúa con la versión actual");
            progress.Report(100);
            return;
        }

        SetStatus("Instalando actualización...");
        progress.Report(95);

        if (!_updateService.ApplyAndRestart())
        {
            _logService.Warning(
                $"Splash: no se pudo aplicar ({_updateService.LastError}); se continúa");
        }

        progress.Report(100);
    }

    private async Task CheckSeriesAsync(IProgress<double> progress)
    {
        var series = await _seriesService.GetSeriesAsync();
        _logService.Info($"Splash: {series.Count} series encontradas");
        progress.Report(100);
    }

    private async Task InitializeServicesAsync(IProgress<double> progress)
    {
        var news = await _newsService.GetNewsAsync();
        _logService.Info($"Splash: {news.Count} noticias cargadas");
        progress.Report(100);
        await Task.Yield();
    }

    private async Task LoadResourcesAsync(IProgress<double> progress)
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MineMount");
        Directory.CreateDirectory(appData);

        var root = await _seriesService.GetInstallRootAsync();
        Directory.CreateDirectory(root);

        // Limpieza de temporales antiguos al iniciar
        await _tempCleanupService.CleanupStartupAsync();

        progress.Report(100);
        await Task.Yield();
    }

    private async Task FinalizeAsync(IProgress<double> progress)
    {
        // Paso de cierre: anima suavemente hasta que el llamador decida
        // que terminó (duración mínima global)
        for (var i = 0; i < 10; i++)
        {
            progress.Report(i * 10);
            await Task.Delay(100);
        }
    }

    // ---------------------------------------------------------------
    //  Utilidades de progreso
    // ---------------------------------------------------------------
    private async Task AnimateStepProgress(Func<double> get, Action<double> set, int durationMs)
    {
        var start = get();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < durationMs)
        {
            var t = sw.ElapsedMilliseconds / (double)durationMs;
            set(start + (100 - start) * t);
            await Task.Delay(30);
        }
        set(100);
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
