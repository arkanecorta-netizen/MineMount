using System;
using System.IO;
using System.Threading.Tasks;

namespace MineMount.Services;

public interface ILogService
{
    void Info(string message);
    void Warning(string message);
    void Error(string message, Exception? exception = null);
    void Debug(string message);
}

public class LogService : ILogService
{
    private readonly string _logPath;
    private readonly object _lock = new();

    public LogService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var mineMountDir = Path.Combine(appData, "MineMount");
        Directory.CreateDirectory(mineMountDir);
        _logPath = Path.Combine(mineMountDir, "minemount.log");
    }

    public void Info(string message) => WriteLog("INFO", message);
    public void Warning(string message) => WriteLog("WARN", message);
    public void Error(string message, Exception? exception = null)
    {
        var fullMessage = exception != null ? $"{message}: {exception}" : message;
        WriteLog("ERROR", fullMessage);
    }
    public void Debug(string message) => WriteLog("DEBUG", message);

    private void WriteLog(string level, string message)
    {
        lock (_lock)
        {
            var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
            File.AppendAllText(_logPath, logEntry + Environment.NewLine);
        }
    }
}