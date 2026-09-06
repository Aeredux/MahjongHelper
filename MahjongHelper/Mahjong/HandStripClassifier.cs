using System;
using System.Collections.Generic;
using System.Linq;

namespace MahjongHelper.Mahjong;

/// <summary>
/// Splits the local player's Y=0 42×55 (and 55×42) strip into closed hand, draw, and open melds.
/// Doman renders called sets on the right of that strip — same node size as closed tiles —
/// so 34×45 pond classification never sees them.
/// </summary>
public static class HandStripClassifier
{
    public const float ClusterGapPx = 50f;
    public const float StackedXPx = 12f;
    public const float HandRowBandPx = 25f;
    public const float SameMeldReachPx = 113f;
    public const int DrawNodeIndex = 54;

    public readonly record struct Tile(
        int Id,
        float X,
        float Y,
        int Width,
        int Height,
        float Rotation,
        uint ParentNodeId,
        string? TileCode,
        int NodeIndex = 0,
        float AbsX = 0,
        float AbsY = 0,
        ushort NodeType = 0);

    public sealed record Result(
        IReadOnlyList<int> ClosedIds,
        int? DrawId,
        IReadOnlyList<IReadOnlyList<int>> MeldGroups);

    public static Result Split(IReadOnlyList<Tile> tiles, IReadOnlyList<IconNodeScan.Tray>? trays = null)
    {
        if (tiles == null || tiles.Count == 0)
            return new Result([], null, []);

        var usable = tiles
            .Where(t => LayoutX(t) >= 0 && MeldClassifier.IsUsableTile(t.TileCode))
            .ToList();
        if (usable.Count == 0)
            return new Result([], null, []);

        var rowY = ChooseHandRowY(usable);
        var row = usable
            .Where(t => Math.Abs(LayoutY(t) - rowY) <= HandRowBandPx)
            .OrderBy(LayoutX)
            .ThenBy(t => t.Id)
            .ToList();
        if (row.Count == 0)
            row = usable.OrderBy(LayoutX).ThenBy(t => t.Id).ToList();

        var pack = ReservedClosedPack(row);
        float? packMaxAbsX = pack.Count >= 7 ? pack.Max(LayoutX) : null;

        var melded = new HashSet<int>();
        var meldGroups = new List<List<int>>();

        void AddMeld(IEnumerable<Tile> group)
        {
            var members = group.ToList();
            if (members.Count == 0)
                return;
            if (!OwnLeftoverFuuroAllowed(members, packMaxAbsX, trays))
                return;

            foreach (var tile in members)
                melded.Add(tile.Id);

            var unique = UniqueFaces(members);
            if (unique.Count < 2 || !LeftoverInferMeldOk(unique))
                return;
            var ids = unique.Select(t => t.Id).Distinct().ToList();
            var key = ids.OrderBy(id => id).ToList();
            if (meldGroups.Any(g => g.OrderBy(id => id).SequenceEqual(key)))
                return;
            meldGroups.Add(ids);
        }

        ExtractParentMelds(row, AddMeld);
        ExtractTrayMelds(row, trays, AddMeld);

        var remaining = row.Where(t => !melded.Contains(t.Id)).ToList();
        foreach (var cue in FindCuedTiles(remaining))
        {
            if (melded.Contains(cue.Id))
                continue;
            var expanded = ExpandMeldFromCue(
                cue, remaining.Where(t => !melded.Contains(t.Id)).ToList(), trays);
            if (expanded.Count >= 2)
                AddMeld(expanded);
        }

        remaining = row.Where(t => !melded.Contains(t.Id)).OrderBy(LayoutX).ToList();
        var reserved = ReservedClosedPack(remaining);
        Tile? peelDraw = null;
        if (reserved.Count >= 7)
        {
            var reservedIds = reserved.Select(t => t.Id).ToHashSet();
            var packDraw = remaining.Where(t => t.NodeIndex == DrawNodeIndex).ToList();
            peelDraw = packDraw.Count > 0 ? packDraw[^1] : null;
            GroupConsecutiveMelds(
                remaining.Where(t => !reservedIds.Contains(t.Id) && t.Id != peelDraw?.Id),
                AddMeld);
        }
        else
        {
            peelDraw = FindDrawTile(remaining);
            if (peelDraw != null && !IsMidRowDraw(peelDraw.Value, remaining))
                GroupConsecutiveMelds(
                    remaining.Where(t => t.Id != peelDraw.Value.Id && LayoutX(t) > LayoutX(peelDraw.Value)),
                    AddMeld);
        }

        remaining = row.Where(t => !melded.Contains(t.Id)).OrderBy(LayoutX).ToList();
        var clusters = ClusterByGap(remaining, ClusterGapPx);
        if (clusters.Count > 1)
        {
            for (var i = 1; i < clusters.Count; i++)
            {
                if (clusters[i].Count >= 2
                    && (peelDraw == null || clusters[i].All(t => t.Id != peelDraw.Value.Id)))
                    AddMeld(clusters[i]);
            }
        }

        remaining = row.Where(t => !melded.Contains(t.Id)).OrderBy(LayoutX).ToList();
        int? drawId = remaining.Any(t => t.NodeIndex == DrawNodeIndex)
            ? remaining.Where(t => t.NodeIndex == DrawNodeIndex).OrderBy(LayoutX).Last().Id
            : FindDrawTile(remaining)?.Id;

        var reservedPack = ReservedClosedPack(row);
        var closed = reservedPack.Count >= 7
            ? reservedPack.Where(t => t.Id != drawId && !melded.Contains(t.Id)).Select(t => t.Id).ToList()
            : remaining.Where(t => t.Id != drawId).Select(t => t.Id).ToList();
        if (closed.Count > 14)
            closed = closed.Take(14).ToList();

        return new Result(closed, drawId, meldGroups);
    }

    public static bool IsRotated(Tile tile)
    {
        if (tile.Width > tile.Height)
            return true;

        var abs = Math.Abs(tile.Rotation);
        while (abs > Math.PI * 2)
            abs -= (float)(Math.PI * 2);
        var dist90 = Math.Abs(abs - (float)(Math.PI / 2));
        var dist270 = Math.Abs(abs - (float)(3 * Math.PI / 2));
        return dist90 < 0.35f || dist270 < 0.35f;
    }

    private static void ExtractParentMelds(List<Tile> row, Action<IEnumerable<Tile>> addMeld)
    {
        var byParent = row.GroupBy(t => t.ParentNodeId).ToList();
        if (byParent.Count <= 1 || byParent.All(g => g.Key == 0))
            return;

        var pack = ReservedClosedPack(row);
        var primary = pack.Count >= 7
            ? pack.GroupBy(t => t.ParentNodeId).OrderByDescending(g => g.Count()).First().Key
            : row.OrderBy(LayoutX).First().ParentNodeId;
        foreach (var group in byParent)
        {
            if (group.Key == 0 || group.Key == primary)
                continue;
            if (group.Count() >= 2)
                addMeld(group);
        }
    }

    private static List<Tile> FindCuedTiles(List<Tile> tiles)
    {
        var cues = new List<Tile>();
        foreach (var tile in tiles)
        {
            if (IsRotated(tile))
            {
                cues.Add(tile);
                continue;
            }

            if (tiles.Any(other =>
                    other.Id != tile.Id &&
                    SameKey(other, tile) &&
                    Math.Abs(LayoutX(other) - LayoutX(tile)) < StackedXPx))
            {
                cues.Add(tile);
            }
        }

        return cues
            .GroupBy(t => t.Id)
            .Select(g => g.First())
            .OrderByDescending(LayoutX)
            .ToList();
    }

    private static void ExtractTrayMelds(
        List<Tile> row, IReadOnlyList<IconNodeScan.Tray>? trays, Action<IEnumerable<Tile>> addMeld)
    {
        if (trays == null || trays.Count == 0)
            return;

        foreach (var tray in trays)
        {
            var inside = row
                .Where(t => !IsClosedPackTile(t) && TileCenterInTray(t, tray))
                .ToList();
            if (inside.Count < 2)
                continue;
            if (!inside.Any(IsRotated)
                && !inside.Any(t => IconNodeScan.IsCallCueNode(t.NodeType, t.Width, t.Height, t.Rotation)))
                continue;

            var unique = UniqueFaces(inside);
            if (MeldClassifier.InferMeld(unique.Select(t => t.TileCode!).ToList()) != null)
                addMeld(inside);
        }
    }

    private static List<Tile> ExpandMeldFromCue(
        Tile cue, List<Tile> remaining, IReadOnlyList<IconNodeScan.Tray>? trays)
    {
        var reach = remaining
            .Where(t => Math.Abs(LayoutX(t) - LayoutX(cue)) <= SameMeldReachPx
                        && Math.Abs(LayoutY(t) - LayoutY(cue)) <= HandRowBandPx)
            .ToList();
        foreach (var tray in trays ?? [])
        {
            if (!TileCenterInTray(cue, tray))
                continue;
            foreach (var tile in remaining.Where(t => TileCenterInTray(t, tray)))
            {
                if (reach.All(r => r.Id != tile.Id))
                    reach.Add(tile);
            }
        }

        if (reach.All(t => t.Id != cue.Id))
            reach.Add(cue);

        var same = reach.Where(t => SameKey(t, cue)).ToList();
        var sameUnique = UniqueFaces(same);
        var sameSpan = sameUnique.Count >= 2
            ? LayoutX(sameUnique[^1]) - LayoutX(sameUnique[0])
            : 0f;
        // Live: type-1056 M2 @1524 and type-2 M2 @1528 are one face, not a PON.
        if (sameUnique.Count >= 3 || (sameUnique.Count >= 2 && sameSpan >= IconNodeScan.SameFaceSpanPx))
            return same;

        // Neighbors may be upright in-hand tiles (Doman CHI: only the called
        // tile is sideways). Do not require M1/M3 to be rotated.
        var chi = FindChiIncludingCue(cue, reach);
        return chi.Count >= 3 ? chi : [];
    }

    private static List<Tile> FindChiIncludingCue(Tile cue, List<Tile> nearby)
    {
        var unique = UniqueFaces(nearby);
        for (var i = 0; i + 2 < unique.Count; i++)
        {
            var window = unique.GetRange(i, 3);
            if (window.All(t => Math.Abs(LayoutX(t) - LayoutX(cue)) > SameMeldReachPx) && window.All(t => t.Id != cue.Id))
                continue;
            if (!window.Any(t => t.Id == cue.Id || Math.Abs(LayoutX(t) - LayoutX(cue)) < StackedXPx))
                continue;

            var codes = window.Select(t => t.TileCode!).ToList();
            var meld = MeldClassifier.InferMeld(codes);
            if (meld?.Type != "CHI")
                continue;

            var xs = window.Select(LayoutX).ToList();
            return nearby.Where(t => xs.Any(x => Math.Abs(LayoutX(t) - x) < StackedXPx)).ToList();
        }

        return [];
    }

    private static int? FindDrawId(List<Tile> remaining)
        => FindDrawTile(remaining)?.Id;

    /// <summary>
    /// Live Doman: node 54 is the drawn 1055 on the strip. When that node is
    /// missing, a tile that breaks the ~42px pitch (inserted ~10px after the
    /// last closed tile) or sits alone after a ≥50px gap is the draw.
    /// </summary>
    private static Tile? FindDrawTile(List<Tile> remaining)
    {
        if (remaining.Count == 0)
            return null;

        var node54 = remaining.Where(t => t.NodeIndex == DrawNodeIndex).OrderBy(LayoutX).ToList();
        if (node54.Count > 0)
            return node54[^1];

        var clusters = ClusterByGap(remaining, ClusterGapPx);
        if (clusters.Count >= 2)
        {
            var singleton = clusters.Skip(1).FirstOrDefault(c => c.Count == 1);
            if (singleton != null)
                return singleton[0];
        }

        return FindPitchBreakDraw(remaining);
    }

    private static Tile? FindPitchBreakDraw(List<Tile> remaining)
    {
        var unique = UniqueX(remaining).OrderBy(LayoutX).ToList();
        if (unique.Count < 3)
            return null;

        var deltas = new List<float>();
        for (var i = 1; i < unique.Count; i++)
        {
            var delta = LayoutX(unique[i]) - LayoutX(unique[i - 1]);
            if (delta >= StackedXPx)
                deltas.Add(delta);
        }

        if (deltas.Count == 0)
            return null;

        var pitch = Median(deltas);
        if (pitch < 20f)
            return null;

        for (var i = 1; i < unique.Count; i++)
        {
            var delta = LayoutX(unique[i]) - LayoutX(unique[i - 1]);
            // Live snap-20260906-071141803: last closed at 294, draw at 304 (10px).
            if (delta >= 1f && delta < pitch * 0.55f)
                return unique[i];
        }

        return null;
    }

    private static void GroupConsecutiveMelds(IEnumerable<Tile> tiles, Action<IEnumerable<Tile>> addMeld)
    {
        var ordered = tiles.OrderBy(LayoutX).ThenBy(t => t.Id).ToList();
        var i = 0;
        while (i < ordered.Count)
        {
            if (ordered.Count - i >= 4)
            {
                var four = ordered.GetRange(i, 4);
                var meld4 = MeldClassifier.InferMeld(four.Select(t => t.TileCode!).ToList());
                if (meld4 != null && meld4.Type.StartsWith("KAN", StringComparison.Ordinal))
                {
                    addMeld(four);
                    i += 4;
                    continue;
                }
            }

            if (ordered.Count - i >= 3)
            {
                var three = ordered.GetRange(i, 3);
                if (MeldClassifier.InferMeld(three.Select(t => t.TileCode!).ToList()) != null)
                {
                    addMeld(three);
                    i += 3;
                    continue;
                }
            }

            if (ordered.Count - i >= 2
                && SameKey(ordered[i], ordered[i + 1])
                && (ordered.Count - i == 2 || !SameKey(ordered[i], ordered[i + 2])))
            {
                addMeld(ordered.GetRange(i, 2));
                i += 2;
                continue;
            }

            i++;
        }
    }

    private static float Median(List<float> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2f
            : sorted[mid];
    }

    private static List<List<Tile>> ClusterByGap(List<Tile> ordered, float gapPx)
    {
        var clusters = new List<List<Tile>>();
        if (ordered.Count == 0)
            return clusters;

        var current = new List<Tile> { ordered[0] };
        for (var i = 1; i < ordered.Count; i++)
        {
            var prev = current[^1];
            if (LayoutX(ordered[i]) - LayoutX(prev) >= gapPx)
            {
                clusters.Add(current);
                current = [ordered[i]];
            }
            else
            {
                current.Add(ordered[i]);
            }
        }

        clusters.Add(current);
        return clusters;
    }

    private static IEnumerable<Tile> UniqueX(IEnumerable<Tile> tiles)
        => tiles
            .GroupBy(t => (int)MathF.Round(LayoutX(t) / IconNodeScan.LeafSnapPx))
            .Select(g => g
                .OrderByDescending(t => IconNodeScan.IsFaceLeaf(t.NodeType, t.Width, t.Height))
                .ThenByDescending(IsRotated)
                .ThenBy(t => t.Id)
                .First());

    private static List<Tile> UniqueFaces(IEnumerable<Tile> tiles)
        => IconNodeScan.CollapseStackedDuplicates(UniqueX(tiles), LayoutX, t => t.TileCode).ToList();

    private static float ChooseHandRowY(List<Tile> tiles)
    {
        var anchors = tiles.Where(t => t.NodeType == 1055 && t.Width == 42 && t.Height == 55).ToList();
        var preferred = anchors.Count > 0
            ? anchors
            : tiles.Where(t => t.Width == 42 && t.Height == 55).ToList();
        var source = preferred.Count > 0 ? preferred : tiles;
        return source
            .GroupBy(t => (int)Math.Round(LayoutY(t)))
            .OrderByDescending(g => g.Count())
            .ThenBy(g => Math.Abs(g.Key))
            .First()
            .Average(LayoutY);
    }

    internal static bool IsClosedPackTile(Tile tile)
        => tile.NodeIndex != DrawNodeIndex
           && tile.Width == 42
           && tile.Height == 55
           && (tile.NodeType == 1055 || tile.NodeIndex is >= 59 and <= 71);

    private static List<Tile> ReservedClosedPack(IEnumerable<Tile> tiles)
        => tiles.Where(IsClosedPackTile).OrderBy(LayoutX).ThenBy(t => t.Id).ToList();

    internal static bool IsMidRowDraw(Tile draw, IReadOnlyList<Tile> remaining)
    {
        var pack = ReservedClosedPack(remaining);
        if (pack.Count < 7)
            return false;

        var min = LayoutX(pack[0]);
        var max = LayoutX(pack[^1]);
        var x = LayoutX(draw);
        return x > min + 8f && x < max - 8f;
    }

    private static float LayoutX(Tile tile) => HasAbs(tile) ? tile.AbsX : tile.X;

    private static float LayoutY(Tile tile) => HasAbs(tile) ? tile.AbsY : tile.Y;

    private static bool HasAbs(Tile tile) => tile.AbsX != 0 || tile.AbsY != 0;

    private static bool SameKey(Tile a, Tile b)
        => MeldClassifier.IsUsableTile(a.TileCode)
           && MeldClassifier.IsUsableTile(b.TileCode)
           && string.Equals(
               MeldClassifier.CanonicalKey(a.TileCode!),
               MeldClassifier.CanonicalKey(b.TileCode!),
               StringComparison.Ordinal);

    private static bool TileCenterInTray(Tile tile, IconNodeScan.Tray tray)
        => IconNodeScan.CenterInTray(LayoutX(tile), LayoutY(tile), tile.Width, tile.Height, tray);

    private static bool LeftoverInferMeldOk(IReadOnlyList<Tile> unique)
    {
        var leftover = unique
            .Where(t => IconNodeScan.IsFaceLeaf(t.NodeType, t.Width, t.Height)
                        || t.NodeType == IconNodeScan.CallCueNodeType)
            .ToList();
        if (leftover.Count == 0)
            return true;
        var faces = UniqueFaces(unique);
        return MeldClassifier.InferMeld(faces.Select(t => t.TileCode!).ToList()) != null;
    }

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
            LayoutX,
            LayoutY);
    }
}
