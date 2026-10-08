using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace MineMount.Views;

/// <summary>
/// Polvo/brasas moradas flotando, muy discretas (20–30 partículas, baja opacidad).
/// Todo por GPU (TranslateTransform + Opacity, BitmapCache) y desactivable.
/// </summary>
public partial class ParticlesCanvas : UserControl
{
    private const int ParticleCount = 14;
    private readonly Random _random = new();
    private bool _built;

    private static readonly Color[] Tints =
    {
        Color.FromRgb(0xA8, 0x55, 0xF7), // morado
        Color.FromRgb(0xE8, 0x79, 0xA9), // rosa
        Color.FromRgb(0x22, 0xD3, 0xEE), // cian (pocas)
    };

    public ParticlesCanvas()
    {
        InitializeComponent();
        Loaded += (_, _) => EnsureBuilt();
        SizeChanged += (_, _) => LayoutParticles();
    }

    /// <summary>Muestra u oculta las partículas (ajuste + foco de ventana).</summary>
    public void SetActive(bool active)
    {
        Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        IsEnabled = active;
    }

    private void EnsureBuilt()
    {
        if (_built) return;
        _built = true;

        for (var i = 0; i < ParticleCount; i++)
        {
            var tint = Tints[_random.Next(Tints.Length)];
            var size = 1.5 + _random.NextDouble() * 2.5;
            var fill = new SolidColorBrush(Color.FromArgb(
                (byte)(30 + _random.Next(55)), tint.R, tint.G, tint.B));
            if (fill.CanFreeze) fill.Freeze();
            var ellipse = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = fill,
                Opacity = 0,
                CacheMode = new BitmapCache(),
                RenderTransform = new TranslateTransform(),
                IsHitTestVisible = false,
                Focusable = false
            };
            Host.Children.Add(ellipse);
            AnimateParticle(ellipse, i);
        }

        LayoutParticles();
    }

    private void LayoutParticles()
    {
        if (!_built || Host.ActualWidth <= 0) return;
        foreach (UIElement child in Host.Children)
        {
            Canvas.SetLeft(child, _random.NextDouble() * Host.ActualWidth);
            Canvas.SetTop(child, _random.NextDouble() * Math.Max(Host.ActualHeight, 1));
        }
    }

    private void AnimateParticle(Ellipse ellipse, int seed)
    {
        var transform = (TranslateTransform)ellipse.RenderTransform;
        var rise = 40 + _random.NextDouble() * 110;
        var duration = TimeSpan.FromSeconds(6 + _random.NextDouble() * 8);
        var delay = TimeSpan.FromSeconds(_random.NextDouble() * 8);
        var peakOpacity = 0.10 + _random.NextDouble() * 0.22;

        var drift = new DoubleAnimation(0, -rise, new Duration(duration))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            AutoReverse = true,
            BeginTime = delay,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        var sway = new DoubleAnimation(-8 - _random.NextDouble() * 10, 8 + _random.NextDouble() * 10,
            new Duration(TimeSpan.FromSeconds(duration.TotalSeconds * 0.7)))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            AutoReverse = true,
            BeginTime = delay
        };
        var twinkle = new DoubleAnimation(0, peakOpacity, new Duration(TimeSpan.FromSeconds(2 + _random.NextDouble() * 2)))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            AutoReverse = true,
            BeginTime = delay
        };

        transform.BeginAnimation(TranslateTransform.YProperty, drift);
        transform.BeginAnimation(TranslateTransform.XProperty, sway);
        ellipse.BeginAnimation(OpacityProperty, twinkle);
        _ = seed;
    }
}
