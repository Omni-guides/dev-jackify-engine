using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net.NetworkInformation;

namespace Wabbajack.Common;

/// <summary>
/// Professional-grade bandwidth monitoring for download operations.
/// Uses actual network interface statistics for accurate system-wide throughput measurement.
/// </summary>
public class BandwidthMonitor
{
    private readonly ConcurrentQueue<(DateTime timestamp, long bytesReceived)> _samples = new();
    private readonly object _lock = new();
    private readonly int _sampleWindowSeconds;
    private readonly Timer _networkTimer;
    private long _lastBytesReceived = 0;
    private DateTime _lastSampleTime = DateTime.UtcNow;
    private readonly NetworkInterface? _primaryInterface;
    
    /// <summary>
    /// Creates a bandwidth monitor with specified sample window
    /// </summary>
    /// <param name="sampleWindowSeconds">Window size for calculating average (default 5 seconds)</param>
    public BandwidthMonitor(int sampleWindowSeconds = 5)
    {
        _sampleWindowSeconds = sampleWindowSeconds;
        
        // Find the primary network interface (the one with the highest bytes received)
        _primaryInterface = GetPrimaryNetworkInterface();
        
        if (_primaryInterface != null)
        {
            _lastBytesReceived = _primaryInterface.GetIPv4Statistics().BytesReceived;
        }
        
        // Sample network stats every 500ms for accurate measurement
        _networkTimer = new Timer(SampleNetworkStats, null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
    }
    
    private static NetworkInterface? GetPrimaryNetworkInterface()
    {
        try
        {
            // Get the interface with the most bytes received (likely the main internet connection)
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
                .Where(ni => ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Where(ni => ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .OrderByDescending(ni => ni.GetIPv4Statistics().BytesReceived)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
    
    private void SampleNetworkStats(object? state)
    {
        if (_primaryInterface == null) return;
        
        try
        {
            var stats = _primaryInterface.GetIPv4Statistics();
            var currentBytesReceived = stats.BytesReceived;
            var now = DateTime.UtcNow;
            
            lock (_lock)
            {
                if (_lastBytesReceived > 0) // Skip first sample to establish baseline
                {
                    var deltaBytes = currentBytesReceived - _lastBytesReceived;
                    if (deltaBytes > 0) // Only record positive deltas
                    {
                        _samples.Enqueue((now, deltaBytes));
                    }
                }
                
                _lastBytesReceived = currentBytesReceived;
                _lastSampleTime = now;
                
                // Clean up old samples
                CleanupOldSamples();
            }
        }
        catch
        {
            // Network interface might be temporarily unavailable, skip this sample
        }
    }
    
    public double GetCurrentBandwidthMBps()
    {
        lock (_lock)
        {
            if (_samples.IsEmpty) return 0.0;
            
            var now = DateTime.UtcNow;
            var cutoffTime = now.AddSeconds(-_sampleWindowSeconds);
            
            // Get samples within our window
            var recentSamples = _samples.Where(s => s.timestamp >= cutoffTime).ToList();
            
            if (!recentSamples.Any()) return 0.0;
            
            var totalBytes = recentSamples.Sum(s => s.bytesReceived);
            var timeSpan = (now - recentSamples.First().timestamp).TotalSeconds;
            
            // Avoid division by zero and unrealistic values
            if (timeSpan <= 0 || timeSpan > _sampleWindowSeconds + 1) return 0.0;
            
            var bytesPerSecond = totalBytes / timeSpan;
            var mbps = bytesPerSecond / (1024.0 * 1024.0);
            
            // Sanity check: cap at reasonable maximum (100 MB/s)
            return Math.Min(mbps, 100.0);
        }
    }
    
    /// <summary>
    /// Resets all bandwidth monitoring data
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _samples.Clear();
            _lastSampleTime = DateTime.UtcNow;
            if (_primaryInterface != null)
            {
                _lastBytesReceived = _primaryInterface.GetIPv4Statistics().BytesReceived;
            }
        }
    }
    
    private void CleanupOldSamples()
    {
        var cutoffTime = DateTime.UtcNow.AddSeconds(-_sampleWindowSeconds * 2); // Keep double the window for safety
        
        // Remove old samples - note: ConcurrentQueue doesn't have RemoveWhere, so we rebuild
        var validSamples = _samples.Where(s => s.timestamp >= cutoffTime).ToList();
        _samples.Clear();
        foreach (var sample in validSamples)
        {
            _samples.Enqueue(sample);
        }
    }
    
    public void Dispose()
    {
        _networkTimer?.Dispose();
    }
}