using MahjongHelper.Mahjong;
using Xunit;

namespace MahjongHelper.Tests;

public class MeldClassifierTests
{
    [Fact]
    public void InferMeld_keeps_aka_as_M0_in_pon()
    {
        var meld = MeldClassifier.InferMeld(["M5", "M0", "M5"]);
        Assert.NotNull(meld);
        Assert.Equal("PON", meld!.Type);
        Assert.Equal(["M5", "M0", "M5"], meld.Tiles);
    }

    [Fact]
    public void InferMeld_keeps_aka_as_S0_in_chi()
    {
        var meld = MeldClassifier.InferMeld(["S4", "S0", "S6"]);
        Assert.NotNull(meld);
        Assert.Equal("CHI", meld!.Type);
        Assert.Equal(["S4", "S0", "S6"], meld.Tiles);
    }

    [Fact]
    public void InferMeld_open_kan_keeps_aka()
    {
        var meld = MeldClassifier.InferMeld(["P5", "P5", "P0", "P5"]);
        Assert.NotNull(meld);
        Assert.Equal("KAN_OPEN", meld!.Type);
        Assert.Contains("P0", meld!.Tiles);
    }

    [Fact]
    public void SplitIntoMelds_groups_chi_then_pon()
    {
        var melds = MeldClassifier.SplitIntoMelds(["M1", "M2", "M3", "EAST", "EAST", "EAST"]);
        Assert.Equal(2, melds.Count);
        Assert.Equal("CHI", melds[0].Type);
        Assert.Equal("PON", melds[1].Type);
    }

    [Fact]
    public void SplitIntoMelds_pair_remainder_is_pon_when_already_classified()
    {
        var melds = MeldClassifier.SplitIntoMelds(
            ["WEST", "WEST", "WEST", "GREEN", "GREEN"], acceptPairRemainder: true);
        Assert.Equal(2, melds.Count);
        Assert.Equal("PON", melds[0].Type);
        Assert.Equal("PON", melds[1].Type);
        Assert.Equal(["GREEN", "GREEN"], melds[1].Tiles);
    }

    [Fact]
    public void SplitIntoMelds_does_not_invent_pons_from_pairs_by_default()
    {
        var melds = MeldClassifier.SplitIntoMelds(["GREEN", "GREEN"]);
        Assert.Empty(melds);
    }

    [Fact]
    public void IsUsableTile_rejects_placeholders()
    {
        Assert.True(MeldClassifier.IsUsableTile("M0"));
        Assert.False(MeldClassifier.IsUsableTile("?"));
        Assert.False(MeldClassifier.IsUsableTile("ICON_76075"));
        Assert.False(MeldClassifier.IsUsableTile(null));
    }
}
