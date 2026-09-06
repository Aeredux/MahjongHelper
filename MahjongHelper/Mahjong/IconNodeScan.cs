using System;
using System.Collections.Generic;
using System.Linq;

namespace MahjongHelper.Mahjong;

/// <summary>
/// Icon-first tile discovery. Live AZPC after 78fd2f1 had visible fuuro but
/// no 42×55 / 55×42 leftovers — size filters never saw those nodes.
/// Collect by mahjong icon id (any type/size, any nesting depth), then reject
/// the known 34×45 type-1045 echo.
/// </summary>
public static class IconNodeScan
{
    public const uint MinMahjongIcon = 76041;
    public const uint MaxMahjongIcon = 76150;
    public const int MinTilePx = 16;
    public const int MaxTilePx = 80;
    public const float LeafSnapPx = 8f;
    public const ushort ImageNodeType = 2;
    public const ushort CallCueNodeType = 1056;
    public const ushort FuuroTrayNodeType = 1060;
    public const int FaceLeafShortPx = 40;
    public const int FaceLeafLongPx = 52;
    public const int ClosedTileWidthPx = 42;
    public const float OwnFuuroPackGapPx = 40f;

    public static bool IsMahjongTileIcon(uint iconId)
        => iconId >= MinMahjongIcon && iconId <= MaxMahjongIcon;

    /// <summary>
    /// Discovery window for /mj snap <c>tileSizedNodes</c>. Not a fuuro-type
    /// guess — just "small enough to be a tile, large enough to not be chrome".
    /// </summary>
    public static bool IsTileSized(int width, int height)
    {
        var min = Math.Min(width, height);
        var max = Math.Max(width, height);
        return min >= MinTilePx && max <= MaxTilePx;
    }

    /// <summary>
    /// Live AZPC after 7ad3d5d: own and across fuuro faces are AtkImageNode
    /// type 2 at 40×52 (or 52×40 when the called tile is sideways).
    /// </summary>
    public static bool IsFaceLeaf(ushort nodeType, int width, int height)
        => nodeType == ImageNodeType
           && ((width == FaceLeafShortPx && height == FaceLeafLongPx)
               || (width == FaceLeafLongPx && height == FaceLeafShortPx));

    /// <summary>
    /// Open fuuro always has a sideways called tile. Doman often puts that
    /// flag on a type-1056 sibling (Rotation≈4.712 / 270°) instead of a
    /// type-2 52×40 leaf.
    /// </summary>
    public static bool HasCallCue(int width, int height, float rotation = 0)
        => SmallTileClassifier.IsSideways(rotation, width, height);

    public static bool IsCallCueNode(ushort nodeType, int width, int height, float rotation)
        => nodeType == CallCueNodeType && HasCallCue(width, height, rotation);

    public static bool IsFuuroTray(ushort nodeType, int width, int height)
        => nodeType == FuuroTrayNodeType && width >= 100 && height >= 45 && height <= 80;

    public readonly record struct Tray(float AbsX, float AbsY, int Width, int Height);

    public static bool ClusterHasCallCue<T>(
        IEnumerable<T> tiles,
        Func<T, int> width,
        Func<T, int> height,
        Func<T, float> rotation)
        => tiles.Any(t => HasCallCue(width(t), height(t), rotation(t)));

    /// <summary>Kept for tests; now any sideways sibling counts, including type-1056.</summary>
    public static bool FaceLeafGroupHasCallCue<T>(
        IEnumerable<T> tiles,
        Func<T, ushort> nodeType,
        Func<T, int> width,
        Func<T, int> height,
        Func<T, float> rotation)
        => ClusterHasCallCue(tiles, width, height, rotation);

    public static bool ClusterCoveredByTray<T>(
        IReadOnlyList<T> tiles,
        IReadOnlyList<Tray>? trays,
        Func<T, float> absX,
        Func<T, float> absY,
        Func<T, int> width,
        Func<T, int> height)
    {
        if (trays == null || trays.Count == 0 || tiles.Count == 0)
            return false;

        var centers = tiles
            .Select(t => (X: absX(t) + width(t) * 0.5f, Y: absY(t) + height(t) * 0.5f))
            .ToList();
        foreach (var tray in trays)
        {
            var covered = centers.Count(c =>
                c.X >= tray.AbsX && c.X <= tray.AbsX + tray.Width
                && c.Y >= tray.AbsY && c.Y <= tray.AbsY + tray.Height);
            if (covered >= 2)
                return true;
        }

        return false;
    }

    public static bool ClusterRightOfClosedPack<T>(
        IReadOnlyList<T> tiles,
        float? packMaxAbsX,
        Func<T, float> absX)
    {
        if (packMaxAbsX is not float max || tiles.Count == 0)
            return false;
        return tiles.Min(absX) >= max + ClosedTileWidthPx + OwnFuuroPackGapPx;
    }

    /// <summary>
    /// Own leftover type-2 / 1056 groups are real fuuro only when they have a
    /// call cue and either sit in a type-1060 tray or start past the closed
    /// 1055 pack. Ghost WEST 1056+type-2 at AbsX≈1385–1484 fails both.
    /// </summary>
    public static bool IsPlausibleOwnLeftoverFuuro<T>(
        IReadOnlyList<T> tiles,
        float? packMaxAbsX,
        IReadOnlyList<Tray>? trays,
        Func<T, int> width,
        Func<T, int> height,
        Func<T, float> rotation,
        Func<T, float> absX,
        Func<T, float> absY)
    {
        if (!ClusterHasCallCue(tiles, width, height, rotation))
            return false;
        return ClusterCoveredByTray(tiles, trays, absX, absY, width, height)
               || ClusterRightOfClosedPack(tiles, packMaxAbsX, absX);
    }

    /// <summary>
    /// Live parent-60 strip: seven type-1045 34×45 tiles at AbsY≈512 that
    /// shadow a previous hand. Never treat these as fuuro.
    /// </summary>
    public static bool IsType1045PondEcho(ushort nodeType, int width, int height)
        => nodeType == 1045
           && ((width == 34 && height == 45) || (width == 45 && height == 34));

    public static IReadOnlyList<T> RejectPondEcho<T>(
        IEnumerable<T> nodes,
        Func<T, ushort> nodeType,
        Func<T, int> width,
        Func<T, int> height)
        => nodes.Where(n => !IsType1045PondEcho(nodeType(n), width(n), height(n))).ToList();

    /// <summary>
    /// When the deep walk records both a tray and the tiles inside it, keep
    /// the smaller node at each snapped (icon, x, y). Otherwise a 3-tile PON
    /// plus its parent becomes a 4-tile KAN.
    /// </summary>
    public static IReadOnlyList<T> PreferLeafTiles<T>(
        IEnumerable<T> nodes,
        Func<T, uint> iconId,
        Func<T, float> absX,
        Func<T, float> absY,
        Func<T, int> width,
        Func<T, int> height)
    {
        var best = new Dictionary<(uint Icon, int X, int Y), T>();
        foreach (var node in nodes)
        {
            var key = (
                iconId(node),
                (int)MathF.Round(absX(node) / LeafSnapPx),
                (int)MathF.Round(absY(node) / LeafSnapPx));
            if (!best.TryGetValue(key, out var existing)
                || width(node) * height(node) < width(existing) * height(existing))
                best[key] = node;
        }

        return best.Values.ToList();
    }

    /// <summary>
    /// Drop a node that spatially covers two or more smaller icon nodes
    /// (meld tray / player-area wrappers). Does not assume a fuuro size.
    /// </summary>
    public static IReadOnlyList<T> DropContainers<T>(
        IReadOnlyList<T> nodes,
        Func<T, float> absX,
        Func<T, float> absY,
        Func<T, int> width,
        Func<T, int> height)
    {
        if (nodes == null || nodes.Count == 0)
            return Array.Empty<T>();

        var keep = new List<T>(nodes.Count);
        for (var i = 0; i < nodes.Count; i++)
        {
            var a = nodes[i];
            var ax = absX(a);
            var ay = absY(a);
            var aw = width(a);
            var ah = height(a);
            var covered = 0;
            for (var j = 0; j < nodes.Count; j++)
            {
                if (i == j)
                    continue;
                var b = nodes[j];
                if (width(b) * height(b) >= aw * ah)
                    continue;
                var cx = absX(b) + width(b) * 0.5f;
                var cy = absY(b) + height(b) * 0.5f;
                if (cx >= ax && cx <= ax + aw && cy >= ay && cy <= ay + ah)
                    covered++;
            }

            if (covered < 2)
                keep.Add(a);
        }

        return keep;
    }
}
