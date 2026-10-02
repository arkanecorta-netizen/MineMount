using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MineMount.ViewModels;

namespace MineMount;

public partial class MainWindow : Window
{
    private const double CornerRadius = 12;
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int MONITOR_DEFAULTTONEAREST = 2;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        StateChanged += OnStateChanged;
        SizeChanged += OnSizeChanged;
        SourceInitialized += OnSourceInitialized;
        UpdateWindowShape();
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

    private void FadeInCurrentPage()
    {
        if (PageHost == null) return;

        PageHost.Opacity = 0;
        var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        PageHost.BeginAnimation(UIElement.OpacityProperty, animation);
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
