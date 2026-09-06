using MahjongHelper.Mahjong;
using Xunit;

namespace MahjongHelper.Tests;

public class PondTsumogiriTrackerTests
{
    [Fact]
    public void Player_marks_new_tile_matching_last_draw_as_tsumogiri()
    {
        var tracker = new PondTsumogiriTracker();
        tracker.Update(PondTsumogiriTracker.Player, [], [], "S3");
        var flags = tracker.Update(PondTsumogiriTracker.Player, ["S3"], [false], "S3");
        Assert.True(Assert.Single(flags));
    }

    [Fact]
    public void Player_marks_newest_tile_when_pond_grows_by_more_than_one()
    {
        var tracker = new PondTsumogiriTracker();
        tracker.Update(PondTsumogiriTracker.Player, ["M1"], [false], "P2");
        var flags = tracker.Update(PondTsumogiriTracker.Player, ["M1", "EAST", "P2"], [false, false, false], "P2");
        Assert.Equal(3, flags.Count);
        Assert.False(flags[0]);
        Assert.False(flags[1]);
        Assert.True(flags[2]);
    }

    [Fact]
    public void Player_does_not_mark_mismatching_discard()
    {
        var tracker = new PondTsumogiriTracker();
        tracker.Update(PondTsumogiriTracker.Player, [], [], "S3");
        var flags = tracker.Update(PondTsumogiriTracker.Player, ["M1"], [false], "S3");
        Assert.False(Assert.Single(flags));
    }

    [Fact]
    public void Opponent_ui_rotation_persists_across_stable_prefix()
    {
        var tracker = new PondTsumogiriTracker();
        tracker.Update(PondTsumogiriTracker.Right, ["M2", "P4"], [false, true], null);
        var flags = tracker.Update(PondTsumogiriTracker.Right, ["M2", "P4", "S8"], [false, false, false], null);
        Assert.Equal([false, true, false], flags);
    }

    [Fact]
    public void Reset_clears_draw_history()
    {
        var tracker = new PondTsumogiriTracker();
        tracker.Update(PondTsumogiriTracker.Player, [], [], "S3");
        tracker.Reset();
        var flags = tracker.Update(PondTsumogiriTracker.Player, ["S3"], [false], null);
        Assert.False(Assert.Single(flags));
    }
}
