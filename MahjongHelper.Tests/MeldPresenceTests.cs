using MahjongHelper.Mahjong;
using Xunit;

namespace MahjongHelper.Tests;

public class MeldPresenceTests
{
    [Fact]
    public void IsOnScreen_requires_every_ancestor()
    {
        Assert.False(MeldPresence.IsOnScreen(true, [false]));
        Assert.False(MeldPresence.IsOnScreen(true, [true, false, true]));
        Assert.False(MeldPresence.IsOnScreen(false, [true]));
        Assert.True(MeldPresence.IsOnScreen(true, [true]));
        Assert.True(MeldPresence.IsOnScreen(true, [true, true]));
        Assert.True(MeldPresence.IsOnScreen(true, null));
    }

    [Fact]
    public void Resolve_drops_cached_opponent_ghosts_when_live_is_empty()
    {
        var previous = new[] { new ObservedMeld("PON", ["S9", "S9", "S9"]) };
        Assert.Null(MeldPresence.Resolve([], previous, allowCache: false));
        Assert.Null(MeldPresence.Resolve(null, previous, allowCache: false));
    }

    [Fact]
    public void Resolve_keeps_own_cache_through_strip_flicker()
    {
        var previous = new[] { new ObservedMeld("CHI", ["M1", "M2", "M3"]) };
        var cached = MeldPresence.Resolve([], previous, allowCache: true);
        Assert.Same(previous, cached);

        var live = new[] { new ObservedMeld("CHI", ["M4", "M5", "M6"]) };
        Assert.Same(live, MeldPresence.Resolve(live, previous, allowCache: false));
    }
}
