using System.Collections.Generic;
using System.Linq;

namespace MahjongHelper.Mahjong;

/// <summary>
/// Live ATK display presence for solver melds.
/// AgentEmj has no decoded per-seat fuuro list; AtkValues do not enumerate
/// opponent calls. Ancestor <c>IsVisible</c> is necessary but not sufficient:
/// leftover type-2 / 1056 icons stay self-visible after deal reset as
/// siblings of the hidden slot. The authoritative source is the dedicated
/// fuuro slot arrays. 1060 140×55 (own) hides when empty. 1061/1062/1063
/// at 86×35 are empty chrome and can stay Visible — only a populated
/// opponent slot (long side ≥100px) counts. West-seat NORTH is Right → 1062.
/// Own cache is only for 1060 flicker, never across a deal with no live slot.
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
