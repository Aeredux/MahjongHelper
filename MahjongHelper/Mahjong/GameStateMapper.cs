using System.Collections.Generic;
using System.Linq;

namespace MahjongHelper.Mahjong;

/// <summary>
/// Converts MahjongGameState into server API request objects.
/// Tile codes from the icon map may include red dora notation (M0/P0/S0),
/// which are normalized to regular 5s (M5/P5/S5) for the server.
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
    /// Normalizes red dora tiles (M0/P0/S0) to regular 5s (M5/P5/S5).
    /// The server's TileType enum does not accept red dora notation.
    /// </summary>
    private static string NormalizeTile(string tile)
    {
        return tile switch
        {
            "M0" => "M5",
            "P0" => "P5",
            "S0" => "S5",
            _ => tile
        };
    }

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

        return new SuggestMoveRequest
        {
            Hand = hand,
            DrawnTile = drawnTile,
            Opponents = BuildOpponents(state),
            SeatWind = state.SeatWind.Value is int sw ? WindNames.GetValueOrDefault(sw) : null,
            RoundWind = state.RoundWind.Value is int rw ? WindNames.GetValueOrDefault(rw) : null,
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

        return new EvaluateCallRequest
        {
            Hand = hand,
            CallTile = callTile != null ? NormalizeTile(callTile) : null,
            CallType = callType?.ToUpperInvariant(),
            Menzen = true,
            PlayerScore = state.PlayerScore.Value is int ps and > 0 ? ps : null,
            Opponents = BuildOpponents(state),
            SeatWind = state.SeatWind.Value is int sw ? WindNames.GetValueOrDefault(sw) : null,
            RoundWind = state.RoundWind.Value is int rw ? WindNames.GetValueOrDefault(rw) : null,
        };
    }

    /// <summary>
    /// Resolves the player's hand tile codes from icon IDs.
    /// Normalizes red dora tiles (M0/P0/S0) to regular 5s for the server.
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
                tiles.Add(NormalizeTile(code));
        }

        return tiles.Count > 0 ? tiles : null;
    }

    /// <summary>
    /// Resolves the drawn tile code from its icon ID.
    /// Normalizes red dora tiles (M0/P0/S0) to regular 5s for the server.
    /// </summary>
    private static string? ResolveDrawnTile(MahjongGameState state, MahjongIconMap iconMap)
    {
        if (state.DrawIconId.Value is uint drawId and > 0)
        {
            var code = iconMap.Resolve(drawId);
            return code != null ? NormalizeTile(code) : null;
        }
        return null;
    }

    /// <summary>
    /// Builds opponent info list from state discard pools and riichi status.
    /// Opponents are: Right (index 1), Opposite (index 2), Left (index 3).
    /// </summary>
    private static List<OpponentInfo>? BuildOpponents(MahjongGameState state)
    {
        var opponents = new List<OpponentInfo>();
        var riichiStatus = state.RiichiStatus.Value;
        var seatWind = state.SeatWind.Value is int sw ? sw : -1;

        // Right opponent
        var rightDiscards = FilterValidTiles(state.RightDiscards.Value);
        if (rightDiscards.Count > 0 || (riichiStatus is { Count: >= 4 } && riichiStatus[1]))
        {
            opponents.Add(new OpponentInfo
            {
                Wind = GetOpponentWind(seatWind, 1),
                Discards = rightDiscards.Select(t => new DiscardedTile { Tile = t }).ToList(),
                Riichi = riichiStatus is { Count: >= 4 } && riichiStatus[1],
            });
        }

        // Opposite opponent
        var oppositeDiscards = FilterValidTiles(state.OppositeDiscards.Value);
        if (oppositeDiscards.Count > 0 || (riichiStatus is { Count: >= 4 } && riichiStatus[2]))
        {
            opponents.Add(new OpponentInfo
            {
                Wind = GetOpponentWind(seatWind, 2),
                Discards = oppositeDiscards.Select(t => new DiscardedTile { Tile = t }).ToList(),
                Riichi = riichiStatus is { Count: >= 4 } && riichiStatus[2],
            });
        }

        // Left opponent
        var leftDiscards = FilterValidTiles(state.LeftDiscards.Value);
        if (leftDiscards.Count > 0 || (riichiStatus is { Count: >= 4 } && riichiStatus[3]))
        {
            opponents.Add(new OpponentInfo
            {
                Wind = GetOpponentWind(seatWind, 3),
                Discards = leftDiscards.Select(t => new DiscardedTile { Tile = t }).ToList(),
                Riichi = riichiStatus is { Count: >= 4 } && riichiStatus[3],
            });
        }

        return opponents.Count > 0 ? opponents : null;
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

    /// <summary>
    /// Filters out placeholder/unresolved tile strings (like "?" or "ICON_*").
    /// Normalizes red dora tiles (M0/P0/S0) to regular 5s for the server.
    /// </summary>
    private static List<string> FilterValidTiles(IReadOnlyList<string>? tiles)
    {
        if (tiles == null || tiles.Count == 0)
            return [];

        return tiles
            .Where(t => !string.IsNullOrWhiteSpace(t) && !t.StartsWith("ICON_") && t != "?")
            .Select(NormalizeTile)
            .ToList();
    }
}
