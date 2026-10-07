using System.Windows;
using System.Windows.Controls;

namespace MineMount.Views;

public partial class HomeView : UserControl
{
    private const double ScrollStep = 300;

    public HomeView()
    {
        InitializeComponent();
    }

    private void ScrollNewsLeft(object sender, RoutedEventArgs e)
    {
        NewsScroller?.ScrollToHorizontalOffset(NewsScroller.HorizontalOffset - ScrollStep);
    }

    private void ScrollNewsRight(object sender, RoutedEventArgs e)
    {
        NewsScroller?.ScrollToHorizontalOffset(NewsScroller.HorizontalOffset + ScrollStep);
    }
}
