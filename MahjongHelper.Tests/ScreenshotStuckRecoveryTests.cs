using MahjongHelper.Mahjong;
using Xunit;

namespace MahjongHelper.Tests;

public class ScreenshotStuckRecoveryTests
{
    [Fact]
    public void Empty_location_is_missing()
    {
        Assert.True(ScreenshotStuckRecovery.IsLocationMissingOnDisk(null));
        Assert.True(ScreenshotStuckRecovery.IsLocationMissingOnDisk(""));
        Assert.True(ScreenshotStuckRecovery.IsLocationMissingOnDisk("   "));
    }

    [Fact]
    public void Missing_path_is_missing_including_forward_slashes()
    {
        var missing = Path.Combine(Path.GetTempPath(), "mh-no-such-" + Guid.NewGuid().ToString("N") + ".png");
        Assert.True(ScreenshotStuckRecovery.IsLocationMissingOnDisk(missing));
        Assert.True(ScreenshotStuckRecovery.IsLocationMissingOnDisk(missing.Replace('\\', '/')));
    }

    [Fact]
    public void Existing_temp_file_is_not_missing()
    {
        var path = Path.Combine(Path.GetTempPath(), "mh-shot-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(path, [1, 2, 3]);
        try
        {
            Assert.False(ScreenshotStuckRecovery.IsLocationMissingOnDisk(path));
            Assert.False(ScreenshotStuckRecovery.IsLocationMissingOnDisk(path.Replace('\\', '/')));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Phantom_success_requires_success_and_missing_file()
    {
        Assert.True(ScreenshotStuckRecovery.IsPhantomSuccess("Success", @"C:\nope\ffxiv_09052026_170302_038.png"));
        Assert.False(ScreenshotStuckRecovery.IsPhantomSuccess("NoDiskSpace", @"C:\nope\x.png"));
        var path = Path.Combine(Path.GetTempPath(), "mh-shot-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(path, [1]);
        try
        {
            Assert.False(ScreenshotStuckRecovery.IsPhantomSuccess("Success", path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Phantom_message_names_location_and_reshade()
    {
        var msg = ScreenshotStuckRecovery.FormatPhantomSuccess(
            @"C:/Users/alvin/Documents/FF14Modding/Screenshots/ffxiv_09052026_170302_038.png");
        Assert.Contains("Result=Success but file missing at Location=", msg);
        Assert.Contains("ffxiv_09052026_170302_038.png", msg);
        Assert.Contains("ReShade/DXGI", msg);
    }

    [Fact]
    public void Unix_seconds_age_is_estimated()
    {
        var now = new DateTime(2026, 9, 6, 0, 18, 0, DateTimeKind.Utc);
        var ts = new DateTimeOffset(now.AddSeconds(-12)).ToUnixTimeSeconds();
        Assert.True(ScreenshotStuckRecovery.TryEstimateAgeSeconds(ts, now, out var age));
        Assert.InRange(age, 11, 13);
    }

    [Fact]
    public void Force_clear_when_requested_and_location_missing()
    {
        Assert.True(ScreenshotStuckRecovery.ShouldForceClearStuckRequest(
            requested: true,
            location: @"C:\Users\alvin\Documents\FF14Modding\Screenshots\ffxiv_09052026_170302_038.png",
            timestamp: 0,
            newestFileFound: false,
            utcNow: DateTime.UtcNow));
    }

    [Fact]
    public void No_force_clear_when_not_requested()
    {
        Assert.False(ScreenshotStuckRecovery.ShouldForceClearStuckRequest(
            requested: false,
            location: null,
            timestamp: 0,
            newestFileFound: false,
            utcNow: DateTime.UtcNow));
    }

    [Fact]
    public void Force_clear_when_stale_and_no_new_file()
    {
        var now = DateTime.UtcNow;
        var ts = new DateTimeOffset(now.AddSeconds(-20)).ToUnixTimeSeconds();
        var existing = Path.Combine(Path.GetTempPath(), "mh-old-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(existing, [1]);
        try
        {
            Assert.True(ScreenshotStuckRecovery.ShouldForceClearStuckRequest(
                requested: true,
                location: existing,
                timestamp: ts,
                newestFileFound: false,
                utcNow: now));
        }
        finally
        {
            try { File.Delete(existing); } catch { }
        }
    }

    [Fact]
    public void No_force_clear_when_new_file_exists_and_location_present()
    {
        var path = Path.Combine(Path.GetTempPath(), "mh-new-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(path, [1]);
        try
        {
            Assert.False(ScreenshotStuckRecovery.ShouldForceClearStuckRequest(
                requested: true,
                location: path,
                timestamp: new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds(),
                newestFileFound: true,
                utcNow: DateTime.UtcNow));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
