using System;
using System.Collections.Generic;
using System.IO;

namespace MahjongHelper.Mahjong;

/// <summary>
/// Pure helper (no game runtime) for FFXIV.cfg <c>ScreenShotDir</c> and
/// screenshot-folder candidates. Safe to unit-test without Dalamud.
/// </summary>
public static class FfxivCfgScreenshotDir
{
    public const string RealmRebornFolder = "FINAL FANTASY XIV - A Realm Reborn";
    public const string ShortFolder = "FINAL FANTASY XIV";
    public const string CfgFileName = "FFXIV.cfg";
    public const string ScreenShotDirKey = "ScreenShotDir";

    /// <summary>
    /// Parse <c>ScreenShotDir</c> from FFXIV.cfg text. Empty / missing means the
    /// client default folder. The live file uses a tab after the key, e.g.
    /// <c>ScreenShotDir\tC:\Users\...\Screenshots</c>.
    /// </summary>
    public static string? ParseScreenShotDir(string? cfgText)
    {
        if (string.IsNullOrEmpty(cfgText))
            return null;

        using var reader = new StringReader(cfgText);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0)
                continue;
            if (line[0] == '\uFEFF')
                line = line[1..];

            if (!line.StartsWith(ScreenShotDirKey, StringComparison.OrdinalIgnoreCase))
                continue;

            var rest = line[ScreenShotDirKey.Length..];
            if (rest.Length == 0)
                return null;

            // Require a delimiter so ScreenShotDirExtra is not a match.
            if (rest[0] is not ('\t' or ' ' or '='))
                continue;

            var value = rest.TrimStart('\t', ' ', '=').Trim().Trim('"');
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    public static IEnumerable<string> EnumerateCfgFileCandidates(string? documents, string? userProfile)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(documents))
            AddUnique(paths, seen, Path.Combine(documents, "My Games", RealmRebornFolder, CfgFileName));

        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            AddUnique(paths, seen, Path.Combine(userProfile, "Documents", "My Games", RealmRebornFolder, CfgFileName));
            AddUnique(paths, seen, Path.Combine(userProfile, "OneDrive", "Documents", "My Games", RealmRebornFolder, CfgFileName));
            foreach (var od in EnumerateOneDriveRoots(userProfile))
                AddUnique(paths, seen, Path.Combine(od, "Documents", "My Games", RealmRebornFolder, CfgFileName));
        }

        return paths;
    }

    public static IEnumerable<string> EnumerateCfgFileCandidates()
        => EnumerateCfgFileCandidates(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// First existing cfg that has a non-empty ScreenShotDir, else first existing cfg.
    /// </summary>
    public static string? TryFindLiveCfgPath()
    {
        string? firstExisting = null;
        foreach (var cfg in EnumerateCfgFileCandidates())
        {
            if (!File.Exists(cfg))
                continue;
            firstExisting ??= cfg;
            try
            {
                if (ParseScreenShotDir(File.ReadAllText(cfg)) != null)
                    return cfg;
            }
            catch
            {
                // keep looking
            }
        }

        return firstExisting;
    }

    public static string? TryReadConfiguredDirectory()
    {
        foreach (var cfg in EnumerateCfgFileCandidates())
        {
            if (!File.Exists(cfg))
                continue;
            try
            {
                var dir = ParseScreenShotDir(File.ReadAllText(cfg));
                if (!string.IsNullOrWhiteSpace(dir))
                    return dir;
            }
            catch
            {
                // keep looking
            }
        }

        return null;
    }

    public static IEnumerable<string> EnumerateScreenshotDirectoryCandidates(
        string? configuredDir,
        string? documents,
        string? userProfile)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(configuredDir))
            AddUnique(paths, seen, configuredDir.Trim());

        var relative = new[]
        {
            Path.Combine("My Games", RealmRebornFolder, "screenshots"),
            Path.Combine("My Games", ShortFolder, "screenshots"),
        };

        if (!string.IsNullOrWhiteSpace(documents))
        {
            foreach (var rel in relative)
                AddUnique(paths, seen, Path.Combine(documents, rel));
        }

        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            foreach (var root in new[]
                     {
                         Path.Combine(userProfile, "Documents"),
                         Path.Combine(userProfile, "OneDrive", "Documents"),
                     })
            {
                foreach (var rel in relative)
                    AddUnique(paths, seen, Path.Combine(root, rel));
            }

            foreach (var od in EnumerateOneDriveRoots(userProfile))
            {
                foreach (var rel in relative)
                    AddUnique(paths, seen, Path.Combine(od, "Documents", rel));
            }
        }

        return paths;
    }

    private static IEnumerable<string> EnumerateOneDriveRoots(string userProfile)
    {
        try
        {
            if (Directory.Exists(userProfile))
                return Directory.EnumerateDirectories(userProfile, "OneDrive*");
        }
        catch
        {
            // ignore
        }

        return [];
    }

    private static void AddUnique(List<string> paths, HashSet<string> seen, string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && seen.Add(path))
            paths.Add(path);
    }
}
