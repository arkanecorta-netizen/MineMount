using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace MineMount.Converters;

public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b ? !b : value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b ? !b : value;
    }
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Visibility v && v == Visibility.Visible;
    }
}

public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Visibility v && v != Visibility.Visible;
    }
}

public class BoolToStatusColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true
            ? new SolidColorBrush(Color.FromRgb(76, 175, 80))
            : new SolidColorBrush(Color.FromRgb(244, 67, 54));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class FirstLetterConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value as string;
        return string.IsNullOrWhiteSpace(text) ? "?" : char.ToUpperInvariant(text[0]).ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Etiquetas de categoría con colores distintos:
/// Serie = morado, Anuncio = azul, Actualización = verde, resto = cian/gris.
/// Devuelve el color de texto (siempre claro para contraste WCAG AA sobre tinte oscuro).
/// </summary>
public class CategoryToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var category = (value as string)?.Trim() ?? string.Empty;
        var color = category.ToLowerInvariant() switch
        {
            "serie" => Color.FromRgb(0xD8, 0xB4, 0xFE),        // morado claro
            "anuncio" => Color.FromRgb(0x93, 0xC5, 0xFD),       // azul claro
            "actualización" => Color.FromRgb(0x86, 0xEF, 0xAC), // verde claro
            "actualizacion" => Color.FromRgb(0x86, 0xEF, 0xAC),
            "comunidad" => Color.FromRgb(0x67, 0xE8, 0xF9),     // cian claro
            "evento" => Color.FromRgb(0xF9, 0xA8, 0xD4),        // rosa claro
            _ => Color.FromRgb(0xE4, 0xE4, 0xE7)               // gris claro
        };
        return new SolidColorBrush(color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Fondo del pill de categoría: mismo tono con ~22% de opacidad para legibilidad.
/// </summary>
public class CategoryToBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var category = (value as string)?.Trim() ?? string.Empty;
        var color = category.ToLowerInvariant() switch
        {
            "serie" => Color.FromArgb(0x38, 0xA8, 0x55, 0xF7),
            "anuncio" => Color.FromArgb(0x38, 0x3B, 0x82, 0xF6),
            "actualización" => Color.FromArgb(0x38, 0x22, 0xC5, 0x5E),
            "actualizacion" => Color.FromArgb(0x38, 0x22, 0xC5, 0x5E),
            "comunidad" => Color.FromArgb(0x38, 0x22, 0xD3, 0xEE),
            "evento" => Color.FromArgb(0x38, 0xE8, 0x79, 0xA9),
            _ => Color.FromArgb(0x38, 0x63, 0x63, 0x70)
        };
        var brush = new SolidColorBrush(color);
        if (brush.CanFreeze) brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Resuelve un icono por su clave de recurso (ej: "IconCalendar") para las promos.
/// </summary>
public class PromoIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string key && !string.IsNullOrWhiteSpace(key))
        {
            try
            {
                return Application.Current?.TryFindResource(key);
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}