using System.Collections.Generic;
using System.Linq;

namespace SamplePlugin.Mahjong;

/// <summary>
/// Converts MahjongGameState into server API request objects.
/// Tile codes are already in the server's expected format (M1-M9, P1-P9, S1-S9,
/// EAST/SOUTH/WEST/NORTH, WHITE/GREEN/RED, M0/P0/S0).
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
    /// Returns null if there isn't enough data (no hand tiles).
    /// </summary>
    public static SuggestMoveRequest? BuildSuggestMoveRequest(MahjongGameState state, MahjongIconMap iconMap)
    {
        var hand = ResolveHand(state, iconMap);
        if (hand == null || hand.Count == 0)
            return null;

        var drawnTile = ResolveDrawnTile(state, iconMap);

        // Server expects all 14 tiles in the hand array (including drawn tile)
        if (drawnTile != null)
            hand.Add(drawnTile);

        var request = new SuggestMoveRequest
        {
            Hand = hand,
            Discards = BuildDiscardPools(state),
            DoraIndicators = ResolveDoraIndicators(state),
            SeatWind = state.SeatWind.Value is int sw ? WindNames.GetValueOrDefault(sw) : null,
            RoundWind = state.RoundWind.Value is int rw ? WindNames.GetValueOrDefault(rw) : null,
            RoundNumber = state.RoundNumber.Value is int rn and > 0 ? rn : null,
        };

        return request;
    }

    /// <summary>
    /// Builds an evaluate-call request.
    /// callTile is the tile being offered (e.g., the discard you can chi/pon/ron).
    /// callType is "chi", "pon", "kan", "ron", "tsumo", or "riichi".
    /// </summary>
    public static EvaluateCallRequest? BuildEvaluateCallRequest(
        MahjongGameState state, MahjongIconMap iconMap, string? callTile, string? callType)
    {
        var hand = ResolveHand(state, iconMap);
        if (hand == null || hand.Count == 0)
            return null;

        return new EvaluateCallRequest
        {
            Hand = hand,
            CallTile = callTile,
            CallType = callType,
            Discards = BuildDiscardPools(state),
            DoraIndicators = ResolveDoraIndicators(state),
            SeatWind = state.SeatWind.Value is int sw ? WindNames.GetValueOrDefault(sw) : null,
            RoundWind = state.RoundWind.Value is int rw ? WindNames.GetValueOrDefault(rw) : null,
        };
    }

    /// <summary>
    /// Resolves the player's hand tile codes from icon IDs.
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
    /// Resolves the drawn tile code from its icon ID.
    /// </summary>
    private static string? ResolveDrawnTile(MahjongGameState state, MahjongIconMap iconMap)
    {
        if (state.DrawIconId.Value is uint drawId and > 0)
            return iconMap.Resolve(drawId);
        return null;
    }

    /// <summary>
    /// Builds discard pools from state. Filters out unresolved tile codes.
    /// </summary>
    private static DiscardPools BuildDiscardPools(MahjongGameState state)
    {
        return new DiscardPools
        {
            Player = FilterValidTiles(state.PlayerDiscards.Value),
            Right = FilterValidTiles(state.RightDiscards.Value),
            Opposite = FilterValidTiles(state.OppositeDiscards.Value),
            Left = FilterValidTiles(state.LeftDiscards.Value),
        };
    }

    /// <summary>
    /// Resolves dora indicator tile codes.
    /// </summary>
    private static List<string>? ResolveDoraIndicators(MahjongGameState state)
    {
        if (state.DoraIndicators.Value is not { Count: > 0 } indicators)
            return null;

        var valid = FilterValidTiles(indicators);
        return valid.Count > 0 ? valid : null;
    }

    /// <summary>
    /// Filters out placeholder/unresolved tile strings (like "?" or "ICON_*").
    /// </summary>
    private static List<string> FilterValidTiles(IReadOnlyList<string>? tiles)
    {
        if (tiles == null || tiles.Count == 0)
            return [];

        return tiles.Where(t => !string.IsNullOrWhiteSpace(t) && !t.StartsWith("ICON_") && t != "?").ToList();
    }
}
