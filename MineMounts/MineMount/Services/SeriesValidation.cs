using System;
using System.IO;
using System.Linq;

namespace MineMount.Services;

// Validación estricta de los datos que vienen del catálogo remoto.
// El catálogo se descarga de Internet: nunca confiar en id/tag/asset
// para construir rutas de archivo o URLs (path traversal / URL injection).
public static class SeriesValidation
{
    public static bool IsValidSeriesId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) return false;

        if (id.Contains("..", StringComparison.Ordinal)) return false;

        return id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');
    }

    public static bool IsValidVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > 32) return false;

        return version.All(c => char.IsDigit(c) || c is '.' or '-' or '+');
    }

    // Segmento de URL (tag de release): sin /, \, ?, #, % ni espacios
    public static bool IsValidReleaseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag.Length > 128) return false;

        if (tag.Any(char.IsWhiteSpace)) return false;

        return tag.IndexOfAny(new[] { '/', '\\', '?', '#', '%', '"', '<', '>', '|' }) < 0;
    }

    // Nombre de archivo: sin separadores ni caracteres reservados
    public static bool IsValidAssetName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200) return false;

        if (name.Contains("..", StringComparison.Ordinal)) return false;

        return name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|', '#', '%' }) < 0;
    }

    // Ruta de instalación: absoluta y sin caracteres inválidos
    public static bool IsValidInstallPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 260) return false;

        try
        {
            if (!Path.IsPathRooted(path)) return false;

            Path.GetFullPath(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
