using System;
using System.IO;

namespace MahjongHelper.Mahjong;

/// <summary>
/// Pure rules for recovering a stuck <c>ScreenShotRequested</c> bit and
/// detecting phantom Success (Result=Success but no file). No game runtime.
/// </summary>
public static class ScreenshotStuckRecovery
{
    public const int StaleRequestSeconds = 8;

    public const string PhantomSuccessHint =
        "ReShade/DXGI often causes this";

    public static string NormalizeGamePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        return path.Trim().Replace('/', Path.DirectorySeparatorChar);
    }

    public static bool IsLocationMissingOnDisk(string? location)
    {
        var path = NormalizeGamePath(location);
        if (path.Length == 0)
            return true;
        try
        {
            return !File.Exists(path);
        }
        catch
        {
            return true;
        }
    }

    public static bool IsPhantomSuccess(string? result, string? location)
        => string.Equals(result, "Success", StringComparison.OrdinalIgnoreCase)
           && IsLocationMissingOnDisk(location);

    public static string FormatPhantomSuccess(string? location)
        => $"Result=Success but file missing at Location={location ?? "(none)"} ({PhantomSuccessHint})";

    /// <summary>
    /// Decode <c>ScreenShotTimestamp</c> as unix seconds, unix ms, or FILETIME.
    /// Returns false when the value is 0 or does not look like a recent clock.
    /// </summary>
    public static bool TryEstimateAgeSeconds(long timestamp, DateTime utcNow, out double ageSeconds)
    {
        ageSeconds = 0;
        if (timestamp <= 0)
            return false;

        try
        {
            DateTime utc;
            if (timestamp > 10_000_000_000_000L)
                utc = DateTime.FromFileTimeUtc(timestamp);
            else if (timestamp > 20_000_000_000L)
                utc = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime;
            else
                utc = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime;

            ageSeconds = Math.Abs((utcNow - utc).TotalSeconds);
            return ageSeconds <= TimeSpan.FromDays(365).TotalSeconds;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// After a wait, force-clear when Requested is still set and either the
    /// Location path is empty/missing, or the request looks stale with no new file.
    /// </summary>
    public static bool ShouldForceClearStuckRequest(
        bool requested,
        string? location,
        long timestamp,
        bool newestFileFound,
        DateTime utcNow)
    {
        if (!requested)
            return false;

        if (IsLocationMissingOnDisk(location))
            return true;

        if (newestFileFound)
            return false;

        if (TryEstimateAgeSeconds(timestamp, utcNow, out var age))
            return age >= StaleRequestSeconds;

        // Timestamp unreadable, no new file, and we already waited — recover.
        return true;
    }
}
