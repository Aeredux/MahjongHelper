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
    public void Type_1045_34x45_hand_echo_is_never_fuuro()
    {
        var leftovers = new List<OpponentAreaClassifier.Tile>
        {
            Area(0, 0, 0, 200, 512, 34, 45, parent: 60, "M4", nodeType: 1045),
            Area(1, 34, 0, 234, 512, 34, 45, parent: 60, "M5", nodeType: 1045),
            Area(2, 68, 0, 268, 512, 34, 45, parent: 60, "P6", nodeType: 1045),
            Area(3, 102, 0, 302, 512, 34, 45, parent: 60, "P6", nodeType: 1045),
            Area(4, 136, 0, 336, 512, 34, 45, parent: 60, "S5", nodeType: 1045),
            Area(5, 170, 0, 370, 512, 34, 45, parent: 60, "S6", nodeType: 1045),
            Area(6, 204, 0, 404, 512, 34, 45, parent: 60, "S7", nodeType: 1045),
            Area(7, 0, 0, 520, 46, 40, 52, parent: 200, "S2", nodeType: 1031),
            Area(8, 40, 0, 560, 60, 52, 40, parent: 200, "S2", nodeType: 1031),
            Area(9, 80, 0, 600, 46, 40, 52, parent: 200, "S2", nodeType: 1031),
        };

        var assignments = OpponentAreaClassifier.Classify(leftovers, pondHints: null, playerStripAbsY: 972);
        Assert.DoesNotContain(assignments, a => a.Kind == SmallTileClassifier.Kind.LeftMeld);
        Assert.DoesNotContain(assignments, a => a.TileIds.Any(id => id <= 6));
        var pon = Assert.Single(assignments);
        Assert.Equal(SmallTileClassifier.Kind.OppositeMeld, pon.Kind);
        Assert.Equal([7, 8, 9], pon.TileIds);
    }

    [Fact]
    public void Leftover_icon_nodes_any_size_that_infermeld_are_fuuro()
    {
        // snap after 78fd2f1: no 42×55 / 55×42 leftovers. Fuuro must still
        // classify if icon-bearing nodes of another size are collected.
        var leftovers = new List<OpponentAreaClassifier.Tile>
        {
            Area(0, 0, 0, 820, 972, 40, 52, parent: 80, "S2", nodeType: 1031),
            Area(1, 40, 0, 860, 972, 40, 52, parent: 80, "S3", nodeType: 1031),
            Area(2, 80, 0, 900, 972, 40, 52, parent: 80, "S4", nodeType: 1031),
            Area(3, 0, 0, 520, 48, 40, 52, parent: 200, "S2", nodeType: 1031),
            Area(4, 40, 0, 560, 62, 52, 40, parent: 200, "S2", nodeType: 1031),
            Area(5, 80, 0, 600, 48, 40, 52, parent: 200, "S2", nodeType: 1031),
        };
        var ponds = new[]
        {
            new OpponentAreaClassifier.PondHint(SmallTileClassifier.Kind.PlayerDiscard, 400, 972),
            new OpponentAreaClassifier.PondHint(SmallTileClassifier.Kind.OppositeDiscard, 420, 180),
            new OpponentAreaClassifier.PondHint(SmallTileClassifier.Kind.RightDiscard, 820, 400),
            new OpponentAreaClassifier.PondHint(SmallTileClassifier.Kind.LeftDiscard, 90, 380),
        };

        var assignments = OpponentAreaClassifier.Classify(leftovers, ponds, playerStripAbsY: 972);
        var own = Assert.Single(assignments, a => a.Kind == SmallTileClassifier.Kind.PlayerMeld);
        var across = Assert.Single(assignments, a => a.Kind == SmallTileClassifier.Kind.OppositeMeld);
        Assert.Equal([0, 1, 2], own.TileIds);
        Assert.Equal([3, 4, 5], across.TileIds);
        Assert.DoesNotContain(assignments, a => a.Kind == SmallTileClassifier.Kind.LeftMeld);
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

    [Fact]
    public void Nested_image_nodes_any_type_still_seat_own_chi_and_across_pon()
    {
        // After 78fd2f1 the live sidecar had no 42×55 leftovers. Fuuro may
        // only appear as nested Image (type 2) or other wrappers once the
        // addon tree is deep-walked. The 7-tile type-1045 34×45 echo stays out.
        var leftovers = new List<OpponentAreaClassifier.Tile>
        {
            Area(0, 0, 0, 200, 512, 34, 45, parent: 60, "M4", nodeType: 1045),
            Area(1, 34, 0, 234, 512, 34, 45, parent: 60, "M5", nodeType: 1045),
            Area(2, 68, 0, 268, 512, 34, 45, parent: 60, "P6", nodeType: 1045),
            Area(3, 102, 0, 302, 512, 34, 45, parent: 60, "P6", nodeType: 1045),
            Area(4, 136, 0, 336, 512, 34, 45, parent: 60, "S5", nodeType: 1045),
            Area(5, 170, 0, 370, 512, 34, 45, parent: 60, "S6", nodeType: 1045),
            Area(6, 204, 0, 404, 512, 34, 45, parent: 60, "S7", nodeType: 1045),
            Area(7, 0, 0, 820, 972, 36, 48, parent: 80, "S2", nodeType: 2),
            Area(8, 36, 0, 856, 972, 36, 48, parent: 80, "S3", nodeType: 2),
            Area(9, 72, 0, 892, 972, 48, 36, parent: 80, "S4", nodeType: 2),
            Area(10, 0, 0, 520, 48, 36, 48, parent: 200, "S2", nodeType: 2),
            Area(11, 36, 0, 556, 62, 48, 36, parent: 200, "S2", nodeType: 2),
            Area(12, 72, 0, 592, 48, 36, 48, parent: 200, "S2", nodeType: 2),
        };
        var ponds = new[]
        {
            new OpponentAreaClassifier.PondHint(SmallTileClassifier.Kind.PlayerDiscard, 400, 972),
            new OpponentAreaClassifier.PondHint(SmallTileClassifier.Kind.OppositeDiscard, 420, 180),
            new OpponentAreaClassifier.PondHint(SmallTileClassifier.Kind.RightDiscard, 820, 400),
            new OpponentAreaClassifier.PondHint(SmallTileClassifier.Kind.LeftDiscard, 90, 380),
        };

        var assignments = OpponentAreaClassifier.Classify(leftovers, ponds, playerStripAbsY: 972);
        Assert.DoesNotContain(assignments, a => a.Kind == SmallTileClassifier.Kind.LeftMeld);
        Assert.DoesNotContain(assignments, a => a.TileIds.Any(id => id <= 6));
        var own = Assert.Single(assignments, a => a.Kind == SmallTileClassifier.Kind.PlayerMeld);
        var across = Assert.Single(assignments, a => a.Kind == SmallTileClassifier.Kind.OppositeMeld);
        Assert.Equal([7, 8, 9], own.TileIds);
        Assert.Equal([10, 11, 12], across.TileIds);
    }

    [Fact]
    public void Live_7ad3d5d_type2_faces_seat_own_and_across_without_echo_or_tray()
    {
        // snap-20260906-081032563: type-2 40×52 leaves. Across CHI P2/P3/P4.
        // Own PON WEST + CHI M1-M3 on the player band. AbsY≈500 echo and the
        // 192×52 type-1038 tray must not become opposite/left melds.
        var leftovers = new List<OpponentAreaClassifier.Tile>
        {
            Area(0, 0, 0, 1041, 410, 40, 52, parent: 300, "P2", nodeType: 2),
            Area(1, 0, 0, 1078, 396, 40, 52, parent: 300, "P3", nodeType: 2),
            Area(2, 0, 0, 1104, 430, 52, 40, parent: 300, "P4", nodeType: 2),
            Area(3, 0, 0, 1040, 396, 192, 52, parent: 300, "P2", nodeType: 1038),
            Area(4, 0, 0, 300, 500, 40, 52, parent: 60, "P6", nodeType: 2),
            Area(5, 0, 0, 340, 500, 40, 52, parent: 60, "M4", nodeType: 2),
            Area(6, 0, 0, 380, 500, 40, 52, parent: 60, "M5", nodeType: 2),
            Area(7, 0, 0, 420, 500, 40, 52, parent: 60, "P6", nodeType: 2),
            Area(8, 0, 0, 460, 500, 40, 52, parent: 60, "S5", nodeType: 2),
            Area(9, 0, 0, 500, 500, 40, 52, parent: 60, "S6", nodeType: 2),
            Area(10, 0, 0, 540, 500, 40, 52, parent: 60, "S7", nodeType: 2),
            Area(11, 0, 0, 580, 500, 40, 52, parent: 60, "WEST", nodeType: 2),
            Area(12, 0, 0, 620, 500, 40, 52, parent: 60, "WEST", nodeType: 2),
            Area(13, 0, 0, 660, 500, 40, 52, parent: 60, "GREEN", nodeType: 2),
            Area(14, 0, 0, 700, 500, 40, 52, parent: 60, "GREEN", nodeType: 2),
            Area(15, 0, 0, 740, 500, 40, 52, parent: 60, "M3", nodeType: 2),
            Area(16, 0, 0, 1391, 980, 40, 52, parent: 80, "WEST", nodeType: 2),
            Area(17, 0, 0, 1442, 980, 40, 52, parent: 80, "WEST", nodeType: 2),
            Area(18, 0, 0, 1484, 980, 40, 52, parent: 80, "WEST", nodeType: 2),
            Area(19, 0, 0, 1531, 980, 40, 52, parent: 81, "M2", nodeType: 2),
            Area(20, 0, 0, 1582, 980, 40, 52, parent: 81, "M1", nodeType: 2),
            Area(21, 0, 0, 1624, 980, 52, 40, parent: 81, "M3", nodeType: 2),
            Area(22, 0, 0, 1400, 1139, 40, 52, parent: 90, "WEST", nodeType: 2),
            Area(23, 0, 0, 1440, 1139, 40, 52, parent: 90, "WEST", nodeType: 2),
            Area(24, 0, 0, 1480, 1139, 40, 52, parent: 90, "M9", nodeType: 2),
            Area(25, 0, 0, 1520, 1139, 40, 52, parent: 90, "P5", nodeType: 2),
        };
        var ponds = new[]
        {
            new OpponentAreaClassifier.PondHint(SmallTileClassifier.Kind.PlayerDiscard, 400, 760),
            new OpponentAreaClassifier.PondHint(SmallTileClassifier.Kind.OppositeDiscard, 420, 180),
            new OpponentAreaClassifier.PondHint(SmallTileClassifier.Kind.RightDiscard, 820, 400),
            new OpponentAreaClassifier.PondHint(SmallTileClassifier.Kind.LeftDiscard, 90, 380),
        };

        var assignments = OpponentAreaClassifier.Classify(leftovers, ponds, playerStripAbsY: 972);
        Assert.DoesNotContain(assignments, a => a.Kind == SmallTileClassifier.Kind.LeftMeld);
        Assert.DoesNotContain(assignments, a => a.TileIds.Contains(3));
        Assert.DoesNotContain(assignments, a => a.TileIds.Any(id => id is >= 4 and <= 15));
        Assert.DoesNotContain(assignments, a => a.TileIds.Any(id => id is >= 22 and <= 25));

        var across = Assert.Single(assignments, a => a.Kind == SmallTileClassifier.Kind.OppositeMeld);
        Assert.Equal([0, 1, 2], across.TileIds);
        var acrossCodes = across.TileIds.Select(id => leftovers[id].TileCode!).ToList();
        Assert.Equal("CHI", MeldClassifier.InferMeld(acrossCodes)!.Type);

        var own = assignments.Where(a => a.Kind == SmallTileClassifier.Kind.PlayerMeld).ToList();
        Assert.Equal(2, own.Count);
        var ownCodes = own.Select(a => a.TileIds.Select(id => leftovers[id].TileCode!).ToList()).ToList();
        Assert.Contains(ownCodes, g => MeldClassifier.InferMeld(g)?.Type == "PON" && g.All(c => c == "WEST"));
        Assert.Contains(ownCodes, g => MeldClassifier.InferMeld(g)?.Type == "CHI");

        var summary = SolverJson.BuildSnapSummary(new SuggestMoveRequest
        {
            Hand = ["M4", "M6", "M7"],
            DrawnTile = "M4",
            SeatWind = "EAST",
            RoundWind = "EAST",
            Melds =
            [
                new MeldInfo { Type = "PON", Tiles = ["WEST", "WEST", "WEST"] },
                new MeldInfo { Type = "CHI", Tiles = ["M2", "M1", "M3"] },
            ],
            Opponents =
            [
                new OpponentInfo { Wind = "SOUTH" },
                new OpponentInfo
                {
                    Wind = "WEST",
                    Melds = [new MeldInfo { Type = "CHI", Tiles = ["P2", "P3", "P4"] }],
                },
                new OpponentInfo { Wind = "NORTH" },
            ],
        });
        Assert.Contains("ownMelds=2", summary);
        Assert.Contains("oppMelds=1", summary);
    }

    private static OpponentAreaClassifier.Tile Area(
        int id, float x, float y, float absX, float absY, int width, int height,
        uint parent, string code, ushort nodeType = 1055)
        => new(id, x, y, absX, absY, width, height, 0, parent, code, nodeType);
}
