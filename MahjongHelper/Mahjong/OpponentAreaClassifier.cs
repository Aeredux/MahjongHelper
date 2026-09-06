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
/// groups are seated from 1022/1023/1024 pond centroids (kamicha / shimocha /
/// toimen), not "anything above the hand strip".
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
        float? playerStripAbsY = null,
        float? closedPackMaxAbsX = null,
        IReadOnlyList<IconNodeScan.Tray>? trays = null)
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

        var tableCenterX = TableCenterX(pondHints);
        var tableCenterY = TableCenterY(pondHints, playerStripAbsY);
        var denseY = DenseAbsYBands(usable, playerStripAbsY);

        foreach (var cluster in Cluster(usable, ClusterGapPx))
        {
            var onPlayerStrip = playerStripAbsY is float stripY
                                && cluster.All(t => Math.Abs(AbsYOf(t) - stripY) <= PlayerStripBandPx);

            if (!onPlayerStrip && cluster.Any(t => denseY.Contains(BandY(t))))
                continue;

            var working = onPlayerStrip
                ? ExpandPlayerStripCuedCluster(cluster, usable, trays)
                : cluster;

            foreach (var group in SplitFuuroGroups(working, allowSplit: onPlayerStrip))
            {
                var kind = GuessOwner(group, pondHints, playerStripAbsY, tableCenterX, tableCenterY);
                if (!onPlayerStrip && kind == SmallTileClassifier.Kind.LeftMeld && group.Count >= 5)
                    continue;
                // 61610b9 only gated type-2. Live West-seat NORTH ghosts are
                // 42×55 / 55×42 / 1055 (and 1031) Right leftover — those
                // skipped LeftoverNeedsLiveFuuroSlot and never asked for 1062.
                if (kind is SmallTileClassifier.Kind.LeftMeld
                        or SmallTileClassifier.Kind.RightMeld
                        or SmallTileClassifier.Kind.OppositeMeld
                    && !IconNodeScan.SeatHasLiveFuuroSlot(kind, trays))
                    continue;
                if (kind == SmallTileClassifier.Kind.OppositeMeld
                    && !OppositeLeftoverFuuroAllowed(group, trays))
                    continue;
                if (kind == SmallTileClassifier.Kind.PlayerMeld)
                {
                    var withCues = group
                        .Concat(cluster.Where(t =>
                            IconNodeScan.IsCallCueNode(t.NodeType, t.Width, t.Height, t.Rotation)
                            && group.Any(g => Distance(g, t) < 80f)))
                        .Distinct()
                        .ToList();
                    if (!OwnLeftoverFuuroAllowed(withCues, closedPackMaxAbsX, trays))
                        continue;
                }

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

    /// <summary>
    /// Live CHI: type-1056 cue + type-2 M2 sit at AbsX≈1524–1528. M1 @~1580
    /// and M3 @~1622 are the same 1060 tray / nearby AbsX — pull them in so
    /// InferMeld sees three faces, not a 2-tile M2 pair.
    /// </summary>
    internal static List<Tile> ExpandPlayerStripCuedCluster(
        List<Tile> cluster,
        List<Tile> all,
        IReadOnlyList<IconNodeScan.Tray>? trays)
    {
        var cues = cluster
            .Where(t => IconNodeScan.IsCallCueNode(t.NodeType, t.Width, t.Height, t.Rotation)
                        || SmallTileClassifier.IsSideways(t.Rotation, t.Width, t.Height))
            .ToList();
        if (cues.Count == 0)
            return cluster;

        var expanded = cluster.ToList();
        foreach (var cue in cues)
        {
            foreach (var tile in all)
            {
                if (expanded.Any(t => t.Id == tile.Id))
                    continue;
                if (!IconNodeScan.IsFaceLeaf(tile.NodeType, tile.Width, tile.Height)
                    && tile.NodeType != IconNodeScan.CallCueNodeType)
                    continue;

                var dx = Math.Abs(AbsXOf(tile) - AbsXOf(cue));
                var dy = Math.Abs(AbsYOf(tile) - AbsYOf(cue));
                if (dy > PlayerStripBandPx)
                    continue;

                var sameTray = trays != null && trays.Any(tray =>
                    IconNodeScan.CenterInTray(AbsXOf(tile), AbsYOf(tile), tile.Width, tile.Height, tray)
                    && IconNodeScan.CenterInTray(AbsXOf(cue), AbsYOf(cue), cue.Width, cue.Height, tray));
                if (sameTray || dx <= HandStripClassifier.SameMeldReachPx)
                    expanded.Add(tile);
            }
        }

        return expanded
            .OrderBy(t => HasAbs(t) ? t.AbsX : t.X)
            .ThenBy(t => t.Id)
            .ToList();
    }

    internal static List<List<Tile>> SplitFuuroGroups(List<Tile> cluster, bool allowSplit)
    {
        var faces = IconNodeScan.CollapseStackedDuplicates(
            cluster.OrderBy(t => HasAbs(t) ? t.AbsX : t.X).ToList(),
            t => HasAbs(t) ? t.AbsX : t.X,
            t => t.TileCode);
        if (faces.Count is >= 3 and <= 4
            && MeldClassifier.InferMeld(faces.Select(t => t.TileCode!).ToList()) != null)
            return [cluster.Count is >= 3 and <= 4 ? cluster : faces.ToList()];

        if (cluster.Count is >= 3 and <= 4
            && MeldClassifier.InferMeld(cluster.Select(t => t.TileCode!).ToList()) != null)
            return [cluster];

        // 2-tile same-key only on the player strip (called-tile remainder).
        // Opponent leftover pairs are pond / dora ghosts (live East P7×2).
        if (allowSplit && faces.Count == 2
            && SmallTileClassifier.LooksLikeOpenMeld(faces.Select(ToSmall).ToList()))
            return [cluster.Count == 2 ? cluster : faces.ToList()];

        if (cluster.Count < 5)
            return [];

        // Adjacent opponent fuuro (live East SOUTH PON S9 + CHI S4-S6) sits
        // in one 70px cluster. Peel InferMeld windows even off the hand strip.
        return PeelInferMeldGroups(cluster);
    }

    /// <summary>
    /// Peels consecutive InferMeld windows. Tries X-major (two side columns)
    /// and Y-major (one vertical stack of two sets) and keeps the richer peel.
    /// </summary>
    internal static List<List<Tile>> PeelInferMeldGroups(List<Tile> cluster)
    {
        var byParent = cluster
            .Where(t => t.ParentNodeId != 0)
            .GroupBy(t => t.ParentNodeId)
            .Select(g => g.OrderBy(AbsXOf).ThenBy(AbsYOf).ThenBy(t => t.Id).ToList())
            .Where(g => g.Count is >= 3 and <= 4
                        && MeldClassifier.InferMeld(g.Select(t => t.TileCode!).ToList()) != null)
            .ToList();
        if (byParent.Count >= 2)
            return byParent;

        var byX = PeelOrdered(cluster.OrderBy(AbsXOf).ThenBy(AbsYOf).ThenBy(t => t.Id).ToList());
        var byY = PeelOrdered(cluster.OrderBy(AbsYOf).ThenBy(AbsXOf).ThenBy(t => t.Id).ToList());
        return byX.Count >= byY.Count ? byX : byY;
    }

    private static List<List<Tile>> PeelOrdered(List<Tile> ordered)
    {
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
        var kind = SeatLeftoverFuuro(gx, gy, pondHints, playerStripAbsY, tableCenterX, tableCenterY);
        if (kind != SmallTileClassifier.Kind.OppositeMeld)
            return kind;

        // Vertical leftover on a side is kamicha/shimocha. Master treated
        // anything above table-center Y as toimen, so East-seat SOUTH PON S9
        // landed on WEST; PR #4 then dropped it as uncued opposite type-2.
        var xSpan = group.Max(t => HasAbs(t) ? t.AbsX : t.X) - group.Min(t => HasAbs(t) ? t.AbsX : t.X);
        var ySpan = group.Max(t => HasAbs(t) ? t.AbsY : t.Y) - group.Min(t => HasAbs(t) ? t.AbsY : t.Y);
        if (ySpan > xSpan && Math.Abs(gx - tableCenterX) > RegionMarginPx)
        {
            return gx < tableCenterX
                ? SmallTileClassifier.Kind.LeftMeld
                : SmallTileClassifier.Kind.RightMeld;
        }

        return kind;
    }

    /// <summary>
    /// Seats leftover face-up fuuro from 1022/1023/1024 pond centroids.
    /// Mid-left kamicha (live SOUTH P8 at Abs≈993,868) is LeftMeld, not
    /// Opposite — "above the hand strip" is not toimen. Mid-right shimocha
    /// (S1 at Abs≈1585,547) is RightMeld even when side ponds are empty.
    /// </summary>
    public static SmallTileClassifier.Kind SeatLeftoverFuuro(
        float gx,
        float gy,
        IReadOnlyList<PondHint>? pondHints,
        float? playerStripAbsY,
        float tableCenterX = 400f,
        float tableCenterY = 400f)
    {
        if (playerStripAbsY is float stripY && Math.Abs(gy - stripY) <= PlayerStripBandPx)
            return SmallTileClassifier.Kind.PlayerMeld;

        var cx = TableCenterX(pondHints, tableCenterX);
        var cy = TableCenterY(pondHints, playerStripAbsY, tableCenterY);
        var nearest = NearestOpponentPondKind(gx, gy, pondHints);
        var topStrength = cy - gy;
        var sideStrength = Math.Abs(gx - cx);

        if (nearest != null)
        {
            // A lone opposite pond must not steal mid-side fuuro (sidecar
            // left=0/right=0 still has a 1024 centroid).
            if (nearest == SmallTileClassifier.Kind.OppositeMeld
                && sideStrength > topStrength
                && sideStrength > RegionMarginPx)
            {
                return gx < cx
                    ? SmallTileClassifier.Kind.LeftMeld
                    : SmallTileClassifier.Kind.RightMeld;
            }

            // A side pond must not steal a clearly-top toimen row.
            // Require a real top advantage — mid-right shimocha is often
            // slightly above table center but still a side stack.
            if (nearest is SmallTileClassifier.Kind.LeftMeld or SmallTileClassifier.Kind.RightMeld
                && topStrength > sideStrength + RegionMarginPx
                && topStrength > RegionMarginPx)
            {
                return SmallTileClassifier.Kind.OppositeMeld;
            }

            return nearest.Value;
        }

        if (topStrength > sideStrength && topStrength > 0)
            return SmallTileClassifier.Kind.OppositeMeld;
        if (gx + RegionMarginPx < cx)
            return SmallTileClassifier.Kind.LeftMeld;
        if (gx > cx + RegionMarginPx)
            return SmallTileClassifier.Kind.RightMeld;
        if (topStrength > 0)
            return SmallTileClassifier.Kind.OppositeMeld;
        return gx < cx
            ? SmallTileClassifier.Kind.LeftMeld
            : SmallTileClassifier.Kind.RightMeld;
    }

    private static SmallTileClassifier.Kind? NearestOpponentPondKind(
        float gx, float gy, IReadOnlyList<PondHint>? pondHints)
    {
        if (pondHints == null || pondHints.Count == 0)
            return null;

        SmallTileClassifier.Kind? best = null;
        var bestScore = float.MaxValue;
        foreach (var hint in pondHints)
        {
            if (hint.PondKind is not (
                SmallTileClassifier.Kind.LeftDiscard
                or SmallTileClassifier.Kind.RightDiscard
                or SmallTileClassifier.Kind.OppositeDiscard))
            {
                continue;
            }

            var dx = Math.Abs(gx - hint.AbsX);
            var dy = Math.Abs(gy - hint.AbsY);
            // Side ponds are an X column; toimen is a Y row.
            var score = hint.PondKind == SmallTileClassifier.Kind.OppositeDiscard
                ? 0.4f * dx + dy
                : dx + 0.4f * dy;
            if (score < bestScore)
            {
                bestScore = score;
                best = hint.PondKind;
            }
        }

        return best == null ? null : SmallTileClassifier.MeldKindForPond(best.Value);
    }

    private static float TableCenterX(IReadOnlyList<PondHint>? pondHints, float fallback = 400f)
    {
        if (pondHints is not { Count: > 0 })
            return fallback;

        var left = pondHints.Where(h => h.PondKind == SmallTileClassifier.Kind.LeftDiscard).ToList();
        var right = pondHints.Where(h => h.PondKind == SmallTileClassifier.Kind.RightDiscard).ToList();
        if (left.Count > 0 && right.Count > 0)
            return (left.Average(h => h.AbsX) + right.Average(h => h.AbsX)) / 2f;
        if (pondHints.Count >= 2)
            return pondHints.Average(h => h.AbsX);
        return fallback;
    }

    private static float TableCenterY(
        IReadOnlyList<PondHint>? pondHints, float? playerStripAbsY, float fallback = 400f)
    {
        if (pondHints is { Count: > 0 })
        {
            var opposite = pondHints.Where(h => h.PondKind == SmallTileClassifier.Kind.OppositeDiscard).ToList();
            var player = pondHints.Where(h => h.PondKind == SmallTileClassifier.Kind.PlayerDiscard).ToList();
            if (opposite.Count > 0 && player.Count > 0)
                return (opposite.Average(h => h.AbsY) + player.Average(h => h.AbsY)) / 2f;
            if (opposite.Count > 0 && playerStripAbsY is float stripY and > 0)
                return (opposite.Average(h => h.AbsY) + stripY) / 2f;
            if (pondHints.Count >= 2)
                return pondHints.Average(h => h.AbsY);
        }

        if (playerStripAbsY is float handY and > 0)
            return handY * 0.5f;
        return fallback;
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

    private static float AbsXOf(Tile tile) => HasAbs(tile) ? tile.AbsX : tile.X;

    private static float AbsYOf(Tile tile) => HasAbs(tile) ? tile.AbsY : tile.Y;

    private static int BandY(Tile tile) => (int)MathF.Round(AbsYOf(tile) / 20f) * 20;

    private static bool OppositeLeftoverFuuroAllowed(
        IReadOnlyList<Tile> group, IReadOnlyList<IconNodeScan.Tray>? trays)
        => IconNodeScan.IsPlausibleOppositeLeftoverFuuro(
            group,
            trays,
            t => t.NodeType,
            t => t.Width,
            t => t.Height,
            t => t.Rotation,
            t => HasAbs(t) ? t.AbsX : t.X,
            t => HasAbs(t) ? t.AbsY : t.Y,
            t => t.TileCode);

    private static bool OwnLeftoverFuuroAllowed(
        IReadOnlyList<Tile> group, float? packMaxAbsX, IReadOnlyList<IconNodeScan.Tray>? trays)
    {
        var leftover = group
            .Where(t => IconNodeScan.IsFaceLeaf(t.NodeType, t.Width, t.Height)
                        || t.NodeType == IconNodeScan.CallCueNodeType)
            .ToList();
        if (leftover.Count == 0)
            return true;

        return IconNodeScan.IsPlausibleOwnLeftoverFuuro(
            leftover,
            packMaxAbsX,
            trays,
            t => t.Width,
            t => t.Height,
            t => t.Rotation,
            t => HasAbs(t) ? t.AbsX : t.X,
            t => HasAbs(t) ? t.AbsY : t.Y);
    }
}
