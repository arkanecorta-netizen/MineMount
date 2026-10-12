using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MineMount;
using MineMount.Services;
using MineMount.ViewModels;

// Test del camino REAL del click en JUGAR:
// misma App (recursos XAML + DI), mismo MainWindow, mismo MainViewModel,
// mismos comandos que ejecutan los botones de la UI.
public static class Program
{
    private static int _failures;

    public static int Main(string[] args)
    {
        // async Main ignora [STAThread]: hilo STA explícito con Dispatcher
        // bombeando (sin esto, los await migran a MTA y WPF explota).
        int code = 1;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var app = new MineMount.App();
                app.InitializeComponent();
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var task = MainAsync(app, args);
                task.ContinueWith(_ =>
                {
                    if (_.IsFaulted) error = _.Exception;
                    else code = _.Result;
                    Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
                }, TaskScheduler.Default);
                Dispatcher.Run();
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) Console.WriteLine("FATAL: " + error);
        return code;
    }

    private static async Task<int> MainAsync(MineMount.App app, string[] args)
    {
        Console.WriteLine("Apartment: " + Thread.CurrentThread.GetApartmentState());

        var settings = App.GetService<ISettingsService>();
        await settings.LoadAsync();

        var mainVm = App.GetService<MainViewModel>();
        MainWindow window;
        try
        {
            window = App.GetService<MainWindow>();
            try { window.Show(); Console.WriteLine("Ventana principal mostrada."); }
            catch (Exception ex) { Console.WriteLine($"Ventana no visible (headless): {ex.GetType().Name}. Sigo con comandos."); }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR creando MainWindow: {ex}");
            return 1;
        }

        // T0: diagnóstico de la ventana real
        RunOnUi(() =>
        {
            var main = Application.Current.MainWindow;
            Console.WriteLine($"MainWindow es null: {main == null}");
            Console.WriteLine($"MainWindow tipo: {main?.GetType().FullName}");
            Console.WriteLine($"Ventanas abiertas: {Application.Current.Windows.Count}");
            foreach (Window w in Application.Current.Windows)
                Console.WriteLine($"  - {w.GetType().FullName} Visible={w.IsVisible}");
            Console.WriteLine($"MainWindow es nuestra ventana: {ReferenceEquals(main, window)}");
            Console.WriteLine($"IsVisible={window.IsVisible} IsLoaded={window.IsLoaded} Handle=0x{new System.Windows.Interop.WindowInteropHelper(window).Handle.ToInt64():X}");
            window.WindowState = WindowState.Minimized;
            Console.WriteLine($"Set directo a Minimized → {window.WindowState}");
            window.WindowState = WindowState.Normal;
            Console.WriteLine($"Set directo a Normal → {window.WindowState}");
        });

        // T1: botones de ventana (minimizar / maximizar-restaurar)
        RunOnUi(() => mainVm.MinimizeCommand.Execute(null));
        Check("MinimizeCommand minimiza", true,
            () => window.WindowState == WindowState.Minimized);
        RunOnUi(() => { window.WindowState = WindowState.Normal; });

        RunOnUi(() => mainVm.MaximizeRestoreCommand.Execute(null));
        Check("MaximizeRestoreCommand maximiza", true,
            () => window.WindowState == WindowState.Maximized && mainVm.IsMaximized);
        RunOnUi(() => mainVm.MaximizeRestoreCommand.Execute(null));
        Check("MaximizeRestoreCommand restaura", true,
            () => window.WindowState == WindowState.Normal && !mainVm.IsMaximized);

        if (args.Any(a => a == "quick"))
        {
            Console.WriteLine(_failures == 0 ? "ALL PASS" : $"{_failures} FAILURES");
            return _failures == 0 ? 0 : 1;
        }

        // T2: click JUGAR real (PlayPrimaryCommand) en la serie NSE6
        await mainVm.RefreshPlaySeriesAsync();
        var series = mainVm.AvailableSeries.FirstOrDefault(s => s.Id == "NSE6")
                     ?? mainVm.AvailableSeries.FirstOrDefault();
        Console.WriteLine($"Serie seleccionada: {series?.Id} status={series?.Status}");
        if (series == null)
        {
            Console.WriteLine("FAIL: no hay series.");
            return 1;
        }
        RunOnUi(() => { mainVm.SelectedPlaySeries = series; });
        Console.WriteLine($"CanPlayPrimary={mainVm.CanPlayPrimary} PlayButtonText={mainVm.PlayButtonText}");

        Console.WriteLine("Ejecutando PlayPrimaryCommand (equivale al click en JUGAR)...");
        var clickTask = mainVm.PlayPrimaryCommand.ExecuteAsync(null);
        // Watchdog anti-freeze: si el comando no termina en 12 min, la UI se trabó.
        var finished = await Task.WhenAny(clickTask, Task.Delay(TimeSpan.FromMinutes(12)));
        if (!clickTask.IsCompleted)
        {
            Console.WriteLine("FAIL: PlayPrimaryCommand no terminó en 12 min (UI trabada).");
            _failures++;
            return 1;
        }
        await clickTask;
        Console.WriteLine($"Post-click: IsGameRunning={mainVm.IsGameRunning} PlayButtonText={mainVm.PlayButtonText} HasError={mainVm.PlayHasError}");

        if (mainVm.PlayHasError)
        {
            Console.WriteLine($"FAIL: error tras el click. Detalle: {mainVm.PlayStatusDetail}");
            _failures++;
        }
        else if (mainVm.IsGameRunning)
        {
            Console.WriteLine("PASS: proceso del juego iniciado desde el comando del botón (sin trabas).");
            // Esperar a que el juego arranque del todo y salga (headless cierra solo)
            var deadline = DateTime.UtcNow.AddMinutes(15);
            while (mainVm.IsGameRunning && DateTime.UtcNow < deadline)
                await Task.Delay(5000);
            Console.WriteLine($"IsGameRunning={mainVm.IsGameRunning} (fin de espera)");
            var logPath = Path.Combine(series.InstallPath, "logs", "latest.log");
            if (File.Exists(logPath))
            {
                var tail = File.ReadLines(logPath).TakeLast(3);
                foreach (var l in tail) Console.WriteLine("LOG: " + l.Trim());
                var full = File.ReadAllText(logPath);
                Check("log muestra boot de ModLauncher", true, () => full.Contains("ModLauncher running"));
            }
        }
        else
        {
            // Rama instalar/actualizar/reparar: debe terminar sin error y sin trabarse.
            await mainVm.RefreshPlaySeriesAsync();
            var after = mainVm.AvailableSeries.FirstOrDefault(s => s.Id == series.Id);
            Console.WriteLine($"Post-operación: status={after?.Status} version={after?.Version}");
            Check("operación (instalar/actualizar) terminó sin error ni trabas", true,
                () => after != null && after.Status == MineMount.Models.SeriesStatus.Installed);
        }

        // T3: comandos de tarjeta (⋯ Reparar / Abrir carpeta)
        var seriesVm = App.GetService<SeriesViewModel>();
        await seriesVm.RefreshAsync();
        var card = seriesVm.Cards.FirstOrDefault(c => c.Info.Id == "NSE6");
        Check("OpenFolderCommand.CanExecute(card)", card != null,
            () => card != null && seriesVm.OpenFolderCommand.CanExecute(card));
        Check("RepairCardCommand.CanExecute(card)", card != null,
            () => card != null && seriesVm.RepairCardCommand.CanExecute(card));

        // T4: "REINTENTAR" cabe en el botón (medición real de texto)
        CheckTextFits(app);

        Console.WriteLine(_failures == 0 ? "ALL PASS" : $"{_failures} FAILURES");
        return _failures == 0 ? 0 : 1;
    }

    private static void RunOnUi(Action a)
    {
        if (Application.Current?.Dispatcher.CheckAccess() == true) a();
        else Application.Current?.Dispatcher.Invoke(a);
    }

    private static void Check(string name, bool setup, Func<bool> assert)
    {
        bool ok;
        try { ok = setup && assert(); }
        catch (Exception ex) { Console.WriteLine($"FAIL: {name} ({ex.GetType().Name}: {ex.Message})"); _failures++; return; }
        Console.WriteLine((ok ? "PASS: " : "FAIL: ") + name);
        if (!ok) _failures++;
    }

    private static void CheckTextFits(System.Windows.Application app)
    {
        try
        {
            var fontFamily = app.Resources["FontFamilyDisplay"] as FontFamily
                ?? new FontFamily("Segoe UI");
            // Botón 232 - padding 28*2 - icono 26 - margen 12 = 138px útiles.
            // El Viewbox DownOnly achica el texto si excede; se exige escala >= 0.7
            // para que siga legible.
            foreach (var text in new[] { "REINTENTAR", "PRÓXIMAMENTE", "ACTUALIZAR", "RETRY", "COMING SOON" })
            {
                var typeface = new Typeface(fontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
                var ft = new FormattedText(text, CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, typeface, 22.0, Brushes.Black, 1.0);
                var scale = Math.Min(1.0, 138.0 / ft.Width);
                var ok = scale >= 0.7;
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")}: '{text}' mide {ft.Width:F0}px → escala {scale:F2} (mín 0.70)");
                if (!ok) _failures++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: medición de texto ({ex.Message})");
            _failures++;
        }
    }
}
