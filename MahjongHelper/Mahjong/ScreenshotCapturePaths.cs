using System;
using System.IO;

namespace MahjongHelper.Mahjong;

/// <summary>
/// Pure helpers for KAN-55 CaptureFallback output paths. No HWND / game runtime.
/// Primary file: <c>%APPDATA%\MahjongHelper\screenshots\mj-yyyyMMdd-HHmmssfff.png</c>.
/// </summary>
public static class ScreenshotCapturePaths
{
    public const string MethodName = "CaptureFallback";
    public const string FilePrefix = "mj-";
    public const string FileExtension = ".png";

    public static readonly string AppDataScreenshotsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MahjongHelper",
        "screenshots");

    public static string FileName(DateTime utc)
    {
        var stamp = utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc;
        return $"{FilePrefix}{stamp:yyyyMMdd-HHmmssfff}{FileExtension}";
    }

    public static string PrimaryPath(DateTime utc)
        => Path.Combine(AppDataScreenshotsDirectory, FileName(utc));

    public static string? ExtraCopyPath(string? configuredDir, DateTime utc)
    {
        if (string.IsNullOrWhiteSpace(configuredDir))
            return null;
        return Path.Combine(configuredDir.Trim(), FileName(utc));
    }

    public static string EnsureAppDataDirectory()
    {
        Directory.CreateDirectory(AppDataScreenshotsDirectory);
        return AppDataScreenshotsDirectory;
    }

    public static string? TryCopyToConfiguredDir(string primaryPath, string? configuredDir)
    {
        if (string.IsNullOrWhiteSpace(configuredDir) || string.IsNullOrWhiteSpace(primaryPath))
            return null;
        if (!File.Exists(primaryPath))
            return null;

        var dest = Path.Combine(configuredDir.Trim(), Path.GetFileName(primaryPath));
        try
        {
            Directory.CreateDirectory(configuredDir.Trim());
            File.Copy(primaryPath, dest, overwrite: true);
            return File.Exists(dest) ? dest : null;
        }
        catch
        {
            return null;
        }
    }
}
