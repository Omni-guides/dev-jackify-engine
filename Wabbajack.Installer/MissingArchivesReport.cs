using System.Collections.Generic;

namespace Wabbajack.Installer;

/// <summary>One archive or game file the installer could not resolve after retry.</summary>
public class MissingArchiveEntry
{
    public string Name { get; set; } = "";

    /// <summary>"download" (a real download failure) or "game_file" (a GameFileSource mismatch).</summary>
    public string SourceType { get; set; } = "";

    /// <summary>game_file entries only: "missing", "wrong_size", or "wrong_hash".</summary>
    public string? Reason { get; set; }

    public long? ExpectedSize { get; set; }
    public long? ActualSize { get; set; }
    public string? GameVersion { get; set; }
}

/// <summary>
/// Every archive/game-file the installer failed to resolve for one install attempt, collected
/// in one place instead of the caller having to piece it together from log lines or exception
/// messages. Read by Verbs/Install.cs after a failed Begin() to emit a single structured error.
/// </summary>
public class MissingArchivesReport
{
    public List<MissingArchiveEntry> Entries { get; set; } = new();

    /// <summary>Total GameFileSource archives referenced by the modlist, for "N of M" framing.</summary>
    public int TotalGameFileSourceArchives { get; set; }
}
