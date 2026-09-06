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
        int NodeIndex = 0);

    public sealed record Result(
        IReadOnlyList<int> ClosedIds,
        int? DrawId,
        IReadOnlyList<IReadOnlyList<int>> MeldGroups);

    public static Result Split(IReadOnlyList<Tile> tiles)
    {
        if (tiles == null || tiles.Count == 0)
            return new Result([], null, []);

        var usable = tiles
            .Where(t => t.X >= 0 && MeldClassifier.IsUsableTile(t.TileCode))
            .ToList();
        if (usable.Count == 0)
            return new Result([], null, []);

        var rowY = ChooseHandRowY(usable);
        var row = usable
            .Where(t => Math.Abs(t.Y - rowY) <= HandRowBandPx)
            .OrderBy(t => t.X)
            .ThenBy(t => t.Id)
            .ToList();
        if (row.Count == 0)
            row = usable.OrderBy(t => t.X).ThenBy(t => t.Id).ToList();

        var melded = new HashSet<int>();
        var meldGroups = new List<List<int>>();

        void AddMeld(IEnumerable<Tile> group)
        {
            var members = group.ToList();
            if (members.Count == 0)
                return;

            foreach (var tile in members)
                melded.Add(tile.Id);

            var unique = UniqueX(members).Select(t => t.Id).Distinct().ToList();
            if (unique.Count >= 2)
                meldGroups.Add(unique);
        }

        ExtractParentMelds(row, AddMeld);

        var remaining = row.Where(t => !melded.Contains(t.Id)).ToList();
        foreach (var cue in FindCuedTiles(remaining))
        {
            if (melded.Contains(cue.Id))
                continue;
            var expanded = ExpandMeldFromCue(cue, remaining.Where(t => !melded.Contains(t.Id)).ToList());
            if (expanded.Count >= 2)
                AddMeld(expanded);
        }

        remaining = row.Where(t => !melded.Contains(t.Id)).OrderBy(t => t.X).ToList();
        var draw = FindDrawTile(remaining);
        if (draw != null)
            GroupConsecutiveMelds(remaining.Where(t => t.Id != draw.Value.Id && t.X > draw.Value.X), AddMeld);

        remaining = row.Where(t => !melded.Contains(t.Id)).OrderBy(t => t.X).ToList();
        var clusters = ClusterByGap(remaining, ClusterGapPx);
        if (clusters.Count > 1)
        {
            for (var i = 1; i < clusters.Count; i++)
            {
                if (clusters[i].Count >= 2 && (draw == null || clusters[i].All(t => t.Id != draw.Value.Id)))
                    AddMeld(clusters[i]);
            }
        }

        remaining = row.Where(t => !melded.Contains(t.Id)).OrderBy(t => t.X).ToList();
        var drawId = draw?.Id ?? FindDrawId(remaining);

        var closed = remaining
            .Where(t => t.Id != drawId)
            .Select(t => t.Id)
            .ToList();
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

        var primary = row.OrderBy(t => t.X).First().ParentNodeId;
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
                    Math.Abs(other.X - tile.X) < StackedXPx))
            {
                cues.Add(tile);
            }
        }

        return cues
            .GroupBy(t => t.Id)
            .Select(g => g.First())
            .OrderByDescending(t => t.X)
            .ToList();
    }

    private static List<Tile> ExpandMeldFromCue(Tile cue, List<Tile> remaining)
    {
        var reach = remaining
            .Where(t => Math.Abs(t.X - cue.X) <= SameMeldReachPx && Math.Abs(t.Y - cue.Y) <= HandRowBandPx)
            .ToList();
        if (reach.All(t => t.Id != cue.Id))
            reach.Add(cue);

        var same = reach.Where(t => SameKey(t, cue)).ToList();
        if (UniqueX(same).Count() >= 2)
            return same;

        var chi = FindChiIncludingCue(cue, reach);
        return chi.Count >= 3 ? chi : [];
    }

    private static List<Tile> FindChiIncludingCue(Tile cue, List<Tile> nearby)
    {
        var unique = UniqueX(nearby).OrderBy(t => t.X).ToList();
        for (var i = 0; i + 2 < unique.Count; i++)
        {
            var window = unique.GetRange(i, 3);
            if (window.All(t => Math.Abs(t.X - cue.X) > SameMeldReachPx) && window.All(t => t.Id != cue.Id))
                continue;
            if (!window.Any(t => t.Id == cue.Id || Math.Abs(t.X - cue.X) < StackedXPx))
                continue;

            var codes = window.Select(t => t.TileCode!).ToList();
            var meld = MeldClassifier.InferMeld(codes);
            if (meld?.Type != "CHI")
                continue;

            var xs = window.Select(t => t.X).ToList();
            return nearby.Where(t => xs.Any(x => Math.Abs(t.X - x) < StackedXPx)).ToList();
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

        var node54 = remaining.Where(t => t.NodeIndex == DrawNodeIndex).OrderBy(t => t.X).ToList();
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
        var unique = UniqueX(remaining).OrderBy(t => t.X).ToList();
        if (unique.Count < 3)
            return null;

        var deltas = new List<float>();
        for (var i = 1; i < unique.Count; i++)
        {
            var delta = unique[i].X - unique[i - 1].X;
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
            var delta = unique[i].X - unique[i - 1].X;
            // Live snap-20260906-071141803: last closed at 294, draw at 304 (10px).
            if (delta >= 1f && delta < pitch * 0.55f)
                return unique[i];
        }

        return null;
    }

    private static void GroupConsecutiveMelds(IEnumerable<Tile> tiles, Action<IEnumerable<Tile>> addMeld)
    {
        var ordered = tiles.OrderBy(t => t.X).ThenBy(t => t.Id).ToList();
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
            if (ordered[i].X - prev.X >= gapPx)
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
            .GroupBy(t => (int)Math.Round(t.X / 2f) * 2)
            .Select(g => g
                .OrderByDescending(IsRotated)
                .ThenBy(t => t.Id)
                .First());

    private static float ChooseHandRowY(List<Tile> tiles)
    {
        var preferred = tiles.Where(t => t.Width == 42 && t.Height == 55).ToList();
        var source = preferred.Count > 0 ? preferred : tiles;
        return source
            .GroupBy(t => (int)Math.Round(t.Y))
            .OrderByDescending(g => g.Count())
            .ThenBy(g => Math.Abs(g.Key))
            .First()
            .Average(t => t.Y);
    }

    private static bool SameKey(Tile a, Tile b)
        => MeldClassifier.IsUsableTile(a.TileCode)
           && MeldClassifier.IsUsableTile(b.TileCode)
           && string.Equals(
               MeldClassifier.CanonicalKey(a.TileCode!),
               MeldClassifier.CanonicalKey(b.TileCode!),
               StringComparison.Ordinal);
}
