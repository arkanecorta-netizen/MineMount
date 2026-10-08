using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace MineMount.Services;

/// <summary>
/// Datos reales de la PC: RAM total (P/Invoke), CPU/GPU (WMI/registro),
/// SO y espacio en disco. Sin paquetes extra.
/// </summary>
public interface ISystemInfoService
{
    long GetTotalRamMB();
    string GetCpuName();
    List<string> GetGpuNames();
    string GetOsDescription();
    string GetAppDataInfo();
}

public class SystemInfoService : ISystemInfoService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public long GetTotalRamMB()
    {
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref status))
                return (long)(status.ullTotalPhys / (1024 * 1024));
        }
        catch
        {
            // Sin RAM detectable
        }
        return 0;
    }

    public string GetCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var name = key?.GetValue("ProcessorNameString") as string;
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        }
        catch
        {
            // Sin acceso al registro
        }
        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "CPU desconocida";
    }

    public List<string> GetGpuNames()
    {
        var list = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_VideoController");
            foreach (var obj in searcher.Get())
            {
                var name = obj["Name"] as string;
                if (!string.IsNullOrWhiteSpace(name)) list.Add(name.Trim());
            }
        }
        catch
        {
            // WMI no disponible
        }
        return list;
    }

    public string GetOsDescription()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName") as string;
            var build = key?.GetValue("CurrentBuildNumber") as string;
            if (!string.IsNullOrWhiteSpace(product))
                return $"{product} (build {build}, {(Environment.Is64BitOperatingSystem ? "64" : "32")} bits)";
        }
        catch
        {
            // Sin acceso al registro
        }
        return Environment.OSVersion.ToString();
    }

    public string GetAppDataInfo()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)));
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                    return $"{root} ({drive.AvailableFreeSpace / 1073741824.0:F1} GB libres)";
            }
        }
        catch
        {
            // Sin acceso al disco
        }
        return "?";
    }

    public static Task<long> GetDirectorySizeAsync(string path)
    {
        return Task.Run(() =>
        {
            long total = 0;
            try
            {
                if (!Directory.Exists(path)) return 0;
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch
                    {
                        // Archivo en uso o sin permiso: se saltea
                    }
                }
            }
            catch
            {
                // Carpeta ilegible
            }
            return total;
        });
    }
}
