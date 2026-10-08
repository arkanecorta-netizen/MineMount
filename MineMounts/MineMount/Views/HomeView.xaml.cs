using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace MineMount.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        Loaded += (_, _) => PlayEntrance();
    }

    /// <summary>Entrada fade + slide de 12px (se omite si las animaciones están off).</summary>
    private void PlayEntrance()
    {
        bool enabled = true;
        try
        {
            enabled = App.GetService<Services.ISettingsService>()
                .GetSettingsAsync().GetAwaiter().GetResult().EnableAnimations;
        }
        catch
        {
            // Sin configuración: animar igual
        }

        if (!enabled || HeroPanel == null) return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(300);
        HeroPanel.Opacity = 0;
        HeroPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        HeroSlide.Y = 12;
        HeroSlide.BeginAnimation(
            System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(12, 0, duration) { EasingFunction = ease });
    }
}
