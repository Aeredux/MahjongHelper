using MahjongHelper.Mahjong;
using Xunit;

namespace MahjongHelper.Tests;

public class ScreenshotCapturePathsTests
{
    [Fact]
    public void FileName_uses_utc_stamp()
    {
        var utc = new DateTime(2026, 9, 6, 12, 34, 56, 789, DateTimeKind.Utc);
        Assert.Equal("mj-20260906-123456789.png", ScreenshotCapturePaths.FileName(utc));
    }

    [Fact]
    public void FileName_normalizes_unspecified_as_utc()
    {
        var utc = new DateTime(2026, 1, 2, 3, 4, 5, 6, DateTimeKind.Unspecified);
        Assert.Equal("mj-20260102-030405006.png", ScreenshotCapturePaths.FileName(utc));
    }

    [Fact]
    public void PrimaryPath_is_under_appdata_screenshots()
    {
        var utc = new DateTime(2026, 9, 6, 0, 0, 0, DateTimeKind.Utc);
        var path = ScreenshotCapturePaths.PrimaryPath(utc);
        Assert.EndsWith(
            Path.Combine("MahjongHelper", "screenshots", "mj-20260906-000000000.png"),
            path);
        Assert.Equal("mj-20260906-000000000.png", Path.GetFileName(path));
    }

    [Fact]
    public void ExtraCopyPath_null_when_unset()
    {
        var utc = new DateTime(2026, 9, 6, 0, 0, 0, DateTimeKind.Utc);
        Assert.Null(ScreenshotCapturePaths.ExtraCopyPath(null, utc));
        Assert.Null(ScreenshotCapturePaths.ExtraCopyPath("  ", utc));
        Assert.Null(ScreenshotCapturePaths.ExtraCopyPath("", utc));
    }

    [Fact]
    public void ExtraCopyPath_joins_filename()
    {
        var utc = new DateTime(2026, 9, 6, 1, 2, 3, 4, DateTimeKind.Utc);
        Assert.Equal(
            Path.Combine(@"C:\shots", "mj-20260906-010203004.png"),
            ScreenshotCapturePaths.ExtraCopyPath(@"C:\shots", utc));
    }

    [Fact]
    public void MethodName_is_CaptureFallback()
    {
        Assert.Equal("CaptureFallback", ScreenshotCapturePaths.MethodName);
    }
}
