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
