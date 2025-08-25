using System;

namespace Wabbajack.Common;

public static class ConsoleOutput
{
    private static readonly DateTime _startTime = DateTime.UtcNow;

    /// <summary>
    /// Prints a message with a duration timestamp (elapsed time since program start)
    /// Format: [HH:MM:SS] message
    /// </summary>
    public static void PrintWithDuration(string message)
    {
        var elapsed = DateTime.UtcNow - _startTime;
        Console.WriteLine($"[{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}] {message}");
    }

    public static void PrintProgressWithDuration(string message)
    {
        var elapsed = DateTime.UtcNow - _startTime;
        var timestamped = $"[{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}] {message}";
        
        try
        {
            // Clear the current line and write the progress message
            Console.Write($"\r\x1b[K{timestamped}");
            Console.Out.Flush();
        }
        catch
        {
            // Fallback if ANSI escape codes aren't supported
            // Use a more robust fallback - pad with spaces and return to start
            var windowWidth = 120; // Default fallback width
            try { windowWidth = Console.WindowWidth; } catch { }
            Console.Write($"\r{timestamped.PadRight(windowWidth)}\r");
            Console.Out.Flush();
        }
    }

    /// <summary>
    /// Clears the current progress line and moves to the next line
    /// Call this after progress updates to ensure clean output
    /// </summary>
    public static void ClearProgressLine()
    {
        try
        {
            // Clear the current line and move to next line
            Console.Write("\r\x1b[K\n");
            Console.Out.Flush();
        }
        catch
        {
            // Fallback if ANSI escape codes aren't supported
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Gets the current duration timestamp as a string
    /// Format: [HH:MM:SS]
    /// </summary>
    public static string GetDurationTimestamp()
    {
        var elapsed = DateTime.UtcNow - _startTime;
        return $"[{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}]";
    }
}
