using System;

namespace Wabbajack.Common;

public static class ConsoleOutput
{
    /// <summary>
    /// When true, [FILE_PROGRESS] tagged lines are emitted to stdout.
    /// Set by --show-file-progress flag in Program.Main before verb dispatch.
    /// </summary>
    public static bool ShowFileProgress { get; set; } = false;

    /// <summary>
    /// Prints a message to stdout.
    /// </summary>
    public static void PrintWithDuration(string message)
    {
        Console.WriteLine(message);
    }

    public static void PrintProgressWithDuration(string message)
    {
        Console.Write(message + "\x1b[K\r");
        Console.Out.Flush();
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
    /// Returns empty string. Kept for call-site compatibility; callers should be cleaned up.
    /// </summary>
    [Obsolete("Timestamp prefix removed - do not use")]
    public static string GetDurationTimestamp() => "";

    /// <summary>
    /// Prints individual file progress in the format expected by Jackify GUI parser.
    /// Format: [FILE_PROGRESS] Operation: filename (percent%) [speed] (completed/total)
    /// Outputs to stdout with a newline so Jackify's line-by-line capture sees each line.
    /// Only outputs if ShowFileProgress is true (enabled via --show-file-progress flag).
    /// </summary>
    public static void PrintFileProgress(string operation, string filename, double percent, string speed, int? completed = null, int? total = null)
    {
        // Suppress FILE_PROGRESS output unless --show-file-progress flag is set
        // This keeps manual runs clean while allowing Jackify GUI to enable detailed progress
        if (!ShowFileProgress)
            return;
        
        // Format percent - use integer if whole number, otherwise one decimal place
        var percentStr = percent % 1.0 == 0 ? $"{(int)percent}" : $"{percent:F1}";
        
        // Don't show speed for operations that don't involve transfer or don't have meaningful speed data
        var speedPart = (operation == "Checking existing" || operation == "Converting" || operation == "Building") ? "" : $" [{speed}]";
        
        // Add counter if provided (e.g., " (1232/3927753)")
        var counterPart = (completed.HasValue && total.HasValue) ? $" ({completed.Value}/{total.Value})" : "";
        
        var message = $"[FILE_PROGRESS] {operation}: {filename} ({percentStr}%){speedPart}{counterPart}";
        
        // Write to stdout with newline so Jackify's line-by-line capture sees each FILE_PROGRESS line
        Console.Out.WriteLine(message);
        Console.Out.Flush();
    }
}
