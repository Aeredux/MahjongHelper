using System;
using System.Collections.Generic;
using System.Linq;

namespace MahjongHelper.Mahjong;

/// <summary>
/// Assigns leftover hand-sized face-up tiles (42×55 / 55×42) that are not on
/// the local Y=0 strip to opponent (or leftover own) fuuro.
///
/// Live Doman: toimen CHI is upright beside that player's face-down hand.
/// Shimocha/kamicha CHI is a sideways stack (often one 55×42 called tile).
/// Type 1045 strips and pond leftovers (1021–1024) must not become fuuro.
/// </summary>
public static class OpponentAreaClassifier
{
    public const float PlayerStripBandPx = 80f;
    public const float ClusterGapPx = 70f;
    public const float RegionMarginPx = 80f;

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
            .Where(t => t.NodeType is not (1009 or 1006 or 1045))
            .ToList();
        if (usable.Count == 0)
            return result;

        var tableCenterX = TableCenterX(usable, pondHints);
        var tableCenterY = TableCenterY(pondHints, playerStripAbsY);

        foreach (var cluster in Cluster(usable, ClusterGapPx))
        {
            if (cluster.Count is < 2 or > 4)
                continue;

            var asSmall = cluster.Select(ToSmall).ToList();
            if (!SmallTileClassifier.LooksLikeOpenMeld(asSmall))
                continue;

            var kind = GuessOwner(cluster, pondHints, playerStripAbsY, tableCenterX, tableCenterY);
            result.Add(new Assignment(kind, cluster.Select(t => t.Id).ToList()));
        }

        return result;
    }

    internal static SmallTileClassifier.Kind GuessOwner(
        IReadOnlyList<Tile> group,
        IReadOnlyList<PondHint>? pondHints,
        float? playerStripAbsY,
        float tableCenterX = 400f,
        float tableCenterY = 400f)
    {
        var gx = group.Average(t => HasAbs(t) ? t.AbsX : t.X);
        var gy = group.Average(t => HasAbs(t) ? t.AbsY : t.Y);

        if (playerStripAbsY is float stripY && Math.Abs(gy - stripY) <= PlayerStripBandPx)
            return SmallTileClassifier.Kind.PlayerMeld;

        // Table regions beat nearest-pond (live AZPC: a mid-Y 1045 strip
        // sat closer to the left pond than the visible shimocha CHI).
        if (gy + RegionMarginPx < tableCenterY)
            return SmallTileClassifier.Kind.OppositeMeld;
        if (gx + RegionMarginPx < tableCenterX)
            return SmallTileClassifier.Kind.LeftMeld;
        if (gx > tableCenterX + RegionMarginPx)
            return SmallTileClassifier.Kind.RightMeld;

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

    private static float TableCenterY(IReadOnlyList<PondHint>? pondHints, float? playerStripAbsY)
    {
        if (pondHints is { Count: > 0 })
            return pondHints.Average(h => h.AbsY);
        if (playerStripAbsY is float stripY and > 0)
            return stripY * 0.5f;
        return 400f;
    }

    internal static List<List<Tile>> Cluster(List<Tile> tiles, float gapPx)
    {
        var remaining = tiles.ToList();
        var clusters = new List<List<Tile>>();
        while (remaining.Count > 0)
        {
            var cluster = new List<Tile> { remaining[0] };
            remaining.RemoveAt(0);
            var grew = true;
            while (grew)
            {
                grew = false;
                for (var i = remaining.Count - 1; i >= 0; i--)
                {
                    if (cluster.Any(t => Distance(t, remaining[i]) < gapPx))
                    {
                        cluster.Add(remaining[i]);
                        remaining.RemoveAt(i);
                        grew = true;
                    }
                }
            }

            clusters.Add(cluster.OrderBy(t => t.X).ThenBy(t => t.Y).ThenBy(t => t.Id).ToList());
        }

        return clusters;
    }

    private static SmallTileClassifier.Tile ToSmall(Tile t)
        => new(t.Id, t.NodeType, t.X, t.Y, t.AbsX, t.AbsY, t.Width, t.Height,
            t.Rotation, t.ParentNodeId, t.TileCode);

    private static float Distance(Tile a, Tile b)
    {
        var useAbs = HasAbs(a) || HasAbs(b);
        var dx = useAbs ? a.AbsX - b.AbsX : a.X - b.X;
        var dy = useAbs ? a.AbsY - b.AbsY : a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static bool HasAbs(Tile tile) => tile.AbsX != 0 || tile.AbsY != 0;
}
