using System;
using System.Collections.Generic;
using System.Linq;

namespace MahjongHelper.Mahjong;

public sealed record ObservedMeld(string Type, IReadOnlyList<string> Tiles)
{
    public override string ToString() => Tiles.Count == 0 ? Type : $"{Type}:{string.Join(" ", Tiles)}";
}

/// <summary>
/// Groups face-up called tiles into solver meld objects (CHI / PON / KAN_*).
/// Aka tiles stay as M0/P0/S0 in the output; equality treats them as 5s.
/// </summary>
public static class MeldClassifier
{
    public static IReadOnlyList<ObservedMeld> SplitIntoMelds(IReadOnlyList<string> tiles, bool acceptPairRemainder = false)
    {
        var result = new List<ObservedMeld>();
        if (tiles == null || tiles.Count == 0)
            return result;

        var remaining = tiles.Where(IsUsableTile).ToList();
        var i = 0;
        while (i < remaining.Count)
        {
            if (remaining.Count - i >= 4)
            {
                var four = remaining.GetRange(i, 4);
                var meld4 = InferMeld(four);
                if (meld4 != null && meld4.Type.StartsWith("KAN", StringComparison.Ordinal))
                {
                    result.Add(meld4);
                    i += 4;
                    continue;
                }
            }

            if (remaining.Count - i >= 3)
            {
                var three = remaining.GetRange(i, 3);
                var meld3 = InferMeld(three);
                if (meld3 != null)
                {
                    result.Add(meld3);
                    i += 3;
                    continue;
                }
            }

            if (acceptPairRemainder && remaining.Count - i == 2)
            {
                var two = remaining.GetRange(i, 2);
                if (AllEqual(two.Select(CanonicalKey).ToList()))
                {
                    result.Add(new ObservedMeld("PON", two));
                    break;
                }
            }

            i++;
        }

        return result;
    }

    public static ObservedMeld? InferMeld(IReadOnlyList<string> tiles)
    {
        if (tiles == null || tiles.Count < 3)
            return null;

        var usable = tiles.Where(IsUsableTile).ToList();
        if (usable.Count < 3)
            return null;

        var keys = usable.Select(CanonicalKey).ToList();
        if (usable.Count == 4 && AllEqual(keys))
            return new ObservedMeld("KAN_OPEN", usable);

        if (usable.Count == 3 && AllEqual(keys))
            return new ObservedMeld("PON", usable);

        if (usable.Count == 3 && IsChi(keys))
            return new ObservedMeld("CHI", usable);

        return null;
    }

    public static bool IsOpenMeld(string? type)
        => type is "CHI" or "PON" or "KAN_OPEN" or "KAN_ADDED";

    public static bool IsUsableTile(string? tile)
        => !string.IsNullOrWhiteSpace(tile) && tile != "?" && !tile.StartsWith("ICON_", StringComparison.Ordinal);

    /// <summary>
    /// Aka-aware equality key: M0/P0/S0 compare as the matching 5, but only for grouping.
    /// </summary>
    public static string CanonicalKey(string tile)
        => tile switch
        {
            "M0" => "M5",
            "P0" => "P5",
            "S0" => "S5",
            _ => tile,
        };

    private static bool AllEqual(IReadOnlyList<string> keys)
    {
        if (keys.Count == 0)
            return false;
        var first = keys[0];
        for (var i = 1; i < keys.Count; i++)
        {
            if (!string.Equals(keys[i], first, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static bool IsChi(IReadOnlyList<string> keys)
    {
        if (keys.Count != 3)
            return false;

        var parsed = new List<(char Suit, int Rank)>(3);
        foreach (var key in keys)
        {
            if (key.Length != 2 || key[0] is not ('M' or 'P' or 'S') || key[1] < '1' || key[1] > '9')
                return false;
            parsed.Add((key[0], key[1] - '0'));
        }

        if (parsed[0].Suit != parsed[1].Suit || parsed[1].Suit != parsed[2].Suit)
            return false;

        var ranks = parsed.Select(p => p.Rank).OrderBy(r => r).ToArray();
        return ranks[1] == ranks[0] + 1 && ranks[2] == ranks[1] + 1;
    }
}
