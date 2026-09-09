using System;
using System.Collections.Generic;
using System.Threading;

namespace Wabbajack.Common;

/// <summary>
/// Tracks individual file progress for downloads, extractions, and other operations.
/// Provides thread-safe tracking with per-file speed calculation from progress deltas.
/// </summary>
public class FileProgressTracker : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, FileProgressInfo> _activeFiles = new();
    private bool _disposed = false;
    private const int CompletedFileRetentionSeconds = 3; // Keep completed files visible for 3 seconds
    private const double MinSpeedSampleIntervalSeconds = 0.1; // Floor for delta-based rate calc; avoids inflated instantaneous rates from bursty callback timing

    /// <summary>
    /// Updates progress for a file. Calculates speed from progress deltas.
    /// </summary>
    public void UpdateProgress(string filename, string operation, long currentBytes, long? totalBytes, DateTime updateTime)
    {
        if (_disposed) return;

        lock (_lock)
        {
            if (!_activeFiles.TryGetValue(filename, out var info))
            {
                info = new FileProgressInfo
                {
                    Filename = filename,
                    Operation = operation,
                    StartTime = updateTime,
                    LastUpdateTime = updateTime,
                    LastBytes = currentBytes,
                    CurrentBytes = currentBytes,
                    TotalBytes = totalBytes
                };
                _activeFiles[filename] = info;
            }
            else
            {
                // Calculate speed from delta, but only when enough time has passed to make the
                // rate meaningful. Skipped samples fold into the next one's delta instead of
                // advancing the anchor, so percent stays live without distorting speed.
                var timeDelta = (updateTime - info.LastUpdateTime).TotalSeconds;
                if (timeDelta >= MinSpeedSampleIntervalSeconds && currentBytes > info.LastBytes)
                {
                    var bytesDelta = currentBytes - info.LastBytes;
                    var bytesPerSecond = bytesDelta / timeDelta;

                    // Use exponential moving average for smoother speed display
                    if (info.SpeedBytesPerSecond > 0)
                    {
                        info.SpeedBytesPerSecond = (info.SpeedBytesPerSecond * 0.7) + (bytesPerSecond * 0.3);
                    }
                    else
                    {
                        info.SpeedBytesPerSecond = bytesPerSecond;
                    }

                    info.LastUpdateTime = updateTime;
                    info.LastBytes = currentBytes;
                }

                info.CurrentBytes = currentBytes;
                info.TotalBytes = totalBytes;
            }
        }
    }

    /// <summary>
    /// Marks a file as completed. Keeps it visible for a few seconds so GUI can see completion.
    /// </summary>
    public void MarkCompleted(string filename)
    {
        if (_disposed) return;

        lock (_lock)
        {
            if (_activeFiles.TryGetValue(filename, out var info))
            {
                // Mark as completed but keep it visible for a few seconds
                info.IsCompleted = true;
                info.CompletedTime = DateTime.UtcNow;
                // Set to 100% to show completion
                if (info.TotalBytes.HasValue && info.TotalBytes.Value > 0)
                {
                    info.CurrentBytes = info.TotalBytes.Value;
                }
            }
        }
    }

    /// <summary>
    /// Gets all currently active files with their progress information.
    /// Automatically cleans up old completed files.
    /// </summary>
    public Dictionary<string, FileProgressInfo> GetActiveFiles()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var filesToRemove = new List<string>();
            
            // Clean up completed files that are older than retention period
            foreach (var (filename, info) in _activeFiles)
            {
                if (info.IsCompleted && info.CompletedTime.HasValue)
                {
                    var age = (now - info.CompletedTime.Value).TotalSeconds;
                    if (age > CompletedFileRetentionSeconds)
                    {
                        filesToRemove.Add(filename);
                    }
                }
            }
            
            // Remove old completed files
            foreach (var filename in filesToRemove)
            {
                _activeFiles.Remove(filename);
            }
            
            // Return a copy to avoid locking issues
            return new Dictionary<string, FileProgressInfo>(_activeFiles);
        }
    }

    /// <summary>
    /// Gets information for a specific file (for immediate completion output).
    /// </summary>
    public FileProgressInfo? GetFileInfo(string filename)
    {
        lock (_lock)
        {
            return _activeFiles.TryGetValue(filename, out var info) ? info : null;
        }
    }

    /// <summary>
    /// Clears all tracked files.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _activeFiles.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
    }
}

/// <summary>
/// Information about a file's progress.
/// </summary>
public class FileProgressInfo
{
    public string Filename { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime LastUpdateTime { get; set; }
    public long LastBytes { get; set; }
    public long CurrentBytes { get; set; }
    public long? TotalBytes { get; set; }
    public double SpeedBytesPerSecond { get; set; }
    public bool IsCompleted { get; set; }
    public DateTime? CompletedTime { get; set; }

    /// <summary>
    /// Gets the progress percentage (0-100).
    /// </summary>
    public double GetPercent()
    {
        // Always show 100% when completed
        if (IsCompleted)
        {
            return 100.0;
        }
        
        if (TotalBytes == null || TotalBytes.Value <= 0)
            return 0.0;
        
        return Math.Min(100.0, (CurrentBytes / (double)TotalBytes.Value) * 100.0);
    }

    /// <summary>
    /// Gets the formatted speed string (e.g., "6.8MB/s").
    /// </summary>
    public string GetSpeedString()
    {
        if (SpeedBytesPerSecond <= 0)
            return "0B/s";
        
        var speedBytes = (long)SpeedBytesPerSecond;
        return $"{speedBytes.ToFileSizeString()}/s";
    }
}

