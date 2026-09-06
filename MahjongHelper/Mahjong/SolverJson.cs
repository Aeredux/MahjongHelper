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

    /// <summary>
    /// Chat / log one-liner for <c>/mj snap</c>. Aka is counted in hand, draw, dora, ponds, and melds.
    /// </summary>
    public static string BuildSnapSummary(SuggestMoveRequest? request)
    {
        if (request == null)
            return "no payload";

        var dora = request.Dora == null ? "null" : string.Join(" ", request.Dora);
        var pond = request.DiscardTiles == null ? "null" : string.Join(" ", request.DiscardTiles);
        var ownMelds = request.Melds == null ? 0 : request.Melds.Count;
        var oppMelds = request.Opponents?.Sum(o => o.Melds?.Count ?? 0) ?? 0;
        var tsumogiri = 0;
        if (request.Opponents != null)
            tsumogiri += request.Opponents.SelectMany(o => o.Discards).Count(d => d.Tsumogiri);
        if (request.Player != null)
            tsumogiri += request.Player.Discards.Count(d => d.Tsumogiri);
        var aka = CountAka(request);
        return $"hand={request.Hand.Count} draw={request.DrawnTile ?? "-"} dora=[{dora}] pond=[{pond}] aka={aka} tsumogiri={tsumogiri} ownMelds={ownMelds} oppMelds={oppMelds} seat={request.SeatWind ?? "-"} round={request.RoundWind ?? "-"}";
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
