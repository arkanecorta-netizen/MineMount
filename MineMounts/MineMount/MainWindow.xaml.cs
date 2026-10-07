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
            ApplyAppearance();
            FadeInCurrentPage();
        };

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
        Backdrop.Configure(_viewModel.BackgroundMode, _viewModel.SelectedBackground, _viewModel.EnableAnimations);
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
