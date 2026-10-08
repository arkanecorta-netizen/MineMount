using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MineMount.Views;

/// <summary>
/// Fondo a pantalla completa con rotación automática y crossfade suave:
/// ~8s por imagen, transición de 1.5s, Ken Burns (zoom 1.0 → 1.08).
/// Modos: "Auto" (rotación), "Fijo" (una imagen, sin animación) y
/// "Desactivado" (color sólido, para PCs flojas). Se pausa con IsPaused
/// (la ventana pierde el foco) y respeta EnableAnimations.
/// Todo el catálogo es local (Assets/Backgrounds) para funcionar offline,
/// con fallback a color sólido si una imagen falla.
/// </summary>
public partial class AnimatedBackground : UserControl
{
    private static readonly Duration Crossfade = new(TimeSpan.FromSeconds(1.5));

    private readonly DispatcherTimer _timer;
    private readonly Random _random = new();
    private readonly List<Uri> _playlist = new();
    private int _current = -1;
    private bool _showingB;
    private bool _isPaused;
    private bool _animationsEnabled = true;
    private string _mode = "Auto";
    private int _fixedIndex;
    private int _intervalSeconds = 8;

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            _isPaused = value;
            UpdateTimer();
        }
    }

    public bool AnimationsEnabled
    {
        get => _animationsEnabled;
        set
        {
            _animationsEnabled = value;
            UpdateTimer();
        }
    }

    public AnimatedBackground()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _timer.Tick += (_, _) => Advance();
        Loaded += (_, _) => ApplyMode(initial: true);
        Unloaded += (_, _) => _timer.Stop();
    }

    /// <summary>Configura el fondo desde los ajustes (todo en vivo).</summary>
    public void Configure(
        string mode, int fixedIndex, bool animationsEnabled,
        int intervalSeconds, int dimPercent, int blurRadius,
        System.Collections.Generic.IEnumerable<string>? customBackgrounds)
    {
        _mode = string.IsNullOrWhiteSpace(mode) ? "Auto" : mode;
        _fixedIndex = Math.Max(0, fixedIndex);
        _animationsEnabled = animationsEnabled;
        _intervalSeconds = Math.Clamp(intervalSeconds, 2, 20);
        _timer.Interval = TimeSpan.FromSeconds(_intervalSeconds);

        DimLayer.Opacity = Math.Clamp(dimPercent, 0, 80) / 100.0;

        // Sin blur no se aplica efecto (un BlurEffect en 0 igual cuesta GPU)
        var blur = Math.Clamp(blurRadius, 0, 20);
        ImageA.Effect = blur > 0 ? BlurA : null;
        ImageB.Effect = blur > 0 ? BlurB : null;
        BlurA.Radius = blur;
        BlurB.Radius = blur;

        if (_playlist.Count == 0)
            RebuildPlaylist(customBackgrounds);
        if (!IsLoaded) return;
        ApplyMode(initial: false);
    }

    /// <summary>Compatibilidad: configuración básica.</summary>
    public void Configure(string mode, int fixedIndex, bool animationsEnabled)
        => Configure(mode, fixedIndex, animationsEnabled, 8, 25, 0, null);

    private void RebuildPlaylist(System.Collections.Generic.IEnumerable<string>? customs)
    {
        _playlist.Clear();
        foreach (var item in Services.BackgroundCatalog.Items)
        {
            try
            {
                _playlist.Add(new Uri(item.PackUri, UriKind.Absolute));
            }
            catch
            {
                // Entrada inválida: se saltea
            }
        }

        if (customs != null)
        {
            foreach (var path in customs)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                    if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".bmp" && ext != ".webp")
                        continue;
                    if (!System.IO.File.Exists(path)) continue;
                    _playlist.Add(new Uri(path, UriKind.Absolute));
                }
                catch
                {
                    // Imagen propia inválida: se saltea
                }
            }
        }

        if (_playlist.Count == 0)
        {
            try
            {
                _playlist.Add(new Uri("pack://application:,,,/Assets/MineMount/banner.png", UriKind.Absolute));
            }
            catch
            {
                // Sin imágenes: queda el color sólido
            }
        }
    }

    private Uri PlaylistUriAt(int index)
    {
        if (_playlist.Count == 0)
            return Services.BackgroundCatalog.UriAt(0);
        var safe = ((index % _playlist.Count) + _playlist.Count) % _playlist.Count;
        return _playlist[safe];
    }

    private void ApplyMode(bool initial)
    {
        var mode = (_mode ?? "Auto").Trim();

        if (string.Equals(mode, "Desactivado", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "Disabled", StringComparison.OrdinalIgnoreCase))
        {
            // PCs flojas: solo el color sólido, sin imágenes ni animaciones.
            _timer.Stop();
            ImageA.Source = null;
            ImageB.Source = null;
            ImageA.Opacity = 0;
            ImageB.Opacity = 0;
            return;
        }

        if (string.Equals(mode, "Fijo", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "Fixed", StringComparison.OrdinalIgnoreCase))
        {
            _timer.Stop();
            ShowFixed(_fixedIndex);
            return;
        }

        // Rotación automática
        if (initial || _current < 0)
        {
            if (_playlist.Count == 0)
                RebuildPlaylist(null);
            _current = _playlist.Count > 0 ? _random.Next(_playlist.Count) : 0;
            LoadBitmapAsync(PlaylistUriAt(_current), bitmap =>
            {
                if (bitmap == null) return;
                ImageA.Source = bitmap;
                ImageA.Opacity = 1;
                ImageB.Opacity = 0;
            });
            _showingB = false;
            if (_animationsEnabled) StartKenBurns(ZoomA);
        }
        UpdateTimer();
    }

    private void UpdateTimer()
    {
        var auto = !string.Equals(_mode, "Desactivado", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(_mode, "Off", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(_mode, "Fijo", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(_mode, "Fixed", StringComparison.OrdinalIgnoreCase);
        if (auto && !_isPaused && _animationsEnabled && IsLoaded)
            _timer.Start();
        else
            _timer.Stop();
    }

    private void ShowFixed(int index)
    {
        if (_playlist.Count == 0) RebuildPlaylist(null);
        _current = _playlist.Count > 0 ? index % _playlist.Count : 0;
        _showingB = false;
        var uri = PlaylistUriAt(_current);
        LoadBitmapAsync(uri, bitmap =>
        {
            if (bitmap == null) return;
            ImageA.Source = bitmap;
            ImageB.Opacity = 0;
            ImageA.Opacity = 1;
        });
        // Sin animación en modo fijo: zoom leve estático (barato, sin storyboard).
        ZoomA.ScaleX = 1.04;
        ZoomA.ScaleY = 1.04;
    }

    private void Advance()
    {
        if (_isPaused || !_animationsEnabled || _playlist.Count == 0) return;

        var next = (_current + 1) % _playlist.Count;
        _current = next;

        var fadeIn = _showingB ? ImageA : ImageB;
        var fadeOut = _showingB ? ImageB : ImageA;
        var zoomIn = _showingB ? ZoomA : ZoomB;

        LoadBitmapAsync(PlaylistUriAt(next), bitmap =>
        {
            if (bitmap == null) return; // falla: queda la actual + color sólido detrás
            fadeIn.Source = bitmap;

            var fade = new DoubleAnimation(0, 1, Crossfade)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            var hide = new DoubleAnimation(1, 0, Crossfade)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            fadeIn.BeginAnimation(OpacityProperty, fade);
            fadeOut.BeginAnimation(OpacityProperty, hide);
            StartKenBurns(zoomIn);
        });

        _showingB = !_showingB;
    }

    private void StartKenBurns(System.Windows.Media.ScaleTransform zoom)
    {
        if (!_animationsEnabled) return;
        zoom.ScaleX = 1.0;
        zoom.ScaleY = 1.0;
        var duration = new Duration(TimeSpan.FromSeconds(_intervalSeconds));
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
        zoom.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1.0, 1.08, duration) { EasingFunction = easing });
        zoom.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1.0, 1.08, duration) { EasingFunction = easing });
    }

    /// <summary>
    /// Decodifica en un hilo de fondo (1600px para RAM/lag) y entrega el
    /// bitmap congelado en el hilo UI. Sin esto, cambiar de fondo traba la UI.
    /// </summary>
    private void LoadBitmapAsync(Uri uri, Action<System.Windows.Media.ImageSource?> done)
    {
        System.Threading.Tasks.Task.Run<System.Windows.Media.ImageSource?>(() =>
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = uri;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 1600;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }).ContinueWith(t =>
        {
            try
            {
                done(t.Result);
            }
            catch
            {
                // Ventana cerrada en el medio: se ignora
            }
        }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
    }
}
