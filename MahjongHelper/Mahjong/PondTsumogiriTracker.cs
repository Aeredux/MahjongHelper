using System;
using System.Collections.Generic;

namespace MahjongHelper.Mahjong;

/// <summary>
/// Derives per-pond tsumogiri flags from draw/discard behavior plus UI rotation.
/// Player: a newly appended pond tile that matches the last seen draw is tsumogiri.
/// Opponents: use observed sideways/rotated discard tiles; flags persist while the pond prefix is stable.
/// </summary>
public sealed class PondTsumogiriTracker
{
    public const int Player = 0;
    public const int Right = 1;
    public const int Opposite = 2;
    public const int Left = 3;

    private readonly string[]?[] _ponds = new string[4][];
    private readonly bool[]?[] _flags = new bool[4][];
    private string? _lastPlayerDraw;

    public void Reset()
    {
        Array.Clear(_ponds);
        Array.Clear(_flags);
        _lastPlayerDraw = null;
    }

    public IReadOnlyList<bool> Update(
        int playerIndex,
        IReadOnlyList<string>? pond,
        IReadOnlyList<bool>? observedTsumogiri,
        string? currentPlayerDraw)
    {
        if (playerIndex is < 0 or > 3)
            return Array.Empty<bool>();

        if (playerIndex == Player && MeldClassifier.IsUsableTile(currentPlayerDraw))
            _lastPlayerDraw = currentPlayerDraw;

        var tiles = pond != null ? ToArray(pond) : [];
        var observed = observedTsumogiri != null ? ToArray(observedTsumogiri) : [];
        var prevTiles = _ponds[playerIndex] ?? [];
        var prevFlags = _flags[playerIndex] ?? [];

        var shared = SharedPrefixLength(prevTiles, tiles);
        if (tiles.Length < prevTiles.Length)
            shared = 0;

        var flags = new bool[tiles.Length];
        var pondGrew = tiles.Length > prevTiles.Length;
        for (var i = 0; i < tiles.Length; i++)
        {
            var fromUi = i < observed.Length && observed[i];
            var fromHistory = i < shared && i < prevFlags.Length && prevFlags[i];
            var fromDraw = playerIndex == Player
                           && pondGrew
                           && i == tiles.Length - 1
                           && string.Equals(tiles[i], _lastPlayerDraw, StringComparison.Ordinal);
            flags[i] = fromUi || fromHistory || fromDraw;
        }

        _ponds[playerIndex] = tiles;
        _flags[playerIndex] = flags;

        if (playerIndex == Player && pondGrew)
            _lastPlayerDraw = null;

        return flags;
    }

    private static int SharedPrefixLength(string[] prev, string[] next)
    {
        var n = Math.Min(prev.Length, next.Length);
        var i = 0;
        while (i < n && string.Equals(prev[i], next[i], StringComparison.Ordinal))
            i++;
        return i;
    }

    private static string[] ToArray(IReadOnlyList<string> list)
    {
        var arr = new string[list.Count];
        for (var i = 0; i < list.Count; i++)
            arr[i] = list[i];
        return arr;
    }

    private static bool[] ToArray(IReadOnlyList<bool> list)
    {
        var arr = new bool[list.Count];
        for (var i = 0; i < list.Count; i++)
            arr[i] = list[i];
        return arr;
    }
}
