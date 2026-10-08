using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MineMount.Services;
using MineMount.ViewModels;

namespace MineMount;

public partial class MainWindow : Window
{
    private const double CornerRadius = 12;
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int MONITOR_DEFAULTTONEAREST = 2;

    private MainViewModel? _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        StateChanged += OnStateChanged;
        SizeChanged += OnSizeChanged;
        SourceInitialized += OnSourceInitialized;
        Activated += (_, _) => SetWindowActive(true);
        Deactivated += (_, _) => SetWindowActive(false);

        // Sonido de click suave en botones (desactivable desde Configuración)
        PreviewMouseLeftButtonDown += OnPreviewClick;

        Loaded += (_, _) =>
        {
            RestoreWindowBounds();
            ApplyAppearance();
            ApplyResponsiveLayout();
            FadeInCurrentPage();
        };
        Closing += (_, _) => SaveWindowBounds();

        UpdateWindowShape();
    }

    private void SetWindowActive(bool active)
    {
        if (_viewModel != null) _viewModel.WindowActive = active;
        Backdrop.IsPaused = !active;
        ParticlesHost.SetActive(_viewModel?.ParticlesActive ?? false);
    }

    private void OnPreviewClick(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? node = e.OriginalSource as DependencyObject;
        while (node != null && node is not System.Windows.Controls.Button)
            node = VisualTreeHelper.GetParent(node);

        if (node is System.Windows.Controls.Button)
        {
            try
            {
                App.GetService<ISoundService>().PlayClick();
            }
            catch
            {
                // Sonido decorativo: nunca debe romper un click.
            }
        }
    }

    private void ApplyAppearance()
    {
        if (_viewModel == null) return;
        Backdrop.Configure(
            _viewModel.BackgroundMode,
            _viewModel.SelectedBackground,
            _viewModel.EnableAnimations,
            _viewModel.BackgroundInterval,
            _viewModel.BackgroundDim,
            _viewModel.BackgroundBlur,
            _viewModel.CustomBackgrounds);
        Backdrop.IsPaused = !_viewModel.WindowActive;
        ParticlesHost.SetActive(_viewModel.ParticlesActive);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(handle);
        source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);

            if (monitor != IntPtr.Zero)
            {
                var mi = new MonitorInfo { cbSize = Marshal.SizeOf(typeof(MonitorInfo)) };

                if (GetMonitorInfo(monitor, ref mi))
                {
                    mmi.ptMaxPosition.x = mi.rcWork.left - mi.rcMonitor.left;
                    mmi.ptMaxPosition.y = mi.rcWork.top - mi.rcMonitor.top;
                    mmi.ptMaxSize.x = mi.rcWork.right - mi.rcWork.left;
                    mmi.ptMaxSize.y = mi.rcWork.bottom - mi.rcWork.top;
                    Marshal.StructureToPtr(mmi, lParam, false);
                    handled = true;
                }
            }
        }

        return IntPtr.Zero;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentPageViewModel))
        {
            FadeInCurrentPage();
        }
        else if (e.PropertyName == nameof(MainViewModel.BackgroundMode)
            || e.PropertyName == nameof(MainViewModel.SelectedBackground)
            || e.PropertyName == nameof(MainViewModel.BackgroundInterval)
            || e.PropertyName == nameof(MainViewModel.BackgroundDim)
            || e.PropertyName == nameof(MainViewModel.BackgroundBlur)
            || e.PropertyName == nameof(MainViewModel.CustomBackgrounds)
            || e.PropertyName == nameof(MainViewModel.EnableAnimations))
        {
            ApplyAppearance();
        }
        else if (e.PropertyName == nameof(MainViewModel.ParticlesActive))
        {
            ParticlesHost.SetActive(_viewModel?.ParticlesActive ?? false);
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.IsMaximized = WindowState == WindowState.Maximized;
        }

        UpdateWindowShape();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateWindowShape();
        ApplyResponsiveLayout();
    }

    /// <summary>
    /// Layout fluido: al achicar, primero se ocultan las promos, luego se
    /// compacta el selector; el botón JUGAR siempre queda visible.
    /// </summary>
    private void ApplyResponsiveLayout()
    {
        if (PromoScroller == null || VersionBox == null) return;

        var w = ActualWidth;
        if (_viewModel != null) _viewModel.PromosCompact = w < 1100;

        if (w < 1020)
        {
            VersionBox.Width = 150;
            VersionLabel.Visibility = Visibility.Collapsed;
            VersionPanel.Margin = new Thickness(10, 0, 0, 26);
        }
        else
        {
            VersionBox.Width = 220;
            VersionLabel.Visibility = Visibility.Visible;
            VersionPanel.Margin = new Thickness(14, 0, 0, 26);
        }
    }

    /// <summary>Restaura tamaño, posición y estado guardados (validados a pantalla).</summary>
    private void RestoreWindowBounds()
    {
        try
        {
            var s = App.GetService<ISettingsService>().GetSettingsAsync().GetAwaiter().GetResult();

            var vw = SystemParameters.VirtualScreenWidth;
            var vh = SystemParameters.VirtualScreenHeight;
            var vx = SystemParameters.VirtualScreenLeft;
            var vy = SystemParameters.VirtualScreenTop;

            if (s.WindowWidth >= MinWidth && s.WindowWidth <= vw) Width = s.WindowWidth;
            if (s.WindowHeight >= MinHeight && s.WindowHeight <= vh) Height = s.WindowHeight;

            var left = double.IsNaN(s.WindowLeft) ? Left : s.WindowLeft;
            var top = double.IsNaN(s.WindowTop) ? Top : s.WindowTop;
            if (left >= vx - 40 && left <= vx + vw - 100
                && top >= vy - 20 && top <= vy + vh - 100)
            {
                Left = left;
                Top = top;
            }

            if (s.WindowMaximized) WindowState = WindowState.Maximized;
        }
        catch
        {
            // Geometría inválida: se usan los valores por defecto del XAML.
        }
    }

    /// <summary>Guarda tamaño, posición y estado al cerrar (sin bloquear la UI).</summary>
    private void SaveWindowBounds()
    {
        try
        {
            // Lecturas sincrónicas en el hilo UI; el IO va a un hilo de fondo
            // (Task.Run evita el deadlock del contexto de sincronización).
            var maximized = WindowState == WindowState.Maximized;
            var width = ActualWidth;
            var height = ActualHeight;
            var left = Left;
            var top = Top;

            System.Threading.Tasks.Task.Run(async () =>
            {
                var service = App.GetService<ISettingsService>();
                var s = await service.GetSettingsAsync();

                s.WindowMaximized = maximized;
                if (!maximized)
                {
                    s.WindowWidth = width;
                    s.WindowHeight = height;
                    s.WindowLeft = left;
                    s.WindowTop = top;
                }

                await service.SaveSettingsAsync(s);
            }).GetAwaiter().GetResult();
        }
        catch
        {
            // No bloquear el cierre por no poder guardar la geometría.
        }
    }

    private void UpdateWindowShape()
    {
        bool isNormal = WindowState == WindowState.Normal;

        RootBorder.CornerRadius = isNormal
            ? new CornerRadius(CornerRadius)
            : new CornerRadius(0);

        SidebarBorder.CornerRadius = isNormal
            ? new CornerRadius(CornerRadius, 0, 0, CornerRadius)
            : new CornerRadius(0);

        if (isNormal && ActualWidth > 0 && ActualHeight > 0)
        {
            Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), CornerRadius, CornerRadius);
        }
        else
        {
            Clip = null;
        }
    }

    /// <summary>
    /// Entrada escalonada al cambiar de pestaña: fade + slide de 12px (GPU).
    /// </summary>
    private void FadeInCurrentPage()
    {
        if (PageHost == null) return;
        if (_viewModel != null && !_viewModel.EnableAnimations) return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(250);

        PageHost.Opacity = 0;
        PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });

        if (PageSlide != null)
        {
            PageSlide.Y = 12;
            PageSlide.BeginAnimation(
                System.Windows.Media.TranslateTransform.YProperty,
                new DoubleAnimation(12, 0, duration) { EasingFunction = ease });
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;
            else
                WindowState = WindowState.Maximized;
        }
        else if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointInt
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public PointInt ptReserved;
        public PointInt ptMaxSize;
        public PointInt ptMaxPosition;
        public PointInt ptMinTrackSize;
        public PointInt ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectInt
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public RectInt rcMonitor;
        public RectInt rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);
}
