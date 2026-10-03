using System;
using System.IO;
using System.Threading.Tasks;

namespace MineMount.Services;

public interface IDiskSpaceService
{
    long GetFreeSpaceMB(string path);
    Task<bool> HasEnoughSpaceAsync(string path, long requiredMB);
    string FormatSize(long bytes);
}

// Espacio en disco: se comprueba ANTES de descargar (ZIP +
// extracción + instalación final) y se controlan los errores
// de disco durante la operación.
public class DiskSpaceService : IDiskSpaceService
{
    public long GetFreeSpaceMB(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return -1;

            var drive = new DriveInfo(root);
            if (!drive.IsReady) return -1;

            return drive.AvailableFreeSpace / (1024 * 1024);
        }
        catch
        {
            return -1;
        }
    }

    public Task<bool> HasEnoughSpaceAsync(string path, long requiredMB)
    {
        var free = GetFreeSpaceMB(path);
        return Task.FromResult(free >= 0 && free >= requiredMB);
    }

    public string FormatSize(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F0} MB";
        if (bytes >= 1_024) return $"{bytes / 1_024.0:F0} KB";
        return $"{bytes} B";
    }
}
