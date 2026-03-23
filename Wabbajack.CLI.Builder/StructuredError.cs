using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Wabbajack.CLI.Builder;

/// <summary>
/// Emits structured JSON error lines to stderr for the Jackify Python frontend.
/// Format: {"je":"1","level":"error","type":"...","message":"...","context":{...}}
/// </summary>
public static class StructuredError
{
    public static class ErrorType
    {
        public const string AuthFailed       = "auth_failed";
        public const string PremiumRequired  = "premium_required";
        public const string NetworkError     = "network_error";
        public const string DiskFull         = "disk_full";
        public const string DiskError        = "disk_error";
        public const string PermissionDenied = "permission_denied";
        public const string ArchiveCorrupt   = "archive_corrupt";
        public const string FileNotFound     = "file_not_found";
        public const string DownloadStalled  = "download_stalled";
        public const string ValidationFailed = "validation_failed";
        public const string EngineError      = "engine_error";
    }

    /// <summary>
    /// Returns the exit code for a given error type per the jackify-engine structured error spec.
    /// 2=auth, 3=network, 4=disk/IO, 5=validation, 6=engine_error, 1=fallback/unknown.
    /// </summary>
    public static int ExitCodeFor(string type) => type switch
    {
        ErrorType.AuthFailed or ErrorType.PremiumRequired                                => 2,
        ErrorType.NetworkError or ErrorType.DownloadStalled                              => 3,
        ErrorType.DiskFull or ErrorType.DiskError or ErrorType.PermissionDenied          => 4,
        ErrorType.ArchiveCorrupt or ErrorType.ValidationFailed or ErrorType.FileNotFound => 5,
        _                                                                                 => 6,
    };

    /// <summary>
    /// Classifies an exception into a structured error type and user-facing message.
    /// Use this everywhere rather than hardcoding error types at each catch site.
    /// Returns null type for OperationCanceledException (caller should exit 1, no error emitted).
    /// </summary>
    public static (string? type, string message) Classify(Exception ex)
    {
        var msg = ex.Message;
        return ex switch
        {
            OperationCanceledException
                => (null, "Cancelled"),

            UnauthorizedAccessException
                => (ErrorType.PermissionDenied,
                    $"Permission denied accessing a file or directory. Check ownership and permissions. Detail: {msg}"),

            System.IO.PathTooLongException
                => (ErrorType.ValidationFailed,
                    $"A path in the modlist exceeds your filesystem's filename length limit. " +
                    $"This typically occurs with encrypted home directories (eCryptFS/fscrypt), which reduce the " +
                    $"effective limit below the standard 255 characters. Install to a non-encrypted location " +
                    $"such as /opt/LoreRim. Detail: {msg}"),

        InvalidOperationException when msg.Contains("Failed to locate") && msg.Contains("installed archive index")
                => (ErrorType.ValidationFailed,
                    msg),

        InvalidOperationException when msg.Contains("BSA verification failed")
                => (ErrorType.ValidationFailed,
                    msg),

        System.IO.IOException when msg.Contains("ENOSPC") || msg.Contains("No space left")
                => (ErrorType.DiskFull,
                    "Disk is full. Free up space and try again."),

            System.IO.IOException when msg.Contains("Access denied") || msg.Contains("Permission denied") ||
                                       msg.Contains("EACCES") || msg.Contains("EPERM")
                => (ErrorType.PermissionDenied,
                    $"Permission denied reading a file. Detail: {msg}"),

            System.IO.IOException
                => (ErrorType.DiskError,
                    $"Disk I/O error reading a file. This may indicate a failing storage device, corrupt download, or SD card issue. Detail: {msg}"),

            System.Net.Http.HttpRequestException when
                msg.Contains("401") || msg.Contains("403") ||
                msg.Contains("Unauthorized") || msg.Contains("Forbidden")
                => (ErrorType.AuthFailed,
                    $"Authentication failed. Re-authenticate and try again. Detail: {msg}"),

            System.Net.Http.HttpRequestException
                => (ErrorType.NetworkError,
                    $"Network error during installation. Check your connection and try again. Detail: {msg}"),

            _   => (ErrorType.EngineError,
                    $"{ex.GetType().Name}: {msg}"),
        };
    }

    /// <summary>Writes a structured error line to stderr.</summary>
    public static void WriteError(string type, string message, Dictionary<string, object?>? context = null)
        => Emit("error", type, message, context);

    /// <summary>Writes a structured warning line to stderr.</summary>
    public static void WriteWarning(string type, string message, Dictionary<string, object?>? context = null)
        => Emit("warning", type, message, context);

    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = false };

    private static void Emit(string level, string type, string message, Dictionary<string, object?>? context)
    {
        var obj = new Dictionary<string, object?>
        {
            ["je"]      = "1",
            ["level"]   = level,
            ["type"]    = type,
            ["message"] = message,
        };
        if (context is { Count: > 0 })
            obj["context"] = context;
        Console.Error.WriteLine(JsonSerializer.Serialize(obj, _opts));
    }
}
