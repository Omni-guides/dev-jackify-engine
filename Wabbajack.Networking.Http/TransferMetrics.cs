using System;
using System.Threading;
using Wabbajack.Networking.Http.Interfaces;

namespace Wabbajack.Networking.Http;

/// <summary>
/// Application-layer transfer metrics. Derives speed from Record() calls made by the
/// downloader per chunk, so it's accurate regardless of interface naming or multi-NIC setup.
/// </summary>
public class TransferMetrics : ITransferMetrics, IDisposable
{
    private long _totalBytes;
    private long _lastTotalBytes;
    private DateTime _lastSampleTime;
    private double _lastInstant;
    private double _ewma;
    private readonly object _lock = new();
    private readonly Timer _timer;

    public TransferMetrics()
    {
        _lastSampleTime = DateTime.UtcNow;
        _timer = new Timer(Sample, null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
    }

    private void Sample(object? _)
    {
        var now = DateTime.UtcNow;
        var current = Interlocked.Read(ref _totalBytes);

        lock (_lock)
        {
            var elapsed = (now - _lastSampleTime).TotalSeconds;
            if (elapsed > 0)
            {
                var delta = current - _lastTotalBytes;
                if (delta >= 0)
                {
                    var instant = delta / elapsed;
                    _lastInstant = instant;
                    const double alpha = 0.3;
                    _ewma = _ewma <= 0 ? instant : (alpha * instant + (1 - alpha) * _ewma);
                }
                _lastTotalBytes = current;
                _lastSampleTime = now;
            }
        }
    }

    public void Record(long bytes)
    {
        if (bytes > 0)
            Interlocked.Add(ref _totalBytes, bytes);
    }

    public long TotalBytes => Interlocked.Read(ref _totalBytes);

    public double BytesPerSecond1s
    {
        get { lock (_lock) return _lastInstant; }
    }

    public double BytesPerSecondSmoothed
    {
        get { lock (_lock) return _ewma; }
    }

    public void Dispose()
    {
        _timer.Dispose();
    }
}
