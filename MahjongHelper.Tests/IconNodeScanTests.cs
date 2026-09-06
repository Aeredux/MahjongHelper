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
}
