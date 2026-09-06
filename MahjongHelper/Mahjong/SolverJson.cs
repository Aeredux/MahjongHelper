using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MahjongHelper.Mahjong;

/// <summary>
/// Shared snake_case JSON options matching <see cref="MahjongServerClient"/> POST bodies.
/// </summary>
public static class SolverJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    private static readonly string[] SeatWinds = ["EAST", "SOUTH", "WEST", "NORTH"];

    /// <summary>
    /// Chat / log lines for <c>/mj snap</c>. Stats stay on line 1; line 2 lists
    /// own + per-wind opponent meld contents from the solver payload.
    /// </summary>
    public static string BuildSnapSummary(SuggestMoveRequest? request)
        => string.Join(" ", BuildSnapSummaryLines(request));

    public static IReadOnlyList<string> BuildSnapSummaryLines(SuggestMoveRequest? request)
    {
        if (request == null)
            return ["no payload"];

        var dora = request.Dora == null ? "null" : string.Join(" ", request.Dora);
        var pond = request.DiscardTiles == null ? "null" : string.Join(" ", request.DiscardTiles);
        var ownMelds = request.Melds ?? request.Player?.Melds;
        var ownCount = ownMelds?.Count ?? 0;
        var oppMelds = request.Opponents?.Sum(o => o.Melds?.Count ?? 0) ?? 0;
        var tsumogiri = 0;
        if (request.Opponents != null)
            tsumogiri += request.Opponents.SelectMany(o => o.Discards).Count(d => d.Tsumogiri);
        if (request.Player != null)
            tsumogiri += request.Player.Discards.Count(d => d.Tsumogiri);
        var aka = CountAka(request);
        var stats =
            $"hand={request.Hand.Count} draw={request.DrawnTile ?? "-"} dora=[{dora}] pond=[{pond}] aka={aka} tsumogiri={tsumogiri} ownMelds={ownCount} oppMelds={oppMelds} seat={request.SeatWind ?? "-"} round={request.RoundWind ?? "-"}";
        return [stats, FormatMeldSummary(request, ownMelds)];
    }

    /// <summary>
    /// <c>PON S9×3</c> when every face matches; otherwise <c>CHI S4-S5-S6</c>.
    /// </summary>
    public static string FormatMeld(MeldInfo meld)
    {
        var tiles = meld.Tiles ?? [];
        if (tiles.Count == 0)
            return string.IsNullOrEmpty(meld.Type) ? "?" : meld.Type;
        if (tiles.TrueForAll(t => t == tiles[0]))
            return $"{meld.Type} {tiles[0]}×{tiles.Count}";
        return $"{meld.Type} {string.Join("-", tiles)}";
    }

    public static string FormatMeldList(IReadOnlyList<MeldInfo>? melds)
        => melds == null || melds.Count == 0
            ? "none"
            : string.Join(", ", melds.Select(FormatMeld));

    private static string FormatMeldSummary(SuggestMoveRequest request, IReadOnlyList<MeldInfo>? ownMelds)
    {
        var parts = new List<string> { $"own={FormatMeldList(ownMelds)}" };
        foreach (var wind in OpponentWinds(request))
        {
            var opponent = request.Opponents?.FirstOrDefault(o =>
                string.Equals(o.Wind, wind, StringComparison.OrdinalIgnoreCase));
            parts.Add($"{wind}={FormatMeldList(opponent?.Melds)}");
        }

        return string.Join(" | ", parts);
    }

    private static IEnumerable<string> OpponentWinds(SuggestMoveRequest request)
    {
        var seat = Array.FindIndex(SeatWinds, w =>
            string.Equals(w, request.SeatWind, StringComparison.OrdinalIgnoreCase));
        if (seat >= 0)
            return Enumerable.Range(1, 3).Select(off => SeatWinds[(seat + off) % 4]);

        return request.Opponents?
                   .Select(o => o.Wind)
                   .Where(w => !string.IsNullOrWhiteSpace(w))
                   .Select(w => w!)
                   .Distinct(StringComparer.OrdinalIgnoreCase)
               ?? [];
    }

    public static int CountAka(SuggestMoveRequest request)
    {
        var tiles = new List<string>();
        tiles.AddRange(request.Hand);
        if (request.DrawnTile != null)
            tiles.Add(request.DrawnTile);
        if (request.Dora != null)
            tiles.AddRange(request.Dora);

        // discard_tiles / player.discards and melds / player.melds are the same pond+fuuro.
        var ownPond = request.DiscardTiles
                       ?? request.Player?.Discards.Select(d => d.Tile).ToList();
        if (ownPond != null)
            tiles.AddRange(ownPond);

        var ownMelds = request.Melds ?? request.Player?.Melds;
        if (ownMelds != null)
            tiles.AddRange(ownMelds.SelectMany(m => m.Tiles));

        if (request.Opponents != null)
        {
            foreach (var opponent in request.Opponents)
            {
                tiles.AddRange(opponent.Discards.Select(d => d.Tile));
                if (opponent.Melds != null)
                    tiles.AddRange(opponent.Melds.SelectMany(m => m.Tiles));
            }
        }

        return tiles.Count(t => t is "M0" or "P0" or "S0");
    }
}
