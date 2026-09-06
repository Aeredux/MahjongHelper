using MahjongHelper.Mahjong;
using Xunit;

namespace MahjongHelper.Tests;

public class SmallTileClassifierTests
{
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
        Assert.Empty(classified.Where(c => c.Kind == SmallTileClassifier.Kind.PlayerMeld));
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
