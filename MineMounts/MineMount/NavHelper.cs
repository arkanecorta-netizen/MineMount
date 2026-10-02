using System.Windows;

namespace MineMount;

public static class NavHelper
{
    public static readonly DependencyProperty SelectedProperty =
        DependencyProperty.RegisterAttached(
            "Selected",
            typeof(double),
            typeof(NavHelper),
            new PropertyMetadata(0.0));

    public static double GetSelected(DependencyObject obj)
    {
        return (double)obj.GetValue(SelectedProperty);
    }

    public static void SetSelected(DependencyObject obj, double value)
    {
        obj.SetValue(SelectedProperty, value);
    }
}
