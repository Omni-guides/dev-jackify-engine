using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wabbajack.Paths;

namespace Wabbajack.Downloaders.GameFile;

internal static class HeroicLocator
{
    private static readonly string[] HeroicRelativePaths =
    {
        ".config/heroic",
        ".var/app/com.heroicgameslauncher.hgl/config/heroic",
    };

    // Returns GOG numeric app ID → install path
    public static Dictionary<long, AbsolutePath> FindGogGames(ILogger logger)
    {
        var result = new Dictionary<long, AbsolutePath>();
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return result;

        foreach (var heroicPath in EnumerateHeroicPaths())
        {
            var installedJson = Path.Combine(heroicPath, "gog_store", "installed.json");
            if (!File.Exists(installedJson)) continue;
            try
            {
                ParseGogInstalled(installedJson, result, logger);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Failed to parse Heroic GOG installed.json at {Path}", installedJson);
            }
        }

        return result;
    }

    // Returns Epic app_name → install path
    public static Dictionary<string, AbsolutePath> FindEpicGames(ILogger logger)
    {
        var result = new Dictionary<string, AbsolutePath>(StringComparer.OrdinalIgnoreCase);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return result;

        foreach (var heroicPath in EnumerateHeroicPaths())
        {
            // Current location first, then legacy
            var candidatePath = Path.Combine(heroicPath, "store_cache", "legendary_library.json");
            if (!File.Exists(candidatePath))
                candidatePath = Path.Combine(heroicPath, "legendaryConfig", "legendary", "installed.json");
            if (!File.Exists(candidatePath)) continue;
            try
            {
                ParseEpicInstalled(candidatePath, result, logger);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Failed to parse Heroic Epic library at {Path}", candidatePath);
            }
        }

        return result;
    }

    private static IEnumerable<string> EnumerateHeroicPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) yield break;

        foreach (var rel in HeroicRelativePaths)
        {
            var full = Path.Combine(home, rel);
            if (Directory.Exists(full)) yield return full;
        }
    }

    private static void ParseGogInstalled(string path, Dictionary<long, AbsolutePath> result, ILogger logger)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        // Heroic 2.14+: {"installed": [...]}; legacy: bare array
        JsonElement array;
        if (root.ValueKind == JsonValueKind.Array)
            array = root;
        else if (root.TryGetProperty("installed", out var wrapped) && wrapped.ValueKind == JsonValueKind.Array)
            array = wrapped;
        else
            return;

        foreach (var entry in array.EnumerateArray())
        {
            if (entry.TryGetProperty("platform", out var platform) &&
                platform.GetString() is { } p && p != "windows")
                continue;

            if (!entry.TryGetProperty("appName", out var appNameEl)) continue;
            var appName = appNameEl.GetString();
            if (string.IsNullOrEmpty(appName)) continue;

            // GOG app IDs are numeric strings in Heroic
            if (!long.TryParse(appName, out var gogId)) continue;

            if (!entry.TryGetProperty("install_path", out var pathEl)) continue;
            var installPath = pathEl.GetString();
            if (string.IsNullOrEmpty(installPath) || !Directory.Exists(installPath)) continue;

            result[gogId] = installPath.ToAbsolutePath();
            logger.LogDebug("Heroic: GOG {Id} at {Path}", gogId, installPath);
        }
    }

    private static void ParseEpicInstalled(string path, Dictionary<string, AbsolutePath> result, ILogger logger)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        foreach (var prop in root.EnumerateObject())
        {
            var game = prop.Value;
            if (game.ValueKind != JsonValueKind.Object) continue;

            if (!game.TryGetProperty("is_installed", out var installedEl) || !installedEl.GetBoolean())
                continue;

            if (game.TryGetProperty("platform", out var platform))
            {
                var p = platform.GetString();
                if (p != "Windows" && p != "windows") continue;
            }

            if (!game.TryGetProperty("install_path", out var pathEl)) continue;
            var installPath = pathEl.GetString();
            if (string.IsNullOrEmpty(installPath) || !Directory.Exists(installPath)) continue;

            result[prop.Name] = installPath.ToAbsolutePath();
            logger.LogDebug("Heroic: Epic {Id} at {Path}", prop.Name, installPath);
        }
    }
}
