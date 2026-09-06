using System;
using System.Collections.Generic;
using System.Linq;

namespace MahjongHelper.Mahjong;

/// <summary>
/// Assigns leftover face-up tile nodes (any size that carries a mahjong icon)
/// that are not on the local closed-hand pack to opponent or leftover own fuuro.
///
/// Pond leftovers (1021–1024) stay with <see cref="SmallTileClassifier"/>.
/// The live 7-tile type-1045 34×45 hand-echo is dropped by
/// <see cref="IconNodeScan.IsType1045PondEcho"/>. Other 2–4 tile InferMeld
/// groups are seated by table region.
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
            .Where(t => t.NodeType is not (1009 or 1006 or 1021 or 1022 or 1023 or 1024 or 1038))
            .Where(t => IconNodeScan.IsTileSized(t.Width, t.Height)
                        || IconNodeScan.IsFaceLeaf(t.NodeType, t.Width, t.Height))
            .Where(t => !IconNodeScan.IsType1045PondEcho(t.NodeType, t.Width, t.Height))
            .ToList();
        if (usable.Count == 0)
            return result;

        var tableCenterX = TableCenterX(usable, pondHints);
        var tableCenterY = TableCenterY(pondHints, playerStripAbsY);
        var denseY = DenseAbsYBands(usable, playerStripAbsY);

        foreach (var cluster in Cluster(usable, ClusterGapPx))
        {
            var onPlayerStrip = playerStripAbsY is float stripY
                                && cluster.All(t => Math.Abs(AbsYOf(t) - stripY) <= PlayerStripBandPx);

            if (!onPlayerStrip && cluster.Any(t => denseY.Contains(BandY(t))))
                continue;

            foreach (var group in SplitFuuroGroups(cluster, allowSplit: onPlayerStrip))
            {
                var kind = GuessOwner(group, pondHints, playerStripAbsY, tableCenterX, tableCenterY);
                if (!onPlayerStrip && kind == SmallTileClassifier.Kind.LeftMeld && group.Count >= 5)
                    continue;
                result.Add(new Assignment(kind, group.Select(t => t.Id).ToList()));
            }
        }

        return result;
    }

    /// <summary>
    /// Y-bands with 7+ leftover faces are the mid-table hand-echo / pond rows.
    /// Never split those into left/opposite CHI. The player strip is exempt so
    /// adjacent own fuuro (PON+CHI) can still be peeled.
    /// </summary>
    internal static HashSet<int> DenseAbsYBands(IReadOnlyList<Tile> tiles, float? playerStripAbsY)
    {
        var dense = new HashSet<int>();
        foreach (var band in tiles.GroupBy(BandY))
        {
            if (band.Count() < 7)
                continue;
            if (playerStripAbsY is float stripY
                && Math.Abs(band.Average(AbsYOf) - stripY) <= PlayerStripBandPx)
                continue;
            dense.Add(band.Key);
        }

        return dense;
    }

    internal static List<List<Tile>> SplitFuuroGroups(List<Tile> cluster, bool allowSplit)
    {
        if (cluster.Count is >= 2 and <= 4
            && SmallTileClassifier.LooksLikeOpenMeld(cluster.Select(ToSmall).ToList()))
            return [cluster];

        if (!allowSplit || cluster.Count < 3)
            return [];

        var ordered = cluster
            .OrderBy(t => HasAbs(t) ? t.AbsX : t.X)
            .ThenBy(t => t.Id)
            .ToList();
        var groups = new List<List<Tile>>();
        var i = 0;
        while (i < ordered.Count)
        {
            if (ordered.Count - i >= 4)
            {
                var four = ordered.GetRange(i, 4);
                var meld4 = MeldClassifier.InferMeld(four.Select(t => t.TileCode!).ToList());
                if (meld4 != null && meld4.Type.StartsWith("KAN", StringComparison.Ordinal))
                {
                    groups.Add(four);
                    i += 4;
                    continue;
                }
            }

            if (ordered.Count - i >= 3)
            {
                var three = ordered.GetRange(i, 3);
                if (MeldClassifier.InferMeld(three.Select(t => t.TileCode!).ToList()) != null)
                {
                    groups.Add(three);
                    i += 3;
                    continue;
                }
            }

            i++;
        }

        return groups;
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

        var xSpan = group.Max(t => HasAbs(t) ? t.AbsX : t.X) - group.Min(t => HasAbs(t) ? t.AbsX : t.X);
        var ySpan = group.Max(t => HasAbs(t) ? t.AbsY : t.Y) - group.Min(t => HasAbs(t) ? t.AbsY : t.Y);
        var horizontal = xSpan >= ySpan;

        // Toimen fuuro is a horizontal row at the top of the table, including
        // the top-right (live 7ad3d5d CHI at AbsX≈1040, AbsY≈410). Shimocha
        // is a vertical stack on the right edge — don't steal those.
        if (playerStripAbsY is float handY && gy + RegionMarginPx < handY && horizontal)
            return SmallTileClassifier.Kind.OppositeMeld;

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

            clusters.Add(cluster
                .OrderBy(t => HasAbs(t) ? t.AbsX : t.X)
                .ThenBy(t => HasAbs(t) ? t.AbsY : t.Y)
                .ThenBy(t => t.Id)
                .ToList());
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

    private static float AbsYOf(Tile tile) => HasAbs(tile) ? tile.AbsY : tile.Y;

    private static int BandY(Tile tile) => (int)MathF.Round(AbsYOf(tile) / 20f) * 20;
}
