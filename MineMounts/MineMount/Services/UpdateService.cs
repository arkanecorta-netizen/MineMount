using System;
using System.Threading.Tasks;

namespace MineMount.Services;

public interface IUpdateService
{
    Task<bool> CheckForUpdatesAsync();
    Task DownloadUpdateAsync();
    string CurrentVersion { get; }
    string LatestVersion { get; }
    bool IsUpdateAvailable { get; }
}

public class UpdateService : IUpdateService
{
    public string CurrentVersion => "1.0.0";
    public string LatestVersion => "1.0.0";
    public bool IsUpdateAvailable => false;

    public Task<bool> CheckForUpdatesAsync()
    {
        return Task.FromResult(false);
    }

    public Task DownloadUpdateAsync()
    {
        return Task.CompletedTask;
    }
}