using System;
using System.Threading;
using System.Threading.Tasks;

namespace MineMount.Services;

/// <summary>
/// Límites globales de descarga (Configuración → Descargas):
/// simultaneidad con semáforo + velocidad con token bucket.
/// </summary>
public static class DownloadLimiter
{
    private static readonly object _lock = new();
    private static SemaphoreSlim _gate = new(2, 8);
    private static int _maxConcurrent = 2;
    private static long _bytesPerSecond; // 0 = sin límite
    private static double _allowance;
    private static DateTime _lastRefill = DateTime.UtcNow;

    public static void Configure(int maxConcurrent, long bytesPerSecond)
    {
        lock (_lock)
        {
            maxConcurrent = Math.Clamp(maxConcurrent, 1, 8);
            if (maxConcurrent != _maxConcurrent)
            {
                _maxConcurrent = maxConcurrent;
                _gate = new SemaphoreSlim(maxConcurrent, 8);
            }
            _bytesPerSecond = Math.Max(0, bytesPerSecond);
            _allowance = _bytesPerSecond;
            _lastRefill = DateTime.UtcNow;
        }
    }

    public static async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        var gate = _gate;
        await gate.WaitAsync(ct);
        return new Release(gate);
    }

    public static async Task ThrottleAsync(int byteCount, CancellationToken ct)
    {
        long rate;
        lock (_lock) rate = _bytesPerSecond;
        if (rate <= 0 || byteCount <= 0) return;

        while (true)
        {
            double waitMs;
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var elapsed = (now - _lastRefill).TotalSeconds;
                _lastRefill = now;
                _allowance = Math.Min(rate, _allowance + elapsed * rate);
                if (_allowance >= byteCount)
                {
                    _allowance -= byteCount;
                    return;
                }
                waitMs = (byteCount - _allowance) / rate * 1000.0;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, Math.Max(10, waitMs))), ct);
            lock (_lock)
            {
                // Reevaluar tras la espera: si ya alcanza, consumir y salir
                var now = DateTime.UtcNow;
                var elapsed = (now - _lastRefill).TotalSeconds;
                _lastRefill = now;
                _allowance = Math.Min(rate, _allowance + elapsed * rate);
                if (_allowance >= byteCount)
                {
                    _allowance -= byteCount;
                    return;
                }
            }
        }
    }

    private sealed class Release : IDisposable
    {
        private readonly SemaphoreSlim _gate;
        public Release(SemaphoreSlim gate) => _gate = gate;
        public void Dispose() => _gate.Release();
    }
}
