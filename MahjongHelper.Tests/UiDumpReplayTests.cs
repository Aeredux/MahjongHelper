using System.Reflection;
using MahjongHelper.Mahjong;
using Xunit;

namespace MahjongHelper.Tests;

public class UiDumpReplayTests
{
    [Fact]
    public void South_seat_west_chi_fixture_snaps_west_chi_and_own_pons()
    {
        var dump = Load("south-seat-west-chi-m4-m6.json");
        var result = UiDumpReplay.Classify(dump);

        Assert.Contains("ownMelds=2", result.SnapLines[0]);
        Assert.Contains("oppMelds=1", result.SnapLines[0]);
        Assert.Contains("seat=SOUTH", result.SnapLines[0]);
        Assert.Equal(
            "own=PON RED×3, PON WEST×3 | WEST=CHI M4-M5-M6 | NORTH=none | EAST=none",
            result.SnapLines[1]);
        Assert.Equal(dump.Expected?.SnapLine, result.SnapLines[1]);
    }

    [Fact]
    public void West_seat_north_pon_s2_p1_fixture_snaps_two_right_pons()
    {
        var dump = Load("west-seat-north-pon-s2-p1.json");
        var result = UiDumpReplay.Classify(dump);

        Assert.Contains("ownMelds=0", result.SnapLines[0]);
        Assert.Contains("oppMelds=2", result.SnapLines[0]);
        Assert.Contains("seat=WEST", result.SnapLines[0]);
        Assert.Equal(
            "own=none | NORTH=PON S2×3, PON P1×3 | EAST=none | SOUTH=none",
            result.SnapLines[1]);
        Assert.Equal(dump.Expected?.SnapLine, result.SnapLines[1]);
        var north = Assert.Single(result.Request.Opponents!, o => o.Wind == "NORTH");
        Assert.Equal(2, north.Melds?.Count);
        Assert.All(north.Melds!, m => Assert.Equal("PON", m.Type));
        Assert.Contains(north.Melds!, m => m.Tiles.TrueForAll(t => t == "S2"));
        Assert.Contains(north.Melds!, m => m.Tiles.TrueForAll(t => t == "P1"));
    }

    [Fact]
    public void West_seat_empty_table_fixture_is_all_none()
    {
        var dump = Load("west-seat-empty-table.json");
        var result = UiDumpReplay.Classify(dump);

        Assert.Contains("ownMelds=0", result.SnapLines[0]);
        Assert.Contains("oppMelds=0", result.SnapLines[0]);
        Assert.Equal("own=none | NORTH=none | EAST=none | SOUTH=none", result.SnapLines[1]);
        Assert.Equal(dump.Expected?.SnapLine, result.SnapLines[1]);
    }

    [Fact]
    public void Dump_ui_name_is_filesystem_safe()
    {
        Assert.Equal("south-seat-west-chi-m4-m6", UiDumpIO.SanitizeName("south-seat-west-chi-m4-m6"));
        Assert.Equal("south-seat-west", UiDumpIO.SanitizeName("south seat/west"));
    }

    private static UiDump Load(string fileName)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ui");
        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path))
        {
            var fallback = Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
                "Fixtures", "ui", fileName);
            path = File.Exists(fallback) ? fallback : FindInRepo(fileName);
        }

        return UiDumpIO.Load(path);
    }

    private static string FindInRepo(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "MahjongHelper.Tests", "Fixtures", "ui", fileName);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(fileName);
    }
}
