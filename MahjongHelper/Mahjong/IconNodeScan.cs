using System;
using System.Collections.Generic;
using System.Linq;

namespace MahjongHelper.Mahjong;

/// <summary>
/// Icon-first tile discovery. Live AZPC after 78fd2f1 had visible fuuro but
/// no 42×55 / 55×42 leftovers — size filters never saw those nodes.
/// Collect by mahjong icon id, then reject the known 34×45 type-1045 echo.
/// </summary>
public static class IconNodeScan
{
    public const uint MinMahjongIcon = 76041;
    public const uint MaxMahjongIcon = 76150;

    public static bool IsMahjongTileIcon(uint iconId)
        => iconId >= MinMahjongIcon && iconId <= MaxMahjongIcon;

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
}
