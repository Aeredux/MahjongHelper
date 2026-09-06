using System.Collections.Generic;
using System.Linq;

namespace MahjongHelper.Mahjong;

/// <summary>
/// Converts MahjongGameState into server API request objects.
/// Aka tiles stay as M0/P0/S0. Doman dora panel tiles are sent as-is (no Tenhou remap).
/// </summary>
public static class GameStateMapper
{
    private static readonly Dictionary<int, string> WindNames = new()
    {
        [0] = "EAST",
        [1] = "SOUTH",
        [2] = "WEST",
        [3] = "NORTH",
    };

    /// <summary>
    /// Builds a suggest-move request from the current game state.
    /// Server expects 13 tiles in hand + separate drawn_tile.
    /// Returns null if there isn't enough data (no hand tiles).
    /// </summary>
    public static SuggestMoveRequest? BuildSuggestMoveRequest(MahjongGameState state, MahjongIconMap iconMap)
    {
        var hand = ResolveHand(state, iconMap);
        if (hand == null || hand.Count == 0)
            return null;

        var drawnTile = ResolveDrawnTile(state, iconMap);

        // hand should be 13 tiles, drawn_tile sent separately
        // If we have 14 tiles in hand and no separate drawn tile, split the last one
        if (drawnTile == null && hand.Count == 14)
        {
            drawnTile = hand[^1];
            hand.RemoveAt(hand.Count - 1);
        }

        var playerMelds = ToMeldInfos(state.PlayerMelds.Value);
        var playerPond = BuildPond(state.PlayerDiscards.Value, state.PlayerTsumogiri.Value);
        var player = BuildPlayerInfo(state, playerPond, playerMelds);

        return new SuggestMoveRequest
        {
            Hand = hand,
            DrawnTile = drawnTile,
            Opponents = BuildOpponents(state),
            SeatWind = state.SeatWind.Value is int sw ? WindNames.GetValueOrDefault(sw) : null,
            RoundWind = state.RoundWind.Value is int rw ? WindNames.GetValueOrDefault(rw) : null,
            Dora = FilterValidTiles(state.DoraIndicators.Value),
            DiscardTiles = playerPond.Select(d => d.Tile).ToList(),
            Melds = playerMelds,
            Player = player,
        };
    }

    /// <summary>
    /// Returns the total tile count (hand + drawn) for the current request.
    /// Used by Plugin.cs to check if the player has 14 tiles.
    /// </summary>
    public static int GetTotalTileCount(SuggestMoveRequest request)
        => request.Hand.Count + (request.DrawnTile != null ? 1 : 0);

    /// <summary>
    /// Builds an evaluate-call request.
    /// callTile is the tile being offered (e.g., the discard you can chi/pon/ron).
    /// callType should be UPPERCASE: "RON", "PON", "CHI", "KAN", "TSUMO", "RIICHI".
    /// </summary>
    public static EvaluateCallRequest? BuildEvaluateCallRequest(
        MahjongGameState state, MahjongIconMap iconMap, string? callTile, string? callType)
    {
        var hand = ResolveHand(state, iconMap);
        if (hand == null || hand.Count == 0)
            return null;

        var playerMelds = ToMeldInfos(state.PlayerMelds.Value);
        var playerPond = BuildPond(state.PlayerDiscards.Value, state.PlayerTsumogiri.Value);
        var hasOpenMeld = playerMelds.Exists(m => MeldClassifier.IsOpenMeld(m.Type));

        return new EvaluateCallRequest
        {
            Hand = hand,
            CallTile = MeldClassifier.IsUsableTile(callTile) ? callTile : null,
            CallType = callType?.ToUpperInvariant(),
            Menzen = !hasOpenMeld,
            PlayerScore = state.PlayerScore.Value is int ps and > 0 ? ps : null,
            Opponents = BuildOpponents(state),
            SeatWind = state.SeatWind.Value is int sw ? WindNames.GetValueOrDefault(sw) : null,
            RoundWind = state.RoundWind.Value is int rw ? WindNames.GetValueOrDefault(rw) : null,
            Dora = FilterValidTiles(state.DoraIndicators.Value),
            DiscardTiles = playerPond.Select(d => d.Tile).ToList(),
            Melds = playerMelds,
            Player = BuildPlayerInfo(state, playerPond, playerMelds),
        };
    }

    /// <summary>
    /// Resolves the player's hand tile codes from icon IDs, preserving aka (M0/P0/S0).
    /// </summary>
    private static List<string>? ResolveHand(MahjongGameState state, MahjongIconMap iconMap)
    {
        if (state.HandIconIds.Value is not { Count: > 0 } iconIds)
            return null;

        var tiles = new List<string>();
        foreach (var id in iconIds)
        {
            var code = iconMap.Resolve(id);
            if (code != null)
                tiles.Add(code);
        }

        return tiles.Count > 0 ? tiles : null;
    }

    /// <summary>
    /// Resolves the drawn tile code from its icon ID, preserving aka (M0/P0/S0).
    /// </summary>
    private static string? ResolveDrawnTile(MahjongGameState state, MahjongIconMap iconMap)
    {
        if (state.DrawIconId.Value is uint drawId and > 0)
            return iconMap.Resolve(drawId);
        return null;
    }

    private static OpponentInfo? BuildPlayerInfo(
        MahjongGameState state,
        List<DiscardedTile> pond,
        List<MeldInfo> melds)
    {
        var riichiStatus = state.RiichiStatus.Value;
        var riichi = riichiStatus is { Count: >= 1 } && riichiStatus[0];
        var wind = state.SeatWind.Value is int sw ? WindNames.GetValueOrDefault(sw) : null;
        if (pond.Count == 0 && melds.Count == 0 && !riichi && wind == null)
            return null;

        return new OpponentInfo
        {
            Wind = wind,
            Discards = pond,
            Riichi = riichi,
            Melds = melds,
        };
    }

    /// <summary>
    /// Builds opponent info list from state discard pools, tsumogiri, riichi, and melds.
    /// Opponents are: Right (index 1), Opposite (index 2), Left (index 3).
    /// </summary>
    private static List<OpponentInfo>? BuildOpponents(MahjongGameState state)
    {
        var opponents = new List<OpponentInfo>();
        var riichiStatus = state.RiichiStatus.Value;
        var seatWind = state.SeatWind.Value is int sw ? sw : -1;

        TryAddOpponent(opponents, state.RightDiscards.Value, state.RightTsumogiri.Value, state.RightMelds.Value,
            seatWind, 1, riichiStatus is { Count: >= 4 } && riichiStatus[1]);
        TryAddOpponent(opponents, state.OppositeDiscards.Value, state.OppositeTsumogiri.Value, state.OppositeMelds.Value,
            seatWind, 2, riichiStatus is { Count: >= 4 } && riichiStatus[2]);
        TryAddOpponent(opponents, state.LeftDiscards.Value, state.LeftTsumogiri.Value, state.LeftMelds.Value,
            seatWind, 3, riichiStatus is { Count: >= 4 } && riichiStatus[3]);

        return opponents.Count > 0 ? opponents : null;
    }

    private static void TryAddOpponent(
        List<OpponentInfo> opponents,
        IReadOnlyList<string>? discards,
        IReadOnlyList<bool>? tsumogiri,
        IReadOnlyList<ObservedMeld>? melds,
        int seatWind,
        int offset,
        bool riichi)
    {
        var pond = BuildPond(discards, tsumogiri);
        var meldInfos = ToMeldInfos(melds);
        if (pond.Count == 0 && meldInfos.Count == 0 && !riichi)
            return;

        opponents.Add(new OpponentInfo
        {
            Wind = GetOpponentWind(seatWind, offset),
            Discards = pond,
            Riichi = riichi,
            Melds = meldInfos,
        });
    }

    /// <summary>
    /// Given the player's seat wind index (0=E,1=S,2=W,3=N) and a relative offset,
    /// returns the opponent's wind name.
    /// </summary>
    private static string? GetOpponentWind(int playerSeatWind, int offset)
    {
        if (playerSeatWind < 0) return null;
        return WindNames.GetValueOrDefault((playerSeatWind + offset) % 4);
    }

    private static List<DiscardedTile> BuildPond(IReadOnlyList<string>? tiles, IReadOnlyList<bool>? tsumogiri)
    {
        var result = new List<DiscardedTile>();
        if (tiles == null || tiles.Count == 0)
            return result;

        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            if (!MeldClassifier.IsUsableTile(tile))
                continue;
            result.Add(new DiscardedTile
            {
                Tile = tile,
                Tsumogiri = tsumogiri != null && i < tsumogiri.Count && tsumogiri[i],
            });
        }

        return result;
    }

    private static List<MeldInfo> ToMeldInfos(IReadOnlyList<ObservedMeld>? melds)
    {
        if (melds == null || melds.Count == 0)
            return [];

        return melds
            .Where(m => m.Tiles.Count > 0)
            .Select(m => new MeldInfo
            {
                Type = m.Type,
                Tiles = m.Tiles.Where(MeldClassifier.IsUsableTile).ToList(),
            })
            .Where(m => m.Tiles.Count > 0)
            .ToList();
    }

    /// <summary>
    /// Filters out placeholder/unresolved tile strings (like "?" or "ICON_*").
    /// Preserves aka M0/P0/S0.
    /// </summary>
    private static List<string> FilterValidTiles(IReadOnlyList<string>? tiles)
    {
        if (tiles == null || tiles.Count == 0)
            return [];

        return tiles.Where(MeldClassifier.IsUsableTile).ToList();
    }
}
