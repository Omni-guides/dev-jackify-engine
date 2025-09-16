using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;

namespace Wabbajack.Common;

public static class JackifyConfig
{
    private const string ConfigFileName = "config.json";
    private const string ConfigDirName = "jackify";
    private const string DataDirKey = "jackify_data_dir";
    private static AbsolutePath? _cachedDataDir;

    /// <summary>
    /// Returns the configured Jackify data directory from ~/.config/jackify/config.json.
    /// Falls back to ~/Jackify if reading/parsing fails for any reason.
    /// </summary>
    public static AbsolutePath GetDataDirectory()
    {
        if (_cachedDataDir != null) return _cachedDataDir!.Value;

        try
        {
            var home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            AbsolutePath configDir;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                configDir = (home + "/.config/" + ConfigDirName).ToAbsolutePath();
            }
            else
            {
                // Non-Linux fallback (unlikely used): reuse AppDataLocal
                configDir = KnownFolders.WabbajackAppLocal.Combine(ConfigDirName);
            }

            var configPath = configDir.Combine(ConfigFileName);
            if (configPath.FileExists())
            {
                var json = File.ReadAllText(configPath.ToString());
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty(DataDirKey, out var dataDirProp) && dataDirProp.ValueKind == JsonValueKind.String)
                {
                    var dataDir = dataDirProp.GetString();
                    if (!string.IsNullOrWhiteSpace(dataDir))
                    {
                        _cachedDataDir = dataDir!.ToAbsolutePath();
                        return _cachedDataDir.Value;
                    }
                }
            }
        }
        catch
        {
            // Ignore and fall through to default
        }

        var fallback = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).ToAbsolutePath().Combine("Jackify");
        _cachedDataDir = fallback;
        return fallback;
    }
}

