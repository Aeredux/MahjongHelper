using MahjongHelper.Mahjong;
using Xunit;

namespace MahjongHelper.Tests;

public class SmallTileClassifierTests
{
    [Fact]
    public void Pond_west_honors_stay_discards_not_own_pon()
    {
        // Frozen table: one WEST each in opponent ponds is real. Do not steal
        // those 1022/1023/1024 tiles into a meld. Own PON WEST is the hand-row
        // type-1056/type-2 ghost cluster, not these river tiles.
        var tiles = new List<SmallTileClassifier.Tile>
        {
            Pond(0, 1022, 11, 0, 0, "S1", absX: 80, absY: 360),
            Pond(1, 1022, 11, 34, 0, "WEST", absX: 114, absY: 360),
            Pond(2, 1023, 20, 0, 0, "P8", absX: 800, absY: 400),
            Pond(3, 1023, 20, 34, 0, "WEST", absX: 834, absY: 400),
            Pond(4, 1024, 50, 0, 0, "P3", absX: 400, absY: 180),
            Pond(5, 1024, 50, 34, 0, "WEST", absX: 434, absY: 180),
        };

        var classified = SmallTileClassifier.Classify(tiles);

        Assert.Equal(2, classified.Count(c => c.Kind == SmallTileClassifier.Kind.LeftDiscard));
        Assert.Equal(2, classified.Count(c => c.Kind == SmallTileClassifier.Kind.RightDiscard));
        Assert.Equal(2, classified.Count(c => c.Kind == SmallTileClassifier.Kind.OppositeDiscard));
        Assert.Equal(3, classified.Count(c => c.Tile.TileCode == "WEST"));
        Assert.DoesNotContain(classified, c => c.Kind is SmallTileClassifier.Kind.PlayerMeld
            or SmallTileClassifier.Kind.LeftMeld
            or SmallTileClassifier.Kind.RightMeld
            or SmallTileClassifier.Kind.OppositeMeld);
        Assert.All(classified.Where(c => c.Tile.TileCode == "WEST"),
            c => Assert.True(c.Kind is SmallTileClassifier.Kind.LeftDiscard
                or SmallTileClassifier.Kind.RightDiscard
                or SmallTileClassifier.Kind.OppositeDiscard));
    }

    [Fact]
    public void Type_1024_group_is_opposite_pond()
    {
        var tiles = Enumerable.Range(0, 6)
            .Select(i => Pond(i, 1024, parent: 50, x: i * 34, y: 0, code: $"P{i + 1}"))
            .ToList();

        var classified = SmallTileClassifier.Classify(tiles);

        Assert.Equal(6, classified.Count);
        Assert.All(classified, c => Assert.Equal(SmallTileClassifier.Kind.OppositeDiscard, c.Kind));
        Assert.Equal("P6", classified[0].Tile.TileCode);
        Assert.Equal("P1", classified[^1].Tile.TileCode);
    }

    [Fact]
    public void Other_parent_of_same_type_is_that_seat_meld()
    {
        var tiles = new List<SmallTileClassifier.Tile>
        {
            Pond(0, 1024, 50, 0, 0, "P1"),
            Pond(1, 1024, 50, 34, 0, "P2"),
            Pond(2, 1024, 50, 68, 0, "P3"),
            Pond(3, 1024, 50, 102, 0, "P4"),
            Pond(4, 1024, 77, 0, 0, "M1"),
            Pond(5, 1024, 77, 34, 0, "M2"),
            Pond(6, 1024, 77, 68, 0, "M3"),
        };

        var classified = SmallTileClassifier.Classify(tiles);
        var pond = classified.Where(c => c.Kind == SmallTileClassifier.Kind.OppositeDiscard).ToList();
        var meld = classified.Where(c => c.Kind == SmallTileClassifier.Kind.OppositeMeld).ToList();

        Assert.Equal(4, pond.Count);
        Assert.Equal(3, meld.Count);
        Assert.Equal(["M1", "M2", "M3"], meld.Select(c => c.Tile.TileCode).ToList());
    }

    [Fact]
    public void Shared_parent_peels_fuuro_cluster_off_the_pond()
    {
        // Same type 1024 parent: 6-wide pond grid plus a CHI 200px away (hand-area).
        var tiles = new List<SmallTileClassifier.Tile>();
        for (var i = 0; i < 6; i++)
            tiles.Add(Pond(i, 1024, 50, i * 34, 0, $"P{i + 1}"));
        tiles.Add(Pond(6, 1024, 50, 0, -200, "M1"));
        tiles.Add(Pond(7, 1024, 50, 34, -200, "M2"));
        tiles.Add(Pond(8, 1024, 50, 68, -200, "M3"));

        var classified = SmallTileClassifier.Classify(tiles);
        var pond = classified.Where(c => c.Kind == SmallTileClassifier.Kind.OppositeDiscard).ToList();
        var meld = classified.Where(c => c.Kind == SmallTileClassifier.Kind.OppositeMeld).ToList();

        Assert.Equal(6, pond.Count);
        Assert.Equal(3, meld.Count);
        Assert.Equal(["M1", "M2", "M3"], meld.Select(c => c.Tile.TileCode).ToList());
        Assert.DoesNotContain(pond, c => c.Tile.TileCode is "M1" or "M2" or "M3");
    }

    [Fact]
    public void Compact_pond_is_not_split()
    {
        var tiles = Enumerable.Range(0, 6)
            .Select(i => Pond(i, 1021, 10, (i % 6) * 34, (i / 6) * 45, $"S{i + 1}"))
            .ToList();

        var classified = SmallTileClassifier.Classify(tiles);

        Assert.Equal(6, classified.Count);
        Assert.All(classified, c => Assert.Equal(SmallTileClassifier.Kind.PlayerDiscard, c.Kind));
        Assert.DoesNotContain(classified, c => c.Kind == SmallTileClassifier.Kind.PlayerMeld);
    }

    [Fact]
    public void Rotated_45x34_stays_in_pond_as_tsumogiri()
    {
        var tiles = new List<SmallTileClassifier.Tile>
        {
            Pond(0, 1021, 10, 0, 0, "E", tsumogiri: false),
            Pond(1, 1021, 10, 34, 0, "S", tsumogiri: true, width: 45, height: 34),
        };

        var classified = SmallTileClassifier.Classify(tiles);

        Assert.Equal(2, classified.Count);
        Assert.All(classified, c => Assert.Equal(SmallTileClassifier.Kind.PlayerDiscard, c.Kind));
        Assert.Contains(classified, c => c.Tile.TileCode == "S" && c.Tile.Tsumogiri);
    }

    [Fact]
    public void Type_1045_strip_is_not_a_left_meld()
    {
        // Live snap after 40b28b7: parent 60 type 1045 M4 M5 P6 P6 S5 S6 S7
        // at AbsY≈518 became LeftMeld; MeldClassifier then invented CHI S5 S6 S7.
        var tiles = new List<SmallTileClassifier.Tile>
        {
            Pond(0, 1022, 11, 0, 0, "M2", absX: 80, absY: 360),
            Pond(1, 1022, 11, 34, 0, "S8", absX: 114, absY: 360),
            Pond(2, 1045, 60, 0, 0, "M4", absX: 200, absY: 518),
            Pond(3, 1045, 60, 42, 0, "M5", absX: 242, absY: 518),
            Pond(4, 1045, 60, 84, 0, "P6", absX: 284, absY: 518),
            Pond(5, 1045, 60, 126, 0, "P6", absX: 326, absY: 518),
            Pond(6, 1045, 60, 168, 0, "S5", absX: 368, absY: 518),
            Pond(7, 1045, 60, 210, 0, "S6", absX: 410, absY: 518),
            Pond(8, 1045, 60, 252, 0, "S7", absX: 452, absY: 518),
        };

        var classified = SmallTileClassifier.Classify(tiles);

        Assert.DoesNotContain(classified, c => c.Kind == SmallTileClassifier.Kind.LeftMeld);
        Assert.DoesNotContain(classified, c => c.Tile.NodeType == 1045);
        Assert.Equal(2, classified.Count(c => c.Kind == SmallTileClassifier.Kind.LeftDiscard));
    }

    [Fact]
    public void Extra_1022_parent_pair_is_not_left_meld()
    {
        var tiles = new List<SmallTileClassifier.Tile>
        {
            Pond(0, 1022, 11, 0, 0, "P1"),
            Pond(1, 1022, 11, 34, 0, "P2"),
            Pond(2, 1022, 11, 68, 0, "P3"),
            Pond(3, 1022, 11, 102, 0, "P4"),
            Pond(4, 1022, 99, 0, 0, "M2", tsumogiri: true, width: 45, height: 34, absX: 1168, absY: 755),
            Pond(5, 1022, 99, 34, 0, "S8", tsumogiri: true, width: 45, height: 34, absX: 1168, absY: 790),
        };

        var classified = SmallTileClassifier.Classify(tiles);

        Assert.DoesNotContain(classified, c => c.Kind == SmallTileClassifier.Kind.LeftMeld);
        Assert.Equal(4, classified.Count(c => c.Kind == SmallTileClassifier.Kind.LeftDiscard));
    }

    [Fact]
    public void Type_1023_pond_plus_chi_cluster_is_right_meld()
    {
        var tiles = new List<SmallTileClassifier.Tile>
        {
            Pond(0, 1023, 20, 0, 0, "P8", absX: 800, absY: 400),
            Pond(1, 1023, 20, 0, 200, "S2", absX: 800, absY: 200, width: 45, height: 34),
            Pond(2, 1023, 20, 34, 200, "S3", absX: 834, absY: 200),
            Pond(3, 1023, 20, 68, 200, "S4", absX: 868, absY: 200),
        };

        var classified = SmallTileClassifier.Classify(tiles);
        var pond = classified.Where(c => c.Kind == SmallTileClassifier.Kind.RightDiscard).ToList();
        var meld = classified.Where(c => c.Kind == SmallTileClassifier.Kind.RightMeld).ToList();

        Assert.Single(pond);
        Assert.Equal("P8", pond[0].Tile.TileCode);
        Assert.Equal(["S2", "S3", "S4"], meld.Select(c => c.Tile.TileCode).ToList());
    }

    [Fact]
    public void Mixed_pond_cluster_peels_rotated_chi()
    {
        var tiles = new List<SmallTileClassifier.Tile>
        {
            Pond(0, 1023, 20, 0, 0, "P8"),
            Pond(1, 1023, 20, 40, 0, "S2", width: 45, height: 34),
            Pond(2, 1023, 20, 80, 0, "S3"),
            Pond(3, 1023, 20, 114, 0, "S4"),
        };

        var classified = SmallTileClassifier.Classify(tiles);
        var meld = classified.Where(c => c.Kind == SmallTileClassifier.Kind.RightMeld).ToList();
        var pond = classified.Where(c => c.Kind == SmallTileClassifier.Kind.RightDiscard).ToList();

        Assert.Equal(["S2", "S3", "S4"], meld.Select(c => c.Tile.TileCode).ToList());
        Assert.Equal("P8", Assert.Single(pond).Tile.TileCode);
    }

    [Fact]
    public void Leftover_other_type_group_goes_to_nearest_pond()
    {
        var tiles = new List<SmallTileClassifier.Tile>
        {
            Pond(0, 1023, 20, 800, 300, "P9", absX: 800, absY: 300),
            Pond(1, 1023, 20, 834, 300, "P8", absX: 834, absY: 300),
            Pond(2, 1031, 88, 790, 80, "EAST", absX: 790, absY: 80),
            Pond(3, 1031, 88, 824, 80, "EAST", absX: 824, absY: 80),
            Pond(4, 1031, 88, 858, 80, "EAST", absX: 858, absY: 80),
        };

        var classified = SmallTileClassifier.Classify(tiles);
        var meld = classified.Where(c => c.Kind == SmallTileClassifier.Kind.RightMeld).ToList();

        Assert.Equal(3, meld.Count);
        Assert.All(meld, c => Assert.Equal("EAST", c.Tile.TileCode));
    }

    private static SmallTileClassifier.Tile Pond(
        int id, ushort type, uint parent, float x, float y, string code,
        bool tsumogiri = false, int width = 34, int height = 45,
        float absX = 0, float absY = 0)
        => new(id, type, x, y, absX, absY, width, height, 0, parent, code, tsumogiri);
}
