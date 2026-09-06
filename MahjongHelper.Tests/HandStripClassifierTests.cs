using MahjongHelper.Mahjong;
using Xunit;

namespace MahjongHelper.Tests;

public class HandStripClassifierTests
{
    private const float Rot90 = (float)(Math.PI / 2);

    [Fact]
    public void Azpc_west_stack_and_green_pair_leave_closed_hand()
    {
        // Live snap-20260906-062025475: honors sit on the Y=0 42×55 strip.
        // WEST at 336/378/378/420 (called tile stacked), GREEN at 462/504.
        var tiles = new List<HandStripClassifier.Tile>
        {
            T(0, 42, "M4"),
            T(1, 84, "M0"),
            T(2, 126, "P2"),
            T(3, 168, "P2"),
            T(4, 210, "P3"),
            T(5, 252, "P3"),
            T(6, 294, "P3"),
            T(7, 336, "WEST"),
            T(8, 378, "WEST"),
            T(9, 378, "WEST", Rot90),
            T(10, 420, "WEST"),
            T(11, 462, "GREEN"),
            T(12, 504, "GREEN"),
        };

        var split = HandStripClassifier.Split(tiles);

        Assert.Equal([0, 1, 2, 3, 4, 5, 6], split.ClosedIds);
        Assert.Null(split.DrawId);
        Assert.Equal(2, split.MeldGroups.Count);
        Assert.Equal(["WEST", "WEST", "WEST"], Codes(tiles, split.MeldGroups[0]));
        Assert.Equal(["GREEN", "GREEN"], Codes(tiles, split.MeldGroups[1]));
    }

    [Fact]
    public void Closed_pon_without_cue_stays_in_hand()
    {
        var tiles = Enumerable.Range(0, 10)
            .Select(i => T(i, 42 + i * 42, $"M{(i % 9) + 1}"))
            .ToList();
        tiles.Add(T(10, 462, "WEST"));
        tiles.Add(T(11, 504, "WEST"));
        tiles.Add(T(12, 546, "WEST"));

        var split = HandStripClassifier.Split(tiles);

        Assert.Equal(13, split.ClosedIds.Count);
        Assert.Empty(split.MeldGroups);
        Assert.Contains(10, split.ClosedIds);
        Assert.Contains(12, split.ClosedIds);
    }

    [Fact]
    public void Closed_green_pair_without_cue_stays_in_hand()
    {
        var tiles = Enumerable.Range(0, 11)
            .Select(i => T(i, 42 + i * 42, $"P{(i % 9) + 1}"))
            .ToList();
        tiles.Add(T(11, 504, "GREEN"));
        tiles.Add(T(12, 546, "GREEN"));

        var split = HandStripClassifier.Split(tiles);

        Assert.Equal(13, split.ClosedIds.Count);
        Assert.Empty(split.MeldGroups);
    }

    [Fact]
    public void Draw_gap_after_thirteen_is_not_a_meld()
    {
        var tiles = Enumerable.Range(0, 13)
            .Select(i => T(i, 42 + i * 42, $"S{(i % 9) + 1}"))
            .ToList();
        tiles.Add(T(13, 42 + 13 * 42 + 20, "M4")); // 52px gap after last closed

        var split = HandStripClassifier.Split(tiles);

        Assert.Equal(13, split.ClosedIds.Count);
        Assert.Equal(13, split.DrawId);
        Assert.Empty(split.MeldGroups);
    }

    [Fact]
    public void Rotated_chi_on_the_right_is_a_meld()
    {
        var tiles = Enumerable.Range(0, 10)
            .Select(i => T(i, 42 + i * 42, $"M{(i % 9) + 1}"))
            .ToList();
        tiles.Add(T(10, 504, "P2"));
        tiles.Add(T(11, 546, "P3", Rot90));
        tiles.Add(T(12, 588, "P4"));

        var split = HandStripClassifier.Split(tiles);

        Assert.Equal(10, split.ClosedIds.Count);
        var chi = Assert.Single(split.MeldGroups);
        Assert.Equal(["P2", "P3", "P4"], Codes(tiles, chi));
    }

    [Fact]
    public void Other_parent_group_is_treated_as_melds()
    {
        var tiles = Enumerable.Range(0, 10)
            .Select(i => T(i, 42 + i * 42, $"S{(i % 9) + 1}", parent: 10))
            .ToList();
        tiles.Add(T(10, 504, "EAST", parent: 22));
        tiles.Add(T(11, 546, "EAST", parent: 22));
        tiles.Add(T(12, 588, "EAST", parent: 22));

        var split = HandStripClassifier.Split(tiles);

        Assert.Equal(10, split.ClosedIds.Count);
        var pon = Assert.Single(split.MeldGroups);
        Assert.Equal(["EAST", "EAST", "EAST"], Codes(tiles, pon));
    }

    [Fact]
    public void Azpc_strip_feeds_pon_plus_pair_remainder_into_classifier()
    {
        var tiles = new List<HandStripClassifier.Tile>
        {
            T(0, 42, "M4"), T(1, 84, "M0"), T(2, 126, "P2"), T(3, 168, "P2"),
            T(4, 210, "P3"), T(5, 252, "P3"), T(6, 294, "P3"),
            T(7, 336, "WEST"), T(8, 378, "WEST"), T(9, 378, "WEST", Rot90), T(10, 420, "WEST"),
            T(11, 462, "GREEN"), T(12, 504, "GREEN"),
        };
        var split = HandStripClassifier.Split(tiles);
        var meldTiles = split.MeldGroups.SelectMany(g => Codes(tiles, g)).ToList();
        var melds = MeldClassifier.SplitIntoMelds(meldTiles, acceptPairRemainder: true);

        Assert.Equal(7, split.ClosedIds.Count);
        Assert.DoesNotContain("WEST", split.ClosedIds.Select(id => tiles[id].TileCode));
        Assert.DoesNotContain("GREEN", split.ClosedIds.Select(id => tiles[id].TileCode));
        Assert.Equal(2, melds.Count);
        Assert.All(melds, m => Assert.Equal("PON", m.Type));
    }

    [Fact]
    public void Overlapping_suited_tiles_at_same_x_are_not_a_meld_cue()
    {
        // dump/uistate: P2 and P3 both at X=168 (placeholder + live).
        var tiles = new List<HandStripClassifier.Tile>
        {
            T(0, 42, "M4"),
            T(1, 84, "M5"),
            T(2, 126, "M8"),
            T(3, 168, "P2"),
            T(4, 168, "P3"),
            T(5, 210, "P6"),
            T(6, 252, "P6"),
            T(7, 294, "P8"),
            T(8, 336, "S5"),
            T(9, 378, "S0"),
            T(10, 420, "S6"),
            T(11, 462, "SOUTH"),
            T(12, 504, "GREEN"),
            T(13, 556, "WEST"),
        };

        var split = HandStripClassifier.Split(tiles);

        Assert.Equal(13, split.DrawId);
        Assert.Empty(split.MeldGroups);
        Assert.DoesNotContain(13, split.ClosedIds);
    }

    private static HandStripClassifier.Tile T(
        int id, float x, string code, float rotation = 0, uint parent = 1)
        => new(id, x, 0, 42, 55, rotation, parent, code);

    private static List<string> Codes(List<HandStripClassifier.Tile> tiles, IReadOnlyList<int> ids)
    {
        var byId = tiles.ToDictionary(t => t.Id);
        return ids.Select(id => byId[id].TileCode!).ToList();
    }
}
