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
    public void Live_upright_42px_strip_peels_fuuro_right_of_draw()
    {
        // snap-20260906-071141803: all type 1055, Y=0, 42×55, no rotation, no ≥50px gap.
        // Closed 42–294, draw M5 at 304 (node 54), WEST 336/378/420, GREEN 462/504.
        var tiles = LiveUprightStrip(node54: true);

        var split = HandStripClassifier.Split(tiles);

        Assert.Equal(["M5", "P6", "P6", "S5", "S6", "S7", "S7"], Codes(tiles, split.ClosedIds));
        Assert.Equal(7, split.DrawId);
        Assert.Equal(2, split.MeldGroups.Count);
        Assert.Equal(["WEST", "WEST", "WEST"], Codes(tiles, split.MeldGroups[0]));
        Assert.Equal(["GREEN", "GREEN"], Codes(tiles, split.MeldGroups[1]));
        Assert.DoesNotContain("WEST", split.ClosedIds.Select(id => tiles[id].TileCode));
        Assert.DoesNotContain("GREEN", split.ClosedIds.Select(id => tiles[id].TileCode));

        var inferred = MeldClassifier.SplitIntoMelds(
            split.MeldGroups.SelectMany(g => Codes(tiles, g)).ToList(), acceptPairRemainder: true);
        Assert.Equal(2, inferred.Count);
        Assert.All(inferred, m => Assert.Equal("PON", m.Type));
        Assert.Equal("WEST", inferred[0].Tiles[0]);
        Assert.Equal("GREEN", inferred[1].Tiles[0]);
    }

    [Fact]
    public void Live_upright_42px_strip_peels_via_pitch_break_without_node_54()
    {
        var tiles = LiveUprightStrip(node54: false);

        var split = HandStripClassifier.Split(tiles);

        Assert.Equal(7, split.ClosedIds.Count);
        Assert.Equal(7, split.DrawId);
        Assert.Equal(["WEST", "WEST", "WEST"], Codes(tiles, split.MeldGroups[0]));
        Assert.Equal(["GREEN", "GREEN"], Codes(tiles, split.MeldGroups[1]));
    }

    [Fact]
    public void Leftmost_tile_at_x_zero_stays_in_closed_hand()
    {
        var tiles = new List<HandStripClassifier.Tile>
        {
            T(0, 0, "M1"),
            T(1, 42, "M2"),
            T(2, 84, "M3"),
            T(3, 126, "P1"),
            T(4, 168, "P2"),
            T(5, 210, "P3"),
            T(6, 252, "S1"),
            T(7, 294, "S2"),
            T(8, 336, "S3"),
            T(9, 378, "S4"),
            T(10, 420, "S6"),
            T(11, 462, "S7"),
            T(12, 504, "S8"),
            T(13, 556, "S9"),
        };

        var split = HandStripClassifier.Split(tiles);

        Assert.Contains(0, split.ClosedIds);
        Assert.Equal(13, split.DrawId);
        Assert.Empty(split.MeldGroups);
    }

    [Fact]
    public void Chi_to_the_right_of_draw_is_a_meld()
    {
        var tiles = Enumerable.Range(0, 10)
            .Select(i => T(i, 42 + i * 42, $"M{(i % 9) + 1}"))
            .ToList();
        tiles.Add(T(10, 42 + 10 * 42 + 10, "S5", nodeIndex: 54));
        tiles.Add(T(11, 42 + 11 * 42, "P2"));
        tiles.Add(T(12, 42 + 12 * 42, "P3"));
        tiles.Add(T(13, 42 + 13 * 42, "P4"));

        var split = HandStripClassifier.Split(tiles);

        Assert.Equal(10, split.ClosedIds.Count);
        Assert.Equal(10, split.DrawId);
        var chi = Assert.Single(split.MeldGroups);
        Assert.Equal(["P2", "P3", "P4"], Codes(tiles, chi));
    }

    [Fact]
    public void Mid_row_node_54_does_not_block_separated_chi_peel()
    {
        // snap after 2e9952b: 13 type-1055 closed tiles, draw node 54 at X=430
        // (mid-row overlay), own CHI type 1045 physically separated on the right.
        var tiles = new List<HandStripClassifier.Tile>();
        var closed = new[] { "M4", "M6", "M7", "M8", "M8", "M9", "S6", "S7", "S8", "S9", "P1", "P3", "P4" };
        for (var i = 0; i < closed.Length; i++)
        {
            var x = 42 + i * 42;
            tiles.Add(T(i, x, closed[i], nodeIndex: 59 + i, nodeType: 1055, absX: 200 + x, absY: 640, parent: 10));
        }

        tiles.Add(T(13, 430, "M4", nodeIndex: 54, nodeType: 1055, absX: 630, absY: 640, parent: 10));
        tiles.Add(T(14, 0, "S2", (float)(Math.PI / 2), parent: 80, nodeIndex: 90, width: 55, height: 42,
            nodeType: 1045, absX: 820, absY: 640));
        tiles.Add(T(15, 42, "S3", parent: 80, nodeIndex: 91, nodeType: 1045, absX: 862, absY: 640));
        tiles.Add(T(16, 84, "S4", parent: 80, nodeIndex: 92, nodeType: 1045, absX: 904, absY: 640));

        var split = HandStripClassifier.Split(tiles);

        Assert.Equal(13, split.ClosedIds.Count);
        Assert.Equal(13, split.DrawId);
        Assert.DoesNotContain("S2", Codes(tiles, split.ClosedIds));
        Assert.DoesNotContain("S3", Codes(tiles, split.ClosedIds));
        Assert.DoesNotContain("S4", Codes(tiles, split.ClosedIds));
        var chi = Assert.Single(split.MeldGroups);
        Assert.Equal(["S2", "S3", "S4"], Codes(tiles, chi));
    }

    [Fact]
    public void Type2_upright_west_ghosts_are_not_own_pon()
    {
        // snap-20260906-083929732: cue is type-1056 Rotation=4.712, not type-2
        // 52×40. Ghost WEST 1056@1385 + type-2@1391–1484 is not felt fuuro.
        // Real CHI uses 1056 M2@1525 and a 1060 140×55 tray.
        const float Rot270 = 4.712f;
        var tiles = new List<HandStripClassifier.Tile>();
        var closed = new[] { "M4", "M6", "M7", "M8", "M8", "M9", "S1", "S1", "S8", "S8", "M4" };
        for (var i = 0; i < closed.Length; i++)
        {
            var x = 907 + i * 42;
            tiles.Add(T(i, x, closed[i], nodeIndex: 59 + i, nodeType: 1055, width: 42, height: 55,
                absX: x, absY: 972, parent: 10));
        }

        tiles.Add(T(11, 0, "WEST", Rot270, parent: 80, nodeIndex: 190, width: 42, height: 55,
            nodeType: 1056, absX: 1385, absY: 980));
        tiles.Add(T(12, 0, "WEST", parent: 80, nodeIndex: 200, width: 40, height: 52,
            nodeType: 2, absX: 1391, absY: 980));
        tiles.Add(T(13, 0, "WEST", parent: 80, nodeIndex: 201, width: 40, height: 52,
            nodeType: 2, absX: 1442, absY: 980));
        tiles.Add(T(14, 0, "WEST", parent: 80, nodeIndex: 202, width: 40, height: 52,
            nodeType: 2, absX: 1484, absY: 980));
        tiles.Add(T(15, 0, "M2", Rot270, parent: 81, nodeIndex: 191, width: 42, height: 55,
            nodeType: 1056, absX: 1524, absY: 980));
        tiles.Add(T(16, 0, "M2", parent: 81, nodeIndex: 203, width: 40, height: 52,
            nodeType: 2, absX: 1528, absY: 980));
        tiles.Add(T(17, 0, "M1", parent: 81, nodeIndex: 204, width: 40, height: 52,
            nodeType: 2, absX: 1580, absY: 980));
        tiles.Add(T(18, 0, "M3", parent: 81, nodeIndex: 205, width: 40, height: 52,
            nodeType: 2, absX: 1622, absY: 980));

        var trays = new[] { new IconNodeScan.Tray(1525, 972, 140, 55) };
        var split = HandStripClassifier.Split(tiles, trays);

        Assert.Equal(11, split.ClosedIds.Count);
        var chi = Assert.Single(split.MeldGroups);
        var chiCodes = Codes(tiles, chi);
        Assert.Equal(3, chiCodes.Count);
        Assert.Contains("M1", chiCodes);
        Assert.Contains("M2", chiCodes);
        Assert.Contains("M3", chiCodes);
        Assert.DoesNotContain(split.MeldGroups, g => Codes(tiles, g).All(c => c == "WEST"));
    }

    [Fact]
    public void Live_e8c605e_1056_m2_pair_expands_to_chi_without_west_pon()
    {
        // After e8c605e the cued group was only 1056+type-2 M2 @1524–1528
        // (two UniqueX buckets). Sideways M2 is the called discard; upright
        // type-2 M1 @1580 and M3 @1622 were already in hand — normal Doman
        // CHI, not three sideways tiles. Pull those neighbors into the cue.
        const float Rot270 = 4.712f;
        var tiles = new List<HandStripClassifier.Tile>();
        var closed = new[] { "M4", "M6", "M7", "M8", "M8", "M9", "S1", "S1", "S8", "S8", "M4" };
        for (var i = 0; i < closed.Length; i++)
        {
            var x = 907 + i * 42;
            tiles.Add(T(i, x, closed[i], nodeIndex: 59 + i, nodeType: 1055, width: 42, height: 55,
                absX: x, absY: 972, parent: 10));
        }

        tiles.Add(T(11, 0, "WEST", Rot270, parent: 80, nodeIndex: 190, width: 42, height: 55,
            nodeType: 1056, absX: 1385, absY: 980));
        tiles.Add(T(12, 0, "WEST", parent: 80, nodeIndex: 200, width: 40, height: 52,
            nodeType: 2, absX: 1391, absY: 980));
        tiles.Add(T(13, 0, "WEST", parent: 80, nodeIndex: 201, width: 40, height: 52,
            nodeType: 2, absX: 1442, absY: 980));
        tiles.Add(T(14, 0, "WEST", parent: 80, nodeIndex: 202, width: 40, height: 52,
            nodeType: 2, absX: 1484, absY: 980));
        tiles.Add(T(15, 0, "M2", Rot270, parent: 81, nodeIndex: 191, width: 42, height: 55,
            nodeType: 1056, absX: 1524, absY: 980));
        tiles.Add(T(16, 0, "M2", parent: 81, nodeIndex: 203, width: 40, height: 52,
            nodeType: 2, absX: 1528, absY: 980));
        tiles.Add(T(17, 0, "M1", parent: 81, nodeIndex: 204, width: 40, height: 52,
            nodeType: 2, absX: 1580, absY: 980));
        tiles.Add(T(18, 0, "M3", parent: 81, nodeIndex: 205, width: 40, height: 52,
            nodeType: 2, absX: 1622, absY: 980));

        var trays = new[] { new IconNodeScan.Tray(1525, 972, 140, 55) };
        var split = HandStripClassifier.Split(tiles, trays);
        var chi = Assert.Single(split.MeldGroups);
        var chiCodes = Codes(tiles, chi);
        Assert.Equal(3, chiCodes.Count);
        Assert.Contains("M1", chiCodes);
        Assert.Contains("M2", chiCodes);
        Assert.Contains("M3", chiCodes);
        Assert.Equal("CHI", MeldClassifier.InferMeld(chiCodes)!.Type);
        Assert.False(HandStripClassifier.IsRotated(tiles[17]));
        Assert.False(HandStripClassifier.IsRotated(tiles[18]));
        Assert.Equal(40, tiles[17].Width);
        Assert.Equal(52, tiles[17].Height);
        Assert.Equal(40, tiles[18].Width);
        Assert.Equal(52, tiles[18].Height);
        Assert.DoesNotContain(split.MeldGroups, g => Codes(tiles, g).Contains("WEST"));

        var noTray = HandStripClassifier.Split(tiles, trays: null);
        var nearby = Assert.Single(noTray.MeldGroups);
        var nearbyCodes = Codes(tiles, nearby);
        Assert.Contains("M1", nearbyCodes);
        Assert.Contains("M2", nearbyCodes);
        Assert.Contains("M3", nearbyCodes);
        Assert.DoesNotContain(noTray.MeldGroups, g => Codes(tiles, g).All(c => c == "WEST"));
    }

    [Fact]
    public void Live_azpc_own_pon_north_1056_middle_is_player_meld()
    {
        // snap-20260906-095534536: own PON NORTH. Type-2 leaves at
        // Abs (1526,972)/(1572,984)/(1623,972), 1056 cue @1568 rot≈4.712,
        // 1055 upright neighbors on tray parent 112. Concealed NORTH at
        // AbsX 1368 (parent 133, node 59–71) stays in the closed hand.
        const float Rot270 = 4.712f;
        var tiles = new List<HandStripClassifier.Tile>();
        var closed = new[]
        {
            "M4", "M6", "M7", "M8", "M8", "M9", "S1", "S1", "S8", "S8", "M4", "P2", "NORTH",
        };
        for (var i = 0; i < closed.Length; i++)
        {
            var x = 1368 - (closed.Length - 1 - i) * 42;
            tiles.Add(T(i, x, closed[i], nodeIndex: 59 + i, nodeType: 1055, width: 42, height: 55,
                absX: x, absY: 972, parent: 133));
        }

        tiles.Add(T(13, 0, "NORTH", parent: 112, nodeIndex: 200, width: 42, height: 55,
            nodeType: 1055, absX: 1526, absY: 972));
        tiles.Add(T(14, 0, "NORTH", parent: 112, nodeIndex: 202, width: 42, height: 55,
            nodeType: 1055, absX: 1623, absY: 972));
        tiles.Add(T(15, 0, "NORTH", Rot270, parent: 112, nodeIndex: 190, width: 42, height: 55,
            nodeType: 1056, absX: 1568, absY: 984));
        tiles.Add(T(16, 0, "NORTH", parent: 112, nodeIndex: 210, width: 40, height: 52,
            nodeType: 2, absX: 1526, absY: 972));
        tiles.Add(T(17, 0, "NORTH", parent: 112, nodeIndex: 211, width: 40, height: 52,
            nodeType: 2, absX: 1572, absY: 984));
        tiles.Add(T(18, 0, "NORTH", parent: 112, nodeIndex: 212, width: 40, height: 52,
            nodeType: 2, absX: 1623, absY: 972));

        var trays = new[] { new IconNodeScan.Tray(1520, 968, 140, 55) };
        var split = HandStripClassifier.Split(tiles, trays);

        Assert.Equal(13, split.ClosedIds.Count);
        Assert.Contains(12, split.ClosedIds);
        Assert.Equal("NORTH", tiles[12].TileCode);
        Assert.Equal(1368, tiles[12].AbsX);
        Assert.DoesNotContain(13, split.ClosedIds);
        Assert.DoesNotContain(14, split.ClosedIds);

        var pon = Assert.Single(split.MeldGroups);
        var ponCodes = Codes(tiles, pon);
        Assert.Equal(3, ponCodes.Count);
        Assert.All(ponCodes, c => Assert.Equal("NORTH", c));
        Assert.Equal("PON", MeldClassifier.InferMeld(ponCodes)!.Type);
        Assert.DoesNotContain(12, pon);

        var noTray = HandStripClassifier.Split(tiles, trays: null);
        var noTrayPon = Assert.Single(noTray.MeldGroups);
        Assert.Equal(3, Codes(tiles, noTrayPon).Count);
        Assert.All(Codes(tiles, noTrayPon), c => Assert.Equal("NORTH", c));
        Assert.Contains(12, noTray.ClosedIds);
    }

    [Fact]
    public void Type2_pon_with_sideways_call_tile_is_kept()
    {
        var tiles = new List<HandStripClassifier.Tile>();
        for (var i = 0; i < 11; i++)
        {
            tiles.Add(T(i, 907 + i * 42, $"M{(i % 9) + 1}", nodeIndex: 59 + i, nodeType: 1055,
                width: 42, height: 55, absX: 907 + i * 42, absY: 972, parent: 10));
        }

        tiles.Add(T(11, 0, "WEST", parent: 80, nodeIndex: 200, width: 52, height: 40,
            nodeType: 2, absX: 1484, absY: 980));
        tiles.Add(T(12, 0, "WEST", parent: 80, nodeIndex: 201, width: 40, height: 52,
            nodeType: 2, absX: 1531, absY: 980));
        tiles.Add(T(13, 0, "WEST", parent: 80, nodeIndex: 202, width: 40, height: 52,
            nodeType: 2, absX: 1572, absY: 980));

        var split = HandStripClassifier.Split(tiles);
        var pon = Assert.Single(split.MeldGroups);
        Assert.Equal(["WEST", "WEST", "WEST"], Codes(tiles, pon));
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

    private static List<HandStripClassifier.Tile> LiveUprightStrip(bool node54)
        =>
        [
            T(0, 42, "M5"),
            T(1, 84, "P6"),
            T(2, 126, "P6"),
            T(3, 168, "S5"),
            T(4, 210, "S6"),
            T(5, 252, "S7"),
            T(6, 294, "S7"),
            T(7, 304, "M5", nodeIndex: node54 ? 54 : 0),
            T(8, 336, "WEST"),
            T(9, 378, "WEST"),
            T(10, 420, "WEST"),
            T(11, 462, "GREEN"),
            T(12, 504, "GREEN"),
        ];

    private static HandStripClassifier.Tile T(
        int id, float x, string code, float rotation = 0, uint parent = 1, int nodeIndex = 0,
        int width = 42, int height = 55, ushort nodeType = 0, float absX = 0, float absY = 0)
        => new(id, x, 0, width, height, rotation, parent, code, nodeIndex, absX, absY, nodeType);

    private static List<string> Codes(List<HandStripClassifier.Tile> tiles, IReadOnlyList<int> ids)
    {
        var byId = tiles.ToDictionary(t => t.Id);
        return ids.Select(id => byId[id].TileCode!).ToList();
    }
}
