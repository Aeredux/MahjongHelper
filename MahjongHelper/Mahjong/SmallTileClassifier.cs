using System;
using System.Collections.Generic;
using System.Linq;

namespace MahjongHelper.Mahjong;

/// <summary>
/// Classifies 34×45 / 45×34 face-up tiles into ponds and called-tile groups.
/// Pond owner comes from EmjL component type (1021–1024). Extra parent groups
/// of the same type are that seat's fuuro. When fuuro shares the pond parent,
/// a spatial cluster farther than <see cref="PondClusterGapPx"/> is peeled off.
/// </summary>
public static class SmallTileClassifier
{
    public const float PondClusterGapPx = 70f;

    public enum Kind
    {
        PlayerDiscard,
        LeftDiscard,
        RightDiscard,
        OppositeDiscard,
        PlayerMeld,
        LeftMeld,
        RightMeld,
        OppositeMeld,
    }

    public readonly record struct Tile(
        int Id,
        ushort NodeType,
        float X,
        float Y,
        float AbsX,
        float AbsY,
        int Width,
        int Height,
        float Rotation,
        uint ParentNodeId,
        string? TileCode,
        bool Tsumogiri = false);

    public sealed record ClassifiedTile(Kind Kind, int SlotIndex, Tile Tile);

    private static readonly Dictionary<ushort, Kind> PondByType = new()
    {
        [1021] = Kind.PlayerDiscard,
        [1022] = Kind.LeftDiscard,
        [1023] = Kind.RightDiscard,
        [1024] = Kind.OppositeDiscard,
    };

    public static IReadOnlyList<ClassifiedTile> Classify(IReadOnlyList<Tile> tiles)
    {
        var result = new List<ClassifiedTile>();
        if (tiles == null || tiles.Count == 0)
            return result;

        var withIcons = tiles.ToList();
        if (withIcons.Count == 0)
            return result;

        var pondParents = new Dictionary<Kind, uint>();
        var pondTilesByKind = new Dictionary<Kind, List<Tile>>();

        foreach (var (nodeType, pondKind) in PondByType)
        {
            var ofType = withIcons.Where(t => t.NodeType == nodeType).ToList();
            if (ofType.Count == 0)
                continue;

            var byParent = ofType
                .GroupBy(t => t.ParentNodeId)
                .OrderByDescending(g => g.Count())
                .ToList();

            var meldKind = MeldKindForPond(pondKind);
            var largest = byParent[0].ToList();
            var (pond, peeled) = PeelSharedParentMelds(largest);
            pond.Reverse();
            for (var i = 0; i < pond.Count; i++)
                result.Add(new ClassifiedTile(pondKind, i, pond[i]));

            pondParents[pondKind] = byParent[0].Key;
            pondTilesByKind[pondKind] = pond;

            var meldIndex = 0;
            foreach (var tile in peeled.OrderBy(t => t.X).ThenBy(t => t.Y))
                result.Add(new ClassifiedTile(meldKind, meldIndex++, tile));

            for (var g = 1; g < byParent.Count; g++)
            {
                var extra = byParent[g].OrderBy(t => t.X).ThenBy(t => t.Y).ToList();
                if (!LooksLikeOpenMeld(extra))
                    continue;
                foreach (var tile in extra)
                    result.Add(new ClassifiedTile(meldKind, meldIndex++, tile));
            }
        }

        // 1045 34×45 leftovers stay out of this pond path; 42×55 type 1045
        // fuuro is handled by OpponentAreaClassifier / HandStripClassifier.
        var leftovers = withIcons
            .Where(t => t.NodeType is not (1021 or 1022 or 1023 or 1024 or 1009 or 1006 or 1045 or 1055))
            .GroupBy(t => t.ParentNodeId)
            .Where(g => g.Count() is >= 3 and <= 8);

        foreach (var group in leftovers)
        {
            var area = group
                .Select(t => new OpponentAreaClassifier.Tile(
                    t.Id, t.X, t.Y, t.AbsX, t.AbsY, t.Width, t.Height,
                    t.Rotation, t.ParentNodeId, t.TileCode, t.NodeType))
                .ToList();
            var parts = OpponentAreaClassifier.SplitFuuroGroups(area, allowSplit: false);
            foreach (var part in parts)
            {
                if (part.Count < 3)
                    continue;
                var owner = GuessMeldOwner(part, pondParents, pondTilesByKind);
                if (owner == null)
                    continue;
                if (owner == Kind.OppositeMeld
                    && !IconNodeScan.IsPlausibleOppositeLeftoverFuuro(
                        part,
                        trays: null,
                        t => t.NodeType,
                        t => t.Width,
                        t => t.Height,
                        t => t.Rotation,
                        t => t.AbsX != 0 || t.AbsY != 0 ? t.AbsX : t.X,
                        t => t.AbsX != 0 || t.AbsY != 0 ? t.AbsY : t.Y))
                    continue;

                var existing = result.Count(s => s.Kind == owner);
                foreach (var tile in part)
                {
                    var source = group.First(t => t.Id == tile.Id);
                    result.Add(new ClassifiedTile(owner.Value, existing++, source));
                }
            }
        }

        return result;
    }

    public static Kind MeldKindForPond(Kind pondKind) => pondKind switch
    {
        Kind.PlayerDiscard => Kind.PlayerMeld,
        Kind.LeftDiscard => Kind.LeftMeld,
        Kind.RightDiscard => Kind.RightMeld,
        Kind.OppositeDiscard => Kind.OppositeMeld,
        _ => Kind.PlayerMeld,
    };

    public static bool LooksLikeOpenMeld(IReadOnlyList<Tile> tiles)
    {
        if (tiles == null || tiles.Count is < 2 or > 4)
            return false;
        var codes = tiles
            .Select(t => t.TileCode)
            .Where(MeldClassifier.IsUsableTile)
            .Select(c => c!)
            .ToList();
        return LooksLikeOpenMeld(codes);
    }

    public static bool LooksLikeOpenMeld(IReadOnlyList<string> codes)
    {
        if (codes == null || codes.Count is < 2 or > 4)
            return false;
        if (codes.Count == 2)
            return string.Equals(
                MeldClassifier.CanonicalKey(codes[0]),
                MeldClassifier.CanonicalKey(codes[1]),
                StringComparison.Ordinal);
        return MeldClassifier.InferMeld(codes) != null;
    }

    public static bool IsSideways(float rotation, int width, int height)
    {
        if (width > height)
            return true;
        var abs = Math.Abs(rotation);
        while (abs > Math.PI * 2)
            abs -= (float)(Math.PI * 2);
        var dist90 = Math.Abs(abs - (float)(Math.PI / 2));
        var dist270 = Math.Abs(abs - (float)(3 * Math.PI / 2));
        return dist90 < 0.35f || dist270 < 0.35f;
    }

    /// <summary>
    /// Same ATK parent can hold the pond grid and a fuuro row. Clusters that
    /// InferMeld are peeled; a 4+ mix with one rotated called tile is too.
    /// A lone 3-tile pond that happens to be a sequence stays a pond.
    /// </summary>
    internal static (List<Tile> Pond, List<Tile> Melds) PeelSharedParentMelds(List<Tile> sameParent)
    {
        if (sameParent.Count < 3)
            return (sameParent, []);

        var clusters = ClusterByGap(sameParent, PondClusterGapPx);
        var pond = new List<Tile>();
        var melds = new List<Tile>();

        foreach (var cluster in clusters)
        {
            if (LooksLikeOpenMeld(cluster) && (clusters.Count > 1 || cluster.Count == 4))
            {
                melds.AddRange(cluster);
                continue;
            }

            var extracted = TryExtractCuedMeld(cluster);
            if (extracted != null && extracted.Count < cluster.Count)
            {
                var extractedIds = extracted.Select(t => t.Id).ToHashSet();
                melds.AddRange(extracted);
                pond.AddRange(cluster.Where(t => !extractedIds.Contains(t.Id)));
                continue;
            }

            pond.AddRange(cluster);
        }

        if (pond.Count == 0)
            return (sameParent, []);

        return (pond, melds);
    }

    internal static List<Tile>? TryExtractCuedMeld(List<Tile> cluster)
    {
        if (cluster.Count < 4)
            return null;

        var ordered = cluster.OrderBy(t => t.X).ThenBy(t => t.Y).ThenBy(t => t.Id).ToList();
        for (var i = 0; i + 2 < ordered.Count; i++)
        {
            var window = ordered.GetRange(i, 3);
            if (!LooksLikeOpenMeld(window))
                continue;
            if (window.Count(t => IsSideways(t.Rotation, t.Width, t.Height)) != 1)
                continue;
            return window;
        }

        if (cluster.Count >= 4)
        {
            var four = ordered.Take(4).ToList();
            if (LooksLikeOpenMeld(four) && four.Count(t => IsSideways(t.Rotation, t.Width, t.Height)) == 1)
                return four;
        }

        return null;
    }

    internal static List<List<Tile>> ClusterByGap(List<Tile> tiles, float gapPx)
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
                    var candidate = remaining[i];
                    if (cluster.Any(t => Distance(t, candidate) < gapPx))
                    {
                        cluster.Add(candidate);
                        remaining.RemoveAt(i);
                        grew = true;
                    }
                }
            }

            clusters.Add(cluster);
        }

        return clusters;
    }

    private static Kind? GuessMeldOwner(
        IReadOnlyList<OpponentAreaClassifier.Tile> group,
        Dictionary<Kind, uint> pondParents,
        Dictionary<Kind, List<Tile>> pondTiles)
    {
        if (group.Count == 0)
            return null;

        foreach (var (pondKind, parentId) in pondParents)
        {
            if (parentId != 0 && parentId == group[0].ParentNodeId)
                return MeldKindForPond(pondKind);
        }

        var hints = new List<OpponentAreaClassifier.PondHint>();
        foreach (var (pondKind, pond) in pondTiles)
        {
            if (pond.Count == 0)
                continue;
            var useAbs = UseAbs(pond);
            hints.Add(new OpponentAreaClassifier.PondHint(
                pondKind,
                pond.Average(useAbs ? t => t.AbsX : t => t.X),
                pond.Average(useAbs ? t => t.AbsY : t => t.Y)));
        }

        if (hints.Count == 0)
            return null;

        return OpponentAreaClassifier.GuessOwner(group, hints, playerStripAbsY: null);
    }

    private static bool UseAbs(List<Tile> tiles)
        => tiles.Any(t => t.AbsX != 0 || t.AbsY != 0);

    private static float Distance(Tile a, Tile b)
    {
        var useAbs = a.AbsX != 0 || a.AbsY != 0 || b.AbsX != 0 || b.AbsY != 0;
        var dx = useAbs ? a.AbsX - b.AbsX : a.X - b.X;
        var dy = useAbs ? a.AbsY - b.AbsY : a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
