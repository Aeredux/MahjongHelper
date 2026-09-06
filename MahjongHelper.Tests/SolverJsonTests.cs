using System.Text.Json;
using MahjongHelper.Mahjong;
using Xunit;

namespace MahjongHelper.Tests;

public class SolverJsonTests
{
    [Fact]
    public void Serialize_keeps_aka_dora_pond_tsumogiri_melds_and_winds()
    {
        var request = new SuggestMoveRequest
        {
            Hand = ["M1", "M0", "P2"],
            DrawnTile = "S0",
            Dora = ["S2"],
            DiscardTiles = ["P0", "EAST"],
            SeatWind = "SOUTH",
            RoundWind = "EAST",
            Melds = [new MeldInfo { Type = "CHI", Tiles = ["M4", "M5", "M6"] }],
            Player = new OpponentInfo
            {
                Wind = "SOUTH",
                Discards =
                [
                    new DiscardedTile { Tile = "P0", Tsumogiri = false },
                    new DiscardedTile { Tile = "EAST", Tsumogiri = true },
                ],
                Melds = [new MeldInfo { Type = "CHI", Tiles = ["M4", "M5", "M6"] }],
            },
            Opponents =
            [
                new OpponentInfo
                {
                    Wind = "WEST",
                    Discards = [new DiscardedTile { Tile = "S9", Tsumogiri = true }],
                    Melds = [new MeldInfo { Type = "PON", Tiles = ["WHITE", "WHITE", "WHITE"] }],
                },
            ],
        };

        var json = SolverJson.Serialize(request);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("M0", root.GetProperty("hand")[1].GetString());
        Assert.Equal("S0", root.GetProperty("drawn_tile").GetString());
        Assert.Equal("S2", root.GetProperty("dora")[0].GetString());
        Assert.Equal("P0", root.GetProperty("discard_tiles")[0].GetString());
        Assert.Equal("SOUTH", root.GetProperty("seat_wind").GetString());
        Assert.Equal("EAST", root.GetProperty("round_wind").GetString());
        Assert.Equal("CHI", root.GetProperty("melds")[0].GetProperty("type").GetString());
        Assert.True(root.GetProperty("player").GetProperty("discards")[1].GetProperty("tsumogiri").GetBoolean());
        Assert.Equal("PON", root.GetProperty("opponents")[0].GetProperty("melds")[0].GetProperty("type").GetString());
        Assert.DoesNotContain("indicator", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildSnapSummary_counts_aka_in_pond_dora_and_melds()
    {
        var request = new SuggestMoveRequest
        {
            Hand = ["M1", "M2"],
            DrawnTile = "P3",
            Dora = ["S0"],
            DiscardTiles = ["P0"],
            Melds = [new MeldInfo { Type = "PON", Tiles = ["M5", "M0", "M5"] }],
            SeatWind = "EAST",
            RoundWind = "EAST",
            Player = new OpponentInfo
            {
                Wind = "EAST",
                Discards = [new DiscardedTile { Tile = "P0", Tsumogiri = true }],
                Melds = [new MeldInfo { Type = "PON", Tiles = ["M5", "M0", "M5"] }],
            },
            Opponents =
            [
                new OpponentInfo
                {
                    Wind = "SOUTH",
                    Discards = [new DiscardedTile { Tile = "S2", Tsumogiri = false }],
                },
            ],
        };

        Assert.Equal(3, SolverJson.CountAka(request));
        var summary = SolverJson.BuildSnapSummary(request);
        Assert.Contains("dora=[S0]", summary);
        Assert.Contains("pond=[P0]", summary);
        Assert.Contains("aka=3", summary);
        Assert.Contains("tsumogiri=1", summary);
        Assert.Contains("ownMelds=1", summary);
        Assert.Contains("seat=EAST", summary);
        Assert.Contains("round=EAST", summary);
    }
}
