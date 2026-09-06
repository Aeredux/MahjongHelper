using System;
using System.Collections.Generic;
using System.Linq;

namespace MahjongHelper.Mahjong;

/// <summary>
/// Assigns leftover hand-sized face-up tiles (42×55 / 55×42) that are not on
/// the local Y=0 strip to opponent (or leftover own) fuuro.
///
/// Live Doman: toimen CHI sits upright to the right of that player's
/// face-down hand — same size as our closed tiles, not 34×45 pond tiles —
/// so <see cref="SmallTileClassifier"/> never sees them. Kamicha/shimocha
/// fuuro are the sideways 55×42 equivalent.
/// </summary>
public static class OpponentAreaClassifier
{
    public const float PlayerStripBandPx = 80f;

    public readonly record struct Tile(
        int Id,
        float X,
        float Y,
        float AbsX,
        float AbsY,
        int Width,
        int Height,
        float Rotation,
        uint ParentNodeId,
        string? TileCode,
        ushort NodeType = 0,
        int NodeIndex = 0);

    public readonly record struct PondHint(SmallTileClassifier.Kind PondKind, float AbsX, float AbsY);

    public sealed record Assignment(SmallTileClassifier.Kind Kind, IReadOnlyList<int> TileIds);

    public static IReadOnlyList<Assignment> Classify(
        IReadOnlyList<Tile> leftovers,
        IReadOnlyList<PondHint>? pondHints = null,
        float? playerStripAbsY = null)
    {
        var result = new List<Assignment>();
        if (leftovers == null || leftovers.Count == 0)
            return result;

        var usable = leftovers
            .Where(t => MeldClassifier.IsUsableTile(t.TileCode))
            .Where(t => t.NodeType is not (1009 or 1006))
            .ToList();
        if (usable.Count == 0)
            return result;

        var tableCenterX = TableCenterX(usable, pondHints);

        foreach (var group in usable.GroupBy(t => t.ParentNodeId))
        {
            var members = group.OrderBy(t => t.X).ThenBy(t => t.Y).ThenBy(t => t.Id).ToList();
            if (members.Count < 2)
                continue;

            var kind = GuessOwner(members, pondHints, playerStripAbsY, tableCenterX);
            result.Add(new Assignment(kind, members.Select(t => t.Id).ToList()));
        }

        return result;
    }

    internal static SmallTileClassifier.Kind GuessOwner(
        IReadOnlyList<Tile> group,
        IReadOnlyList<PondHint>? pondHints,
        float? playerStripAbsY,
        float tableCenterX = 400f)
    {
        var gx = group.Average(t => HasAbs(t) ? t.AbsX : t.X);
        var gy = group.Average(t => HasAbs(t) ? t.AbsY : t.Y);

        if (playerStripAbsY is float stripY && Math.Abs(gy - stripY) <= PlayerStripBandPx)
            return SmallTileClassifier.Kind.PlayerMeld;

        if (pondHints is { Count: > 0 })
        {
            PondHint? nearest = null;
            var best = float.MaxValue;
            foreach (var hint in pondHints)
            {
                var dx = gx - hint.AbsX;
                var dy = gy - hint.AbsY;
                var dist = dx * dx + dy * dy;
                if (dist < best)
                {
                    best = dist;
                    nearest = hint;
                }
            }

            if (nearest is { } pond)
                return SmallTileClassifier.MeldKindForPond(pond.PondKind);
        }

        var upright = group.Count(t => t.Width <= t.Height);
        if (upright * 2 >= group.Count)
            return SmallTileClassifier.Kind.OppositeMeld;

        return gx < tableCenterX
            ? SmallTileClassifier.Kind.LeftMeld
            : SmallTileClassifier.Kind.RightMeld;
    }

    private static float TableCenterX(List<Tile> leftovers, IReadOnlyList<PondHint>? pondHints)
    {
        if (pondHints is { Count: > 0 })
            return pondHints.Average(h => h.AbsX);

        var xs = leftovers.Select(t => HasAbs(t) ? t.AbsX : t.X).ToList();
        var distinctParents = leftovers.Select(t => t.ParentNodeId).Distinct().Count();
        if (distinctParents >= 2 && xs.Count >= 2)
            return xs.Average();

        return 400f;
    }

    private static bool HasAbs(Tile tile) => tile.AbsX != 0 || tile.AbsY != 0;
}
