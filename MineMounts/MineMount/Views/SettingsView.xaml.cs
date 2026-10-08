using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MineMount.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    /// <summary>Navegación fija de secciones: hace scroll hasta cada tarjeta.</summary>
    private void Section_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string name) return;

        var target = FindName(name) as FrameworkElement;
        if (target == null || SectionScroller == null) return;

        try
        {
            var point = target.TransformToAncestor(SectionScroller)
                .Transform(new Point(0, 0));
            SectionScroller.ScrollToVerticalOffset(
                SectionScroller.VerticalOffset + point.Y - 8);
        }
        catch
        {
            // Scroll fuera de rango: se ignora
        }
    }
}
