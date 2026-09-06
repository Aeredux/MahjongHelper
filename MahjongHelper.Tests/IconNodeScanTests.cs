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
