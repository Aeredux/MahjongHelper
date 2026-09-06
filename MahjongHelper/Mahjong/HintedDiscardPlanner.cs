using System;
using System.Collections.Generic;
using System.Linq;

namespace MahjongHelper.Mahjong;

/// <summary>
/// Pure autoplay discard planner. No HWND / addon I/O.
///
/// Live Doman (2026-09-06): node 54 is the drawn 1055 on the strip, split out of
/// HandTiles. Callback 7 still owns that 14th tile — map it by X-order among
/// eligible closed slots plus the real draw. Type-1022 is only a draw visual,
/// not a callback 7 slot.
///
/// Callback 8 at atk0=6 is skip/pass (ATK unchanged if fired as a discard).
/// Never use callback 8 to unstick a discard, including legitimate tsumogiri.
/// </summary>
public static class HintedDiscardPlanner
{
    public const int MaxCallback7Pos = 13;
    public const ushort HandTileNodeType = 1055;
    public const ushort DrawVisualNodeType = 1022;

    public readonly record struct TileRef(
        int NodeIndex,
        ushort NodeType,
        uint IconId,
        string? TileCode,
        float X);

    public enum Kind
    {
        FireCallback7,
        MissingHint,
        NoSafeAction,
    }

    public readonly record struct Plan(
        Kind Kind,
        int? Callback7Pos,
        int? NodeIndex,
        string Reason);

    public static bool IsCallback7ClosedHandNode(int nodeIndex)
        => nodeIndex == 54 || nodeIndex is >= 59 and <= 71;

    /// <summary>
    /// ATK[0] values where FireCallback 7 is a tile discard.
    /// 30 = draw turn, 2 = after-call discard, 6 = after draw animation.
    /// </summary>
    public static bool IsDiscardReadyAtk(int atk0) => atk0 is 2 or 6 or 30;

    /// <summary>
    /// Callback 8 at atk0=6 is skip/pass, not tsumogiri — including during
    /// WaitingForDiscard after the draw animation. Live AZPC: FireCallback 8
    /// labeled tsumogiri left ATK unchanged.
    /// </summary>
    public static bool Callback8IsSkip(int atk0) => atk0 == 6;

    /// <summary>
    /// Autoplay must never fire callback 8 as a discard. atk0=6 is skip;
    /// firing 8 "only when discard-ready" still hits skip on the live atk0=6
    /// tsumogiri case. Use mapped callback 7 instead.
    /// </summary>
    public static bool Callback8IsSafeTsumogiri(int atk0, bool waitingForDiscard)
        => false;

    /// <summary>
    /// A real 14th hand tile (1055 / ULD node 54), not the type-1022 visual.
    /// </summary>
    public static bool IsRealHandDraw(TileRef draw)
        => IsCallback7ClosedHandNode(draw.NodeIndex) || draw.NodeType == HandTileNodeType;

    public static bool TileCodesMatch(string? uiCode, string? serverCode)
    {
        if (string.IsNullOrEmpty(uiCode) || string.IsNullOrEmpty(serverCode))
            return false;
        if (uiCode.Equals(serverCode, StringComparison.OrdinalIgnoreCase))
            return true;

        var normalizedUi = uiCode switch
        {
            "M0" => "M5",
            "P0" => "P5",
            "S0" => "S5",
            _ => uiCode
        };
        return normalizedUi.Equals(serverCode, StringComparison.OrdinalIgnoreCase);
    }

    public static bool MatchesHint(TileRef tile, string? tileCode, int? hintIconId)
    {
        if (hintIconId is > 0 && tile.IconId == (uint)hintIconId.Value)
            return true;
        return !string.IsNullOrEmpty(tileCode) && TileCodesMatch(tile.TileCode, tileCode);
    }

    /// <summary>
    /// Callback 7 handPos for a real draw that is not already in eligible 0-13.
    /// Sorts eligible closed tiles plus the draw by X (then node). The separated
    /// draw sits to the right, so 13 closed tiles → pos 13.
    /// </summary>
    public static int? MapRealDrawToCallback7Pos(IReadOnlyList<TileRef> eligible, TileRef draw)
    {
        if (!IsRealHandDraw(draw))
            return null;

        for (var i = 0; i < eligible.Count && i <= MaxCallback7Pos; i++)
        {
            if (eligible[i].NodeIndex == draw.NodeIndex)
                return i;
        }

        var ordered = eligible
            .Concat(new[] { draw })
            .OrderBy(t => t.X)
            .ThenBy(t => t.NodeIndex)
            .ToList();
        var pos = ordered.FindIndex(t => t.NodeIndex == draw.NodeIndex);
        if (pos is >= 0 and <= MaxCallback7Pos)
            return pos;
        return null;
    }

    public static Plan PlanHintedDiscard(
        string? tileCode,
        int? hintIconId,
        IReadOnlyList<TileRef> closedLogged,
        IReadOnlyList<TileRef> eligible,
        TileRef? drawn,
        bool allowUnhintedDrawn = false)
    {
        closedLogged ??= Array.Empty<TileRef>();
        eligible ??= Array.Empty<TileRef>();

        var attempts = new List<Plan>();

        void AddCallback7(int pos, int node, string reason)
        {
            if (pos is < 0 or > MaxCallback7Pos)
                return;
            if (attempts.Any(a => a.Callback7Pos == pos))
                return;
            attempts.Add(new Plan(Kind.FireCallback7, pos, node, reason));
        }

        var hasHint = !string.IsNullOrEmpty(tileCode) || hintIconId is > 0;
        if (hasHint)
        {
            foreach (var t in closedLogged)
            {
                if (!MatchesHint(t, tileCode, hintIconId))
                    continue;
                if (!IsCallback7ClosedHandNode(t.NodeIndex))
                    continue;

                var pos = IndexOfNode(eligible, t.NodeIndex);
                if (pos is >= 0 and <= MaxCallback7Pos)
                    AddCallback7(pos.Value, t.NodeIndex, $"hint-closed pos={pos} node={t.NodeIndex} code={t.TileCode} icon={t.IconId}");
            }

            for (var i = 0; i < eligible.Count && i <= MaxCallback7Pos; i++)
            {
                if (MatchesHint(eligible[i], tileCode, hintIconId))
                    AddCallback7(i, eligible[i].NodeIndex, $"hint-eligible pos={i} node={eligible[i].NodeIndex} code={eligible[i].TileCode} icon={eligible[i].IconId}");
            }

            if (drawn is { } draw && MatchesHint(draw, tileCode, hintIconId))
            {
                var viaClosed = IndexMatchingDrawCopy(eligible, draw);
                if (viaClosed is >= 0)
                {
                    AddCallback7(viaClosed.Value, eligible[viaClosed.Value].NodeIndex,
                        $"hint-drawn-via-closed pos={viaClosed} node={eligible[viaClosed.Value].NodeIndex} code={draw.TileCode}");
                }
                else
                {
                    var mapped = MapRealDrawToCallback7Pos(eligible, draw);
                    if (mapped is >= 0)
                    {
                        AddCallback7(mapped.Value, draw.NodeIndex,
                            $"hint-tsumogiri via mapped draw pos={mapped} node={draw.NodeIndex} code={draw.TileCode} type={draw.NodeType} (not callback 8)");
                    }
                }
            }
        }

        if (attempts.Count == 0 && allowUnhintedDrawn && drawn is { } unhinted)
        {
            var viaClosed = IndexMatchingDrawCopy(eligible, unhinted);
            if (viaClosed is >= 0)
            {
                AddCallback7(viaClosed.Value, eligible[viaClosed.Value].NodeIndex,
                    $"unhinted-drawn-via-closed pos={viaClosed} node={eligible[viaClosed.Value].NodeIndex} code={unhinted.TileCode} type={unhinted.NodeType}");
            }
            else
            {
                var mapped = MapRealDrawToCallback7Pos(eligible, unhinted);
                if (mapped is >= 0)
                {
                    AddCallback7(mapped.Value, unhinted.NodeIndex,
                        $"unhinted-tsumogiri via mapped draw pos={mapped} node={unhinted.NodeIndex} code={unhinted.TileCode} type={unhinted.NodeType} (not callback 8)");
                }
            }
        }

        if (attempts.Count > 0)
            return attempts[0];

        if (hasHint)
        {
            var hintMatchesDraw = drawn is { } d && MatchesHint(d, tileCode, hintIconId);
            if (hintMatchesDraw)
            {
                return new Plan(Kind.MissingHint, null, null,
                    $"hint '{tileCode}' matches draw node={drawn?.NodeIndex} type={drawn?.NodeType} but that tile is not a callback-7 hand slot — failing hint, not inventing a closed tile, not firing callback 8");
            }

            if (LooksLikeDiscardHand(eligible, drawn))
            {
                return new Plan(Kind.MissingHint, null, null,
                    $"hint '{tileCode}' icon={hintIconId?.ToString() ?? "(none)"} is not in closed hand and not the draw — failing hint, not looping, not inventing a closed tile, not firing callback 8");
            }

            return new Plan(Kind.NoSafeAction, null, null,
                $"hint '{tileCode}' not matched and hand snapshot looks incomplete (eligible={eligible.Count}) — not failing hint yet, not firing callback 8");
        }

        return new Plan(Kind.NoSafeAction, null, null,
            "no hint and no safe unhinted draw — not firing callback 8");
    }

    public static bool LooksLikeDiscardHand(IReadOnlyList<TileRef> eligible, TileRef? drawn)
        => eligible.Count >= 10 || (eligible.Count >= 7 && drawn != null);

    private static int? IndexOfNode(IReadOnlyList<TileRef> eligible, int nodeIndex)
    {
        for (var i = 0; i < eligible.Count && i <= MaxCallback7Pos; i++)
        {
            if (eligible[i].NodeIndex == nodeIndex)
                return i;
        }
        return null;
    }

    private static int? IndexMatchingDrawCopy(IReadOnlyList<TileRef> eligible, TileRef draw)
    {
        for (var i = 0; i < eligible.Count && i <= MaxCallback7Pos; i++)
        {
            var t = eligible[i];
            if (t.IconId != 0 && t.IconId == draw.IconId)
                return i;
            if (t.TileCode != null && draw.TileCode != null && TileCodesMatch(t.TileCode, draw.TileCode))
                return i;
        }
        return null;
    }
}
