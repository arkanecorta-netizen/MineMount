using System.Windows;
using System.Windows.Input;
using MineMount.ViewModels;

namespace MineMount.Views;

public partial class WelcomeWindow : Window
{
    public WelcomeViewModel ViewModel { get; }

    public WelcomeWindow(WelcomeViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
        Closed += (_, _) => ViewModel.CloseWithoutLogin();
        ViewModel.Done.Task.ContinueWith(_ =>
        {
            Dispatcher.Invoke(Close);
        }, System.Threading.Tasks.TaskScheduler.Default);
    }
}
