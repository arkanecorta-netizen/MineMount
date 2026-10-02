using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MineMount.Services;
using MineMount.ViewModels;
using MineMount.Views;
using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace MineMount;

public partial class App : Application
{
    private readonly IHost _host;

    public App()
    {
        DispatcherUnhandledException += OnUnhandledException;

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                ConfigureServices(services);
            })
            .ConfigureLogging(logging =>
            {
                logging.AddDebug();
                logging.SetMinimumLevel(LogLevel.Debug);
            })
            .Build();
    }

    private bool _handlingException;

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MineMount");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "minemount.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [FATAL] {e.Exception}\n");
        }
        catch
        {
            // Ignorar errores de logging
        }

        if (_handlingException)
        {
            return;
        }

        _handlingException = true;

        MessageBox.Show(
            "MineMount encontró un error y debe cerrarse.\n\n" +
            $"{e.Exception.Message}\n\n" +
            "Detalles guardados en: %APPDATA%\\MineMount\\minemount.log",
            "MineMount",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        Shutdown(1);
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // Services
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<IMinecraftService, MinecraftService>();
        services.AddSingleton<IModpackService, ModpackService>();
        services.AddSingleton<IServerService, ServerService>();
        services.AddSingleton<INewsService, NewsService>();
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<IProfileService, ProfileService>();
        services.AddSingleton<ILogService, LogService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<ISeriesStorageService, SeriesStorageService>();
        services.AddSingleton<ISeriesCatalogService, SeriesCatalogService>();
        services.AddSingleton<ISeriesResourceService, SeriesResourceService>();
        services.AddSingleton<ISeriesInstallService, SeriesInstallService>();
        services.AddSingleton<ISeriesService, SeriesService>();

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<GamesViewModel>();
        services.AddSingleton<ServersViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<SeriesViewModel>();
        services.AddSingleton<SeriesDetailViewModel>();

        // Views
        services.AddSingleton<MainWindow>();
        services.AddTransient<HomeView>();
        services.AddTransient<GamesView>();
        services.AddTransient<ServersView>();
        services.AddTransient<SettingsView>();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        await _host.StartAsync();

        // Cargar configuración antes de construir las vistas (evita carreras de carga)
        await _host.Services.GetRequiredService<ISettingsService>().LoadAsync();

        // Pantalla de carga con tareas reales (10-18 s, sin congelar la UI)
        var splash = new Views.SplashScreen(
            _host.Services.GetRequiredService<ISettingsService>(),
            _host.Services.GetRequiredService<IUpdateService>(),
            _host.Services.GetRequiredService<ISeriesService>(),
            _host.Services.GetRequiredService<INewsService>(),
            _host.Services.GetRequiredService<ILogService>());

        splash.Show();
        await splash.RunAsync();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
        splash.Close();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await _host.StopAsync();
        _host.Dispose();
        base.OnExit(e);
    }

    public static T GetService<T>() where T : notnull
    {
        return ((App)Current)._host.Services.GetRequiredService<T>();
    }
}