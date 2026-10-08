using System.Windows;

namespace MineMount.Services;

/// <summary>
/// Localización ES/EN: busca "S.Clave" en los diccionarios de la app
/// (Strings.es.xaml / Strings.en.xaml). Si falta, devuelve la clave.
/// Los textos de log quedan en español; todo lo visible usa Loc.T.
/// </summary>
public static class Loc
{
    public static string T(string key)
    {
        try
        {
            var app = Application.Current;
            if (app != null)
            {
                var value = app.TryFindResource(key) as string;
                if (!string.IsNullOrEmpty(value)) return value;
            }
        }
        catch
        {
            // Sin Application (tests): cae a la clave
        }
        return key;
    }

    public static string Tf(string key, params object?[] args)
    {
        var format = T(key);
        try
        {
            return string.Format(format, args);
        }
        catch
        {
            return format;
        }
    }
}
