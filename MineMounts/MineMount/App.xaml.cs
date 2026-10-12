using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MineMount.Services;
using MineMount.ViewModels;
using MineMount.Views;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace MineMount;

public partial class App : Application
{
    private readonly IHost _host;
    private Mutex? _singleInstanceMutex;

    public App()
    {
        DispatcherUnhandledException += OnUnhandledException;

        // Excepciones fuera del hilo UI (Task.Run, callbacks de proceso):
        // se registran; sin esto matan el proceso sin ningún rastro.
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

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

    /// <summary>Log fatal síncrono y seguro (usable desde cualquier hilo).</summary>
    private static void AppendFatalLog(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MineMount");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "minemount.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [FATAL] {message}\n");
        }
        catch
        {
            // Ignorar errores de logging
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppendFatalLog(e.Exception.ToString());

        if (_handlingException)
        {
            // Segunda excepción mientras se mostraba el diálogo de error:
            // se registra y se propaga (la app ya está en estado dudoso;
            // ocultarla dejaría al usuario con una app "viva" rota).
            return;
        }

        _handlingException = true;
        e.Handled = true;

        MessageBox.Show(
            "MineMount encontró un error y debe cerrarse.\n\n" +
            $"{e.Exception.Message}\n\n" +
            "Detalles guardados en: %APPDATA%\\MineMount\\minemount.log",
            "MineMount",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        Shutdown(1);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        AppendFatalLog(e.ExceptionObject?.ToString() ?? "Excepción sin objeto");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppendFatalLog($"Task no observada: {e.Exception}");
        e.SetObserved();
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
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<IGameLauncherService, GameLauncherService>();
        services.AddSingleton<ITempCleanupService, TempCleanupService>();
        services.AddSingleton<IDiskSpaceService, DiskSpaceService>();
        services.AddSingleton<IMinecraftInstallService, MinecraftInstallService>();
        services.AddSingleton<IForgeInstallService, ForgeInstallService>();
        services.AddSingleton<ISoundService, SoundService>();
        services.AddSingleton<IMicrosoftAuthService, MicrosoftAuthService>();
        services.AddSingleton<ISystemInfoService, SystemInfoService>();
        services.AddSingleton<IJavaDownloaderService, JavaDownloaderService>();
        services.AddSingleton<IAppearanceService, AppearanceService>();

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<GamesViewModel>();
        services.AddSingleton<ServersViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<SeriesViewModel>();
        services.AddSingleton<SeriesDetailViewModel>();
        services.AddTransient<WelcomeViewModel>();

        // Views
        services.AddSingleton<MainWindow>();
        services.AddTransient<WelcomeWindow>();
        services.AddTransient<HomeView>();
        services.AddTransient<GamesView>();
        services.AddTransient<ServersView>();
        services.AddTransient<SettingsView>();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Instancia única: dos launchers en paralelo se pisaban cachés,
        // estado de actualización y staging.
        _singleInstanceMutex = new Mutex(true, @"Local\MineMount.SingleInstance", out var isNewInstance);
        if (!isNewInstance)
        {
            ActivateExistingInstance();
            Shutdown();
            return;
        }

        await _host.StartAsync();

        // Cargar configuración antes de construir las vistas (evita carreras de carga)
        await _host.Services.GetRequiredService<ISettingsService>().LoadAsync();

        // Tema e idioma guardados
        await _host.Services.GetRequiredService<IAppearanceService>().ApplyAsync();

        // Sesión guardada (con renovación de token en segundo plano si es Microsoft)
        var auth = _host.Services.GetRequiredService<IAuthService>();
        var restored = await auth.TryRestoreAsync();

        // Bienvenida solo si no hay cuenta guardada
        if (!restored || !auth.IsAuthenticated)
        {
            var welcome = _host.Services.GetRequiredService<Views.WelcomeWindow>();
            welcome.ShowDialog();
            var session = await welcome.ViewModel.Done.Task;
            if (session == null || !auth.IsAuthenticated)
            {
                Shutdown();
                return;
            }
        }

        // Pantalla de carga con tareas reales (10-18 s, sin congelar la UI)
        var splash = new Views.SplashScreen(
            _host.Services.GetRequiredService<ISettingsService>(),
            _host.Services.GetRequiredService<IUpdateService>(),
            _host.Services.GetRequiredService<ISeriesService>(),
            _host.Services.GetRequiredService<INewsService>(),
            _host.Services.GetRequiredService<ILogService>(),
            _host.Services.GetRequiredService<ITempCleanupService>());

        splash.Show();
        await splash.RunAsync();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
        splash.Close();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Cierre síncrono acotado: en OnExit un await real nunca alcanza a
        // ejecutarse (WPF apaga el dispatcher justo después), y las
        // excepciones de StopAsync en async void se perdían en silencio.
        try
        {
            _host.StopAsync().Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            AppendFatalLog($"Error al detener el host: {ex}");
        }

        try
        {
            _host.Dispose();
        }
        catch (Exception ex)
        {
            AppendFatalLog($"Error al disposear el host: {ex}");
        }

        try
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch
        {
            // El mutex ya pudo liberarse por abandono.
        }

        base.OnExit(e);
    }

    /// <summary>Trayende al frente la instancia que ya está corriendo.</summary>
    private static void ActivateExistingInstance()
    {
        try
        {
            var current = Environment.ProcessId;
            foreach (var p in Process.GetProcessesByName("MineMount"))
            {
                try
                {
                    if (p.Id == current) continue;
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        NativeMethods.ShowWindow(p.MainWindowHandle, NativeMethods.SW_RESTORE);
                        NativeMethods.SetForegroundWindow(p.MainWindowHandle);
                        break;
                    }
                }
                catch
                {
                    // Proceso que terminó entre enumeración y acceso.
                }
            }
        }
        catch
        {
            // Activación best-effort.
        }
    }

    private static class NativeMethods
    {
        public const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }

    public static T GetService<T>() where T : notnull
    {
        return ((App)Current)._host.Services.GetRequiredService<T>();
    }
}