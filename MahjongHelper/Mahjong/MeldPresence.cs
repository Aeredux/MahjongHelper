using System.Collections.Generic;
using System.Linq;

namespace MahjongHelper.Mahjong;

/// <summary>
/// Live ATK display presence for solver melds.
/// AgentEmj has no decoded per-seat fuuro list in this repo; AtkValues do not
/// enumerate opponent calls. The presence-correct source is nodes that are
/// on-screen this frame: <c>IsVisible</c> on the node <em>and</em> every
/// ancestor (same gate as stale call-button text). Leftover type-2 / 1056
/// scans that ignore hidden parents scavenge previous-round ghosts.
/// Opponent snap fields are live-only — an empty read must not keep cache.
/// </summary>
public static class MeldPresence
{
    public static bool IsOnScreen(bool selfVisible, IReadOnlyList<bool>? ancestorVisible)
    {
        if (!selfVisible)
            return false;
        if (ancestorVisible == null)
            return true;
        for (var i = 0; i < ancestorVisible.Count; i++)
        {
            if (!ancestorVisible[i])
                return false;
        }

        return true;
    }

    /// <summary>
    /// <paramref name="allowCache"/> is only for own hand-strip flicker.
    /// Opponent leftover is gated by on-screen nodes this frame.
    /// </summary>
    public static IReadOnlyList<ObservedMeld>? Resolve(
        IReadOnlyList<ObservedMeld>? observed,
        IReadOnlyList<ObservedMeld>? previous,
        bool allowCache)
    {
        if (observed is { Count: > 0 })
            return observed;
        if (allowCache && previous is { Count: > 0 })
            return previous;
        return null;
    }
}
