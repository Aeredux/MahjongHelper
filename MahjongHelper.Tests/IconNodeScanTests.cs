using MahjongHelper.Mahjong;
using Xunit;

namespace MahjongHelper.Tests;

public class IconNodeScanTests
{
    [Fact]
    public void Mahjong_icon_range_matches_doman_tile_ids()
    {
        Assert.True(IconNodeScan.IsMahjongTileIcon(76041));
        Assert.True(IconNodeScan.IsMahjongTileIcon(76077));
        Assert.True(IconNodeScan.IsMahjongTileIcon(76150));
        Assert.False(IconNodeScan.IsMahjongTileIcon(0));
        Assert.False(IconNodeScan.IsMahjongTileIcon(76040));
        Assert.False(IconNodeScan.IsMahjongTileIcon(76151));
    }

    [Fact]
    public void Type_1045_34x45_is_the_live_hand_echo()
    {
        Assert.True(IconNodeScan.IsType1045PondEcho(1045, 34, 45));
        Assert.True(IconNodeScan.IsType1045PondEcho(1045, 45, 34));
        Assert.False(IconNodeScan.IsType1045PondEcho(1045, 42, 55));
        Assert.False(IconNodeScan.IsType1045PondEcho(1045, 40, 52));
        Assert.False(IconNodeScan.IsType1045PondEcho(1031, 34, 45));
    }

    [Fact]
    public void RejectPondEcho_drops_only_the_34x45_1045_strip()
    {
        var nodes = new (ushort Type, int Width, int Height)[]
        {
            (1045, 34, 45),
            (1045, 42, 55),
            (1031, 40, 52),
        };
        var kept = IconNodeScan.RejectPondEcho(nodes, n => n.Type, n => n.Width, n => n.Height);
        Assert.Equal(2, kept.Count);
        Assert.DoesNotContain(kept, n => n.Type == 1045 && n.Width == 34);
    }

    [Fact]
    public void TileSized_is_a_discovery_window_not_a_fuuro_type()
    {
        Assert.True(IconNodeScan.IsTileSized(42, 55));
        Assert.True(IconNodeScan.IsTileSized(34, 45));
        Assert.True(IconNodeScan.IsTileSized(40, 52));
        Assert.True(IconNodeScan.IsTileSized(55, 42));
        Assert.False(IconNodeScan.IsTileSized(200, 80));
        Assert.False(IconNodeScan.IsTileSized(8, 8));
        Assert.False(IconNodeScan.IsTileSized(42, 200));
        Assert.False(IconNodeScan.IsTileSized(192, 52));
    }

    [Fact]
    public void Live_1060_tray_covers_m2_m1_m3_not_ghost_west()
    {
        var tray = new IconNodeScan.Tray(1525, 972, 140, 55);
        Assert.True(IconNodeScan.CenterInTray(1524, 980, 42, 55, tray));
        Assert.True(IconNodeScan.CenterInTray(1528, 980, 40, 52, tray));
        Assert.True(IconNodeScan.CenterInTray(1580, 980, 40, 52, tray));
        Assert.True(IconNodeScan.CenterInTray(1622, 980, 40, 52, tray));
        Assert.False(IconNodeScan.CenterInTray(1385, 980, 42, 55, tray));
        Assert.False(IconNodeScan.CenterInTray(1484, 980, 40, 52, tray));
    }

    [Fact]
    public void CollapseStackedDuplicates_merges_1524_1528_m2_pair()
    {
        var tiles = new (float X, string Code)[]
        {
            (1524, "M2"),
            (1528, "M2"),
            (1580, "M1"),
            (1622, "M3"),
        };
        var faces = IconNodeScan.CollapseStackedDuplicates(tiles, t => t.X, t => t.Code);
        Assert.Equal(3, faces.Count);
        Assert.Equal(["M2", "M1", "M3"], faces.Select(t => t.Code).ToList());
        Assert.Equal("CHI", MeldClassifier.InferMeld(faces.Select(t => t.Code).ToList())!.Type);
    }

    [Fact]
    public void FaceLeaf_is_type2_40x52_or_52x40()
    {
        Assert.True(IconNodeScan.IsFaceLeaf(2, 40, 52));
        Assert.True(IconNodeScan.IsFaceLeaf(2, 52, 40));
        Assert.False(IconNodeScan.IsFaceLeaf(2, 42, 55));
        Assert.False(IconNodeScan.IsFaceLeaf(1055, 40, 52));
        Assert.False(IconNodeScan.IsFaceLeaf(1038, 192, 52));
    }

    [Fact]
    public void Upright_type2_west_triple_has_no_call_cue()
    {
        var ghosts = new (ushort Type, int W, int H, float Rot)[]
        {
            (2, 40, 52, 0),
            (2, 40, 52, 0),
            (2, 40, 52, 0),
        };
        Assert.False(IconNodeScan.FaceLeafGroupHasCallCue(ghosts, t => t.Type, t => t.W, t => t.H, t => t.Rot));

        var chi = new (ushort Type, int W, int H, float Rot)[]
        {
            (2, 40, 52, 0),
            (2, 40, 52, 0),
            (2, 52, 40, 0),
        };
        Assert.True(IconNodeScan.FaceLeafGroupHasCallCue(chi, t => t.Type, t => t.W, t => t.H, t => t.Rot));

        var with1056 = new (ushort Type, int W, int H, float Rot)[]
        {
            (2, 40, 52, 0),
            (2, 40, 52, 0),
            (1056, 42, 55, 4.712f),
        };
        Assert.True(IconNodeScan.ClusterHasCallCue(with1056, t => t.W, t => t.H, t => t.Rot));
        Assert.True(IconNodeScan.IsCallCueNode(1056, 42, 55, 4.712f));
        Assert.True(IconNodeScan.IsFuuroTray(1060, 140, 55));
        Assert.True(IconNodeScan.IsFuuroSlot(1061, 86, 35));
        Assert.True(IconNodeScan.IsFuuroSlot(1062, 86, 35));
        Assert.True(IconNodeScan.IsFuuroSlot(1063, 86, 35));
        Assert.False(IconNodeScan.IsFuuroSlot(1055, 42, 55));
        Assert.Equal(SmallTileClassifier.Kind.PlayerMeld, IconNodeScan.FuuroSlotOwner(1060));
        Assert.Equal(SmallTileClassifier.Kind.LeftMeld, IconNodeScan.FuuroSlotOwner(1061));
        Assert.Equal(SmallTileClassifier.Kind.RightMeld, IconNodeScan.FuuroSlotOwner(1062));
        Assert.Equal(SmallTileClassifier.Kind.OppositeMeld, IconNodeScan.FuuroSlotOwner(1063));
        Assert.True(IconNodeScan.SeatHasLiveFuuroSlot(
            SmallTileClassifier.Kind.RightMeld, [new IconNodeScan.Tray(850, 300, 86, 35, 1062)]));
        Assert.False(IconNodeScan.SeatHasLiveFuuroSlot(
            SmallTileClassifier.Kind.RightMeld, [new IconNodeScan.Tray(1525, 972, 140, 55)]));
        Assert.False(IconNodeScan.SeatHasLiveFuuroSlot(SmallTileClassifier.Kind.LeftMeld, []));
        // Doman CHI: only the called tile is sideways. Two upright 40×52
        // in-hand leaves plus the 1056 cue are enough — M1/M3 must not be 52×40.
        Assert.True(IconNodeScan.IsFaceLeaf(2, 40, 52));
        Assert.False(IconNodeScan.HasCallCue(40, 52, 0));
    }

    [Fact]
    public void Shared_child_nodeid_does_not_collapse_distinct_north_faces()
    {
        // snap-20260906-100051365: three type-2 NORTH leaves all NodeId=4.
        const uint IconNorth = 76071;
        var faces = new (int NodeIndex, float AbsX, float AbsY)[]
        {
            (1002866, 1526, 972),
            (1002813, 1572, 984),
            (1002839, 1623, 972),
        };

        var keys = faces
            .Select(f => IconNodeScan.HandStripSlotKeyOf(4, f.NodeIndex, f.AbsX, f.AbsY, IconNorth))
            .ToList();
        Assert.Equal(3, keys.Distinct().Count());
        Assert.Single(keys.Select(k => k.NodeId).Distinct());

        var byNode = new Dictionary<IconNodeScan.HandStripSlotKey, (int NodeIndex, float AbsX, float AbsY)>();
        foreach (var face in faces)
            byNode[IconNodeScan.HandStripSlotKeyOf(4, face.NodeIndex, face.AbsX, face.AbsY, IconNorth)] = face;
        // type-1056 cue Abs (1568,984) shares LeafSnap with the middle type-2
        // (1572,984). NodeIndex must keep the cue as a fourth byNode entry.
        byNode[IconNodeScan.HandStripSlotKeyOf(4, 190, 1568, 984, IconNorth)] = (190, 1568, 984);
        Assert.Equal(4, byNode.Count);

        // Same node scanned twice still dedupes.
        var again = IconNodeScan.HandStripSlotKeyOf(4, 1002866, 1526, 972, IconNorth);
        Assert.Equal(keys[0], again);
    }

    [Fact]
    public void Upright_type2_dora_band_is_not_plausible_opposite_fuuro()
    {
        var dora = new (ushort Type, float X, float Y, int W, int H, float Rot)[]
        {
            (2, 1043, 396, 40, 52, 0),
            (2, 1069, 396, 40, 52, 0),
            (2, 1092, 430, 40, 52, 0),
        };
        Assert.False(IconNodeScan.IsPlausibleOppositeLeftoverFuuro(
            dora, trays: null, t => t.Type, t => t.W, t => t.H, t => t.Rot, t => t.X, t => t.Y));

        var chi = new (ushort Type, float X, float Y, int W, int H, float Rot)[]
        {
            (2, 1041, 410, 40, 52, 0),
            (2, 1078, 396, 40, 52, 0),
            (2, 1104, 430, 52, 40, 0),
        };
        Assert.True(IconNodeScan.IsPlausibleOppositeLeftoverFuuro(
            chi, trays: null, t => t.Type, t => t.W, t => t.H, t => t.Rot, t => t.X, t => t.Y));

        var handSized = new (ushort Type, float X, float Y, int W, int H, float Rot)[]
        {
            (1055, 480, 40, 42, 55, 0),
            (1055, 522, 40, 42, 55, 0),
            (1055, 564, 40, 42, 55, 0),
        };
        Assert.True(IconNodeScan.IsPlausibleOppositeLeftoverFuuro(
            handSized, trays: null, t => t.Type, t => t.W, t => t.H, t => t.Rot, t => t.X, t => t.Y));
    }

    [Fact]
    public void Upright_type2_honor_pon_is_plausible_opposite_dora_band_is_not()
    {
        var east = new (ushort Type, float X, float Y, int W, int H, float Rot, string Code)[]
        {
            (2, 1040, 410, 40, 52, 0, "EAST"),
            (2, 1078, 396, 40, 52, 0, "EAST"),
            (2, 1114, 430, 40, 52, 0, "EAST"),
        };
        Assert.True(IconNodeScan.IsPlausibleOppositeLeftoverFuuro(
            east, trays: null, t => t.Type, t => t.W, t => t.H, t => t.Rot, t => t.X, t => t.Y, t => t.Code));

        var dora = new (ushort Type, float X, float Y, int W, int H, float Rot, string Code)[]
        {
            (2, 1043, 396, 40, 52, 0, "M5"),
            (2, 1069, 396, 40, 52, 0, "M5"),
            (2, 1092, 430, 40, 52, 0, "M0"),
        };
        Assert.False(IconNodeScan.IsPlausibleOppositeLeftoverFuuro(
            dora, trays: null, t => t.Type, t => t.W, t => t.H, t => t.Rot, t => t.X, t => t.Y, t => t.Code));
    }

    [Fact]
    public void Ghost_west_cluster_is_not_plausible_own_fuuro()
    {
        var west = new (float X, float Y, int W, int H, float Rot)[]
        {
            (1385, 980, 42, 55, 4.712f),
            (1391, 980, 40, 52, 0),
            (1442, 980, 40, 52, 0),
            (1484, 980, 40, 52, 0),
        };
        var chi = new (float X, float Y, int W, int H, float Rot)[]
        {
            (1524, 980, 42, 55, 4.712f),
            (1528, 980, 40, 52, 0),
            (1580, 980, 40, 52, 0),
            (1622, 980, 40, 52, 0),
        };
        var trays = new[] { new IconNodeScan.Tray(1525, 972, 140, 55) };

        Assert.False(IconNodeScan.IsPlausibleOwnLeftoverFuuro(
            west, 1327, trays, t => t.W, t => t.H, t => t.Rot, t => t.X, t => t.Y));
        Assert.True(IconNodeScan.IsPlausibleOwnLeftoverFuuro(
            chi, 1327, trays, t => t.W, t => t.H, t => t.Rot, t => t.X, t => t.Y));
        Assert.False(IconNodeScan.IsPlausibleOwnLeftoverFuuro(
            chi, 1327, trays: null, t => t.W, t => t.H, t => t.Rot, t => t.X, t => t.Y));
    }

    [Fact]
    public void PreferLeafTiles_keeps_the_smaller_node_at_the_same_spot()
    {
        var nodes = new (uint Icon, float X, float Y, int W, int H)[]
        {
            (76050, 520, 46, 200, 80),
            (76050, 520, 46, 40, 52),
            (76050, 560, 46, 40, 52),
            (76050, 600, 46, 40, 52),
        };

        var kept = IconNodeScan.PreferLeafTiles(nodes, n => n.Icon, n => n.X, n => n.Y, n => n.W, n => n.H);
        Assert.Equal(3, kept.Count);
        Assert.DoesNotContain(kept, n => n.W == 200);
        Assert.All(kept, n => Assert.Equal(40, n.W));
    }

    [Fact]
    public void DropContainers_removes_a_tray_that_covers_three_tiles()
    {
        var nodes = new (float X, float Y, int W, int H)[]
        {
            (500, 40, 200, 80),
            (520, 46, 40, 52),
            (560, 46, 40, 52),
            (600, 46, 40, 52),
        };

        var kept = IconNodeScan.DropContainers(nodes, n => n.X, n => n.Y, n => n.W, n => n.H);
        Assert.Equal(3, kept.Count);
        Assert.DoesNotContain(kept, n => n.W == 200);
    }
}
