using MahjongHelper.Mahjong;
using Xunit;

namespace MahjongHelper.Tests;

public class OpponentAreaClassifierTests
{
    [Fact]
    public void Toimen_42x55_chi_is_opposite_meld()
    {
        // Screenshot geometry: player EAST, West/toimen CHI Man 1-2-3 sits
        // upright to the right of the face-down hand (top of the table), not
        // on the local 42×55 strip and not in the 34×45 pond.
        var leftovers = new List<OpponentAreaClassifier.Tile>
        {
            Area(0, 0, 0, 480, 40, 42, 55, parent: 200, "M1"),
            Area(1, 42, 0, 522, 40, 42, 55, parent: 200, "M2"),
            Area(2, 84, 0, 564, 40, 42, 55, parent: 200, "M3"),
        };
        var ponds = new[]
        {
            new OpponentAreaClassifier.PondHint(SmallTileClassifier.Kind.PlayerDiscard, 400, 620),
            new OpponentAreaClassifier.PondHint(SmallTileClassifier.Kind.OppositeDiscard, 420, 180),
            new OpponentAreaClassifier.PondHint(SmallTileClassifier.Kind.RightDiscard, 780, 360),
            new OpponentAreaClassifier.PondHint(SmallTileClassifier.Kind.LeftDiscard, 80, 360),
        };

        var assignments = OpponentAreaClassifier.Classify(leftovers, ponds, playerStripAbsY: 620);

        var chi = Assert.Single(assignments);
        Assert.Equal(SmallTileClassifier.Kind.OppositeMeld, chi.Kind);
        Assert.Equal([0, 1, 2], chi.TileIds);
    }

    [Fact]
    public void Toimen_chi_still_maps_without_pond_hints()
    {
        var leftovers = new List<OpponentAreaClassifier.Tile>
        {
            Area(0, 0, 0, 480, 40, 42, 55, parent: 200, "M1"),
            Area(1, 42, 0, 522, 40, 42, 55, parent: 200, "M2"),
            Area(2, 84, 0, 564, 40, 42, 55, parent: 200, "M3"),
        };

        var assignments = OpponentAreaClassifier.Classify(leftovers, pondHints: null, playerStripAbsY: 620);

        var chi = Assert.Single(assignments);
        Assert.Equal(SmallTileClassifier.Kind.OppositeMeld, chi.Kind);
    }

    [Fact]
    public void Player_strip_band_stays_player_meld()
    {
        var leftovers = new List<OpponentAreaClassifier.Tile>
        {
            Area(0, 336, 0, 336, 620, 42, 55, parent: 9, "WEST"),
            Area(1, 378, 0, 378, 620, 42, 55, parent: 9, "WEST"),
            Area(2, 420, 0, 420, 620, 42, 55, parent: 9, "WEST"),
        };

        var assignments = OpponentAreaClassifier.Classify(leftovers, pondHints: null, playerStripAbsY: 620);

        var pon = Assert.Single(assignments);
        Assert.Equal(SmallTileClassifier.Kind.PlayerMeld, pon.Kind);
    }

    [Fact]
    public void Left_and_right_55x42_go_to_correct_seats()
    {
        var leftovers = new List<OpponentAreaClassifier.Tile>
        {
            Area(0, 0, 0, 70, 340, 55, 42, parent: 301, "S1"),
            Area(1, 45, 0, 70, 385, 55, 42, parent: 301, "S2"),
            Area(2, 90, 0, 70, 430, 55, 42, parent: 301, "S3"),
            Area(3, 0, 0, 860, 340, 55, 42, parent: 302, "P7"),
            Area(4, 45, 0, 860, 385, 55, 42, parent: 302, "P8"),
            Area(5, 90, 0, 860, 430, 55, 42, parent: 302, "P9"),
        };
        var ponds = new[]
        {
            new OpponentAreaClassifier.PondHint(SmallTileClassifier.Kind.LeftDiscard, 90, 380),
            new OpponentAreaClassifier.PondHint(SmallTileClassifier.Kind.RightDiscard, 850, 380),
            new OpponentAreaClassifier.PondHint(SmallTileClassifier.Kind.OppositeDiscard, 420, 160),
            new OpponentAreaClassifier.PondHint(SmallTileClassifier.Kind.PlayerDiscard, 400, 640),
        };

        var assignments = OpponentAreaClassifier.Classify(leftovers, ponds, playerStripAbsY: 640);

        Assert.Equal(2, assignments.Count);
        var left = Assert.Single(assignments, a => a.Kind == SmallTileClassifier.Kind.LeftMeld);
        var right = Assert.Single(assignments, a => a.Kind == SmallTileClassifier.Kind.RightMeld);
        Assert.Equal([0, 1, 2], left.TileIds);
        Assert.Equal([3, 4, 5], right.TileIds);
    }

    [Fact]
    public void Mixed_upright_and_sideways_toimen_chi_is_still_opposite()
    {
        // Called tile in a toimen CHI is often the one rotated 90°.
        var leftovers = new List<OpponentAreaClassifier.Tile>
        {
            Area(0, 0, 0, 500, 36, 42, 55, parent: 200, "M1"),
            Area(1, 42, 0, 542, 50, 55, 42, parent: 200, "M2"),
            Area(2, 84, 0, 584, 36, 42, 55, parent: 200, "M3"),
        };

        var assignments = OpponentAreaClassifier.Classify(leftovers, pondHints: null, playerStripAbsY: 620);

        var chi = Assert.Single(assignments);
        Assert.Equal(SmallTileClassifier.Kind.OppositeMeld, chi.Kind);
    }

    [Fact]
    public void Singleton_hand_sized_tile_is_ignored()
    {
        var leftovers = new[] { Area(0, 0, 0, 500, 40, 42, 55, parent: 200, "M4") };

        Assert.Empty(OpponentAreaClassifier.Classify(leftovers, pondHints: null, playerStripAbsY: 620));
    }

    [Fact]
    public void Chi_choice_type_1009_is_ignored()
    {
        var leftovers = new List<OpponentAreaClassifier.Tile>
        {
            Area(0, 0, 0, 200, 400, 42, 55, parent: 12, "M2", nodeType: 1009),
            Area(1, 42, 0, 242, 400, 42, 55, parent: 12, "M3", nodeType: 1009),
            Area(2, 84, 0, 284, 400, 42, 55, parent: 12, "M4", nodeType: 1009),
        };

        Assert.Empty(OpponentAreaClassifier.Classify(leftovers, pondHints: null, playerStripAbsY: 620));
    }

    [Fact]
    public void Live_azpc_1045_junk_is_not_left_and_shimocha_chi_is_right()
    {
        // snap after 40b28b7: seat EAST, Cactuar/SOUTH has S2-S3-S4 CHI
        // (one called tile rotated). Capture left[] was type 1045 AbsY≈518
        // plus type 1022 pond leftovers; right[] was empty.
        var leftovers = new List<OpponentAreaClassifier.Tile>
        {
            Area(0, 0, 0, 200, 518, 42, 55, parent: 60, "M4", nodeType: 1045),
            Area(1, 42, 0, 242, 518, 42, 55, parent: 60, "M5", nodeType: 1045),
            Area(2, 84, 0, 284, 518, 42, 55, parent: 60, "P6", nodeType: 1045),
            Area(3, 126, 0, 326, 518, 42, 55, parent: 60, "P6", nodeType: 1045),
            Area(4, 168, 0, 368, 518, 42, 55, parent: 60, "S5", nodeType: 1045),
            Area(5, 210, 0, 410, 518, 42, 55, parent: 60, "S6", nodeType: 1045),
            Area(6, 252, 0, 452, 518, 42, 55, parent: 60, "S7", nodeType: 1045),
            Area(7, 0, 0, 1168, 755, 45, 34, parent: 99, "M2", nodeType: 1022),
            Area(8, 0, 40, 1168, 795, 45, 34, parent: 99, "S8", nodeType: 1022),
            Area(9, 0, 0, 880, 360, 55, 42, parent: 90, "S2", nodeType: 1055),
            Area(10, 0, 45, 880, 405, 42, 55, parent: 90, "S3", nodeType: 1055),
            Area(11, 0, 90, 880, 450, 42, 55, parent: 90, "S4", nodeType: 1055),
        };
        var ponds = new[]
        {
            new OpponentAreaClassifier.PondHint(SmallTileClassifier.Kind.PlayerDiscard, 400, 640),
            new OpponentAreaClassifier.PondHint(SmallTileClassifier.Kind.OppositeDiscard, 420, 180),
            new OpponentAreaClassifier.PondHint(SmallTileClassifier.Kind.RightDiscard, 820, 400),
            new OpponentAreaClassifier.PondHint(SmallTileClassifier.Kind.LeftDiscard, 90, 380),
        };

        var assignments = OpponentAreaClassifier.Classify(leftovers, ponds, playerStripAbsY: 640);

        Assert.DoesNotContain(assignments, a => a.Kind == SmallTileClassifier.Kind.LeftMeld);
        Assert.DoesNotContain(assignments, a => a.Kind == SmallTileClassifier.Kind.OppositeMeld);
        var right = Assert.Single(assignments, a => a.Kind == SmallTileClassifier.Kind.RightMeld);
        Assert.Equal([9, 10, 11], right.TileIds);

        IReadOnlyList<string> tiles = right.TileIds.Select(id => leftovers[id].TileCode!).ToList();
        var meld = Assert.Single(MeldClassifier.SplitIntoMelds(tiles));
        Assert.Equal("CHI", meld.Type);
        Assert.Equal(["S2", "S3", "S4"], meld.Tiles);

        var summary = SolverJson.BuildSnapSummary(new SuggestMoveRequest
        {
            Hand = ["M4", "M6", "M7"],
            DrawnTile = "M4",
            SeatWind = "EAST",
            RoundWind = "EAST",
            Opponents =
            [
                new OpponentInfo
                {
                    Wind = "SOUTH",
                    Melds = [new MeldInfo { Type = meld.Type, Tiles = meld.Tiles.ToList() }],
                },
                new OpponentInfo { Wind = "WEST" },
                new OpponentInfo { Wind = "NORTH" },
            ],
        });
        Assert.Contains("oppMelds=1", summary);
        Assert.Contains("ownMelds=0", summary);
    }

    [Fact]
    public void Type_1045_three_tile_chi_is_kept_when_it_infermelds()
    {
        var leftovers = new List<OpponentAreaClassifier.Tile>
        {
            Area(0, 0, 0, 520, 48, 42, 55, parent: 200, "S2", nodeType: 1045),
            Area(1, 42, 0, 562, 62, 55, 42, parent: 200, "S3", nodeType: 1045),
            Area(2, 84, 0, 604, 48, 42, 55, parent: 200, "S4", nodeType: 1045),
        };

        var chi = Assert.Single(OpponentAreaClassifier.Classify(leftovers, pondHints: null, playerStripAbsY: 640));
        Assert.Equal(SmallTileClassifier.Kind.OppositeMeld, chi.Kind);
        Assert.Equal([0, 1, 2], chi.TileIds);
    }

    [Fact]
    public void Live_azpc_2e9952b_own_across_right_chi_no_left()
    {
        // mj-20260906-074613460: EAST, own + toimen + shimocha CHI visible.
        // Type 1045 7-tile echo at AbsY≈518 must stay junk; lone 1021 ignored.
        var leftovers = new List<OpponentAreaClassifier.Tile>
        {
            Area(0, 0, 0, 200, 518, 42, 55, parent: 60, "M4", nodeType: 1045),
            Area(1, 42, 0, 242, 518, 42, 55, parent: 60, "M5", nodeType: 1045),
            Area(2, 84, 0, 284, 518, 42, 55, parent: 60, "P6", nodeType: 1045),
            Area(3, 126, 0, 326, 518, 42, 55, parent: 60, "P6", nodeType: 1045),
            Area(4, 168, 0, 368, 518, 42, 55, parent: 60, "S5", nodeType: 1045),
            Area(5, 210, 0, 410, 518, 42, 55, parent: 60, "S6", nodeType: 1045),
            Area(6, 252, 0, 452, 518, 42, 55, parent: 60, "S7", nodeType: 1045),
            Area(7, 0, 0, 1168, 755, 45, 34, parent: 99, "M2", nodeType: 1022),
            Area(8, 0, 40, 1168, 795, 45, 34, parent: 99, "S8", nodeType: 1022),
            Area(9, 0, 0, 90, 400, 34, 45, parent: 11, "P9", nodeType: 1021),
            Area(10, 0, 0, 820, 640, 55, 42, parent: 80, "S2", nodeType: 1045),
            Area(11, 42, 0, 862, 640, 42, 55, parent: 80, "S3", nodeType: 1045),
            Area(12, 84, 0, 904, 640, 42, 55, parent: 80, "S4", nodeType: 1045),
            Area(13, 0, 0, 520, 46, 42, 55, parent: 200, "S2", nodeType: 1045),
            Area(14, 42, 0, 562, 60, 55, 42, parent: 200, "S3", nodeType: 1045),
            Area(15, 84, 0, 604, 46, 42, 55, parent: 200, "S4", nodeType: 1045),
            Area(16, 0, 0, 880, 350, 55, 42, parent: 90, "S2", nodeType: 1045),
            Area(17, 0, 45, 880, 395, 42, 55, parent: 90, "S3", nodeType: 1045),
            Area(18, 0, 90, 880, 440, 42, 55, parent: 90, "S4", nodeType: 1045),
        };
        var ponds = new[]
        {
            new OpponentAreaClassifier.PondHint(SmallTileClassifier.Kind.PlayerDiscard, 400, 640),
            new OpponentAreaClassifier.PondHint(SmallTileClassifier.Kind.OppositeDiscard, 420, 180),
            new OpponentAreaClassifier.PondHint(SmallTileClassifier.Kind.RightDiscard, 820, 400),
            new OpponentAreaClassifier.PondHint(SmallTileClassifier.Kind.LeftDiscard, 90, 380),
        };

        var assignments = OpponentAreaClassifier.Classify(leftovers, ponds, playerStripAbsY: 640);

        Assert.DoesNotContain(assignments, a => a.Kind == SmallTileClassifier.Kind.LeftMeld);
        var own = Assert.Single(assignments, a => a.Kind == SmallTileClassifier.Kind.PlayerMeld);
        var across = Assert.Single(assignments, a => a.Kind == SmallTileClassifier.Kind.OppositeMeld);
        var right = Assert.Single(assignments, a => a.Kind == SmallTileClassifier.Kind.RightMeld);
        Assert.Equal([10, 11, 12], own.TileIds);
        Assert.Equal([13, 14, 15], across.TileIds);
        Assert.Equal([16, 17, 18], right.TileIds);

        var summary = SolverJson.BuildSnapSummary(new SuggestMoveRequest
        {
            Hand = ["M4", "M6", "M7", "M8", "M8", "M9", "S6", "S7", "S8", "S9", "P1", "P3", "P4"],
            DrawnTile = "M4",
            SeatWind = "EAST",
            RoundWind = "EAST",
            Melds = [new MeldInfo { Type = "CHI", Tiles = ["S2", "S3", "S4"] }],
            Opponents =
            [
                new OpponentInfo
                {
                    Wind = "SOUTH",
                    Melds = [new MeldInfo { Type = "CHI", Tiles = ["S2", "S3", "S4"] }],
                },
                new OpponentInfo
                {
                    Wind = "WEST",
                    Melds = [new MeldInfo { Type = "CHI", Tiles = ["S2", "S3", "S4"] }],
                },
                new OpponentInfo { Wind = "NORTH" },
            ],
        });
        Assert.Contains("ownMelds=1", summary);
        Assert.Contains("oppMelds=2", summary);
    }

    [Fact]
    public void Opposite_chi_feeds_solver_oppMelds_count()
    {
        var leftovers = new List<OpponentAreaClassifier.Tile>
        {
            Area(0, 0, 0, 480, 40, 42, 55, parent: 200, "M1"),
            Area(1, 42, 0, 522, 40, 42, 55, parent: 200, "M2"),
            Area(2, 84, 0, 564, 40, 42, 55, parent: 200, "M3"),
        };
        var assignment = Assert.Single(OpponentAreaClassifier.Classify(leftovers, pondHints: null, playerStripAbsY: 620));
        IReadOnlyList<string> tiles = assignment.TileIds.Select(id => leftovers[id].TileCode!).ToList();
        var meld = Assert.Single(MeldClassifier.SplitIntoMelds(tiles));

        var summary = SolverJson.BuildSnapSummary(new SuggestMoveRequest
        {
            Hand = ["M4", "M6", "M7"],
            DrawnTile = "M4",
            Dora = ["M4"],
            SeatWind = "EAST",
            RoundWind = "EAST",
            Melds = [new MeldInfo { Type = "CHI", Tiles = ["M2", "M3", "M4"] }],
            Opponents =
            [
                new OpponentInfo
                {
                    Wind = "WEST",
                    Melds = [new MeldInfo { Type = meld.Type, Tiles = meld.Tiles.ToList() }],
                },
            ],
        });

        Assert.Equal("CHI", meld.Type);
        Assert.Contains("ownMelds=1", summary);
        Assert.Contains("oppMelds=1", summary);
        Assert.Contains("seat=EAST", summary);
    }

    private static OpponentAreaClassifier.Tile Area(
        int id, float x, float y, float absX, float absY, int width, int height,
        uint parent, string code, ushort nodeType = 1055)
        => new(id, x, y, absX, absY, width, height, 0, parent, code, nodeType);
}
