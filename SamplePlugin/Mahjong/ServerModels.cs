using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SamplePlugin.Mahjong;

// ─── Request Models ───

public sealed class SuggestMoveRequest
{
    [JsonPropertyName("hand")]
    public List<string> Hand { get; set; } = [];

    [JsonPropertyName("drawn_tile")]
    public string? DrawnTile { get; set; }

    [JsonPropertyName("opponents")]
    public List<OpponentInfo>? Opponents { get; set; }

    [JsonPropertyName("seat_wind")]
    public string? SeatWind { get; set; }

    [JsonPropertyName("round_wind")]
    public string? RoundWind { get; set; }
}

public sealed class OpponentInfo
{
    [JsonPropertyName("wind")]
    public string? Wind { get; set; }

    [JsonPropertyName("discards")]
    public List<DiscardedTile> Discards { get; set; } = [];

    [JsonPropertyName("riichi")]
    public bool Riichi { get; set; }
}

public sealed class DiscardedTile
{
    [JsonPropertyName("tile")]
    public string Tile { get; set; } = "";

    [JsonPropertyName("tsumogiri")]
    public bool Tsumogiri { get; set; }
}

public sealed class EvaluateCallRequest
{
    [JsonPropertyName("hand")]
    public List<string> Hand { get; set; } = [];

    [JsonPropertyName("called_tile")]
    public string? CallTile { get; set; }

    [JsonPropertyName("call_type")]
    public string? CallType { get; set; }

    [JsonPropertyName("sequence_tiles")]
    public List<string>? SequenceTiles { get; set; }

    [JsonPropertyName("menzen")]
    public bool Menzen { get; set; } = true;

    [JsonPropertyName("player_score")]
    public int? PlayerScore { get; set; }

    [JsonPropertyName("open_kan")]
    public bool? OpenKan { get; set; }

    [JsonPropertyName("opponents")]
    public List<OpponentInfo>? Opponents { get; set; }

    [JsonPropertyName("seat_wind")]
    public string? SeatWind { get; set; }

    [JsonPropertyName("round_wind")]
    public string? RoundWind { get; set; }
}

public sealed class ValidateMoveRequest
{
    [JsonPropertyName("hand")]
    public List<string> Hand { get; set; } = [];

    [JsonPropertyName("discard_tile")]
    public string? DiscardTile { get; set; }

    [JsonPropertyName("drawn_tile")]
    public string? DrawnTile { get; set; }

    [JsonPropertyName("riichi")]
    public bool? Riichi { get; set; }
}

// ─── Response Models ───

public sealed class SuggestMoveResponse
{
    [JsonPropertyName("suggestions")]
    public List<DiscardSuggestion> Suggestions { get; set; } = [];

    [JsonPropertyName("current_shanten")]
    public int? Shanten { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public sealed class DiscardSuggestion
{
    [JsonPropertyName("discard_tile")]
    public string Tile { get; set; } = "";

    [JsonPropertyName("shanten_after_discard")]
    public int? Shanten { get; set; }

    [JsonPropertyName("ukeire_count")]
    public int? Ukeire { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    [JsonPropertyName("reasoning")]
    public string? Reasoning { get; set; }
}

public sealed class EvaluateCallResponse
{
    [JsonPropertyName("call_type")]
    public string? CallType { get; set; }

    [JsonPropertyName("should_call")]
    public bool ShouldCall { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    [JsonPropertyName("reasoning")]
    public string? Reasoning { get; set; }

    [JsonPropertyName("shanten_before")]
    public int? ShantenBefore { get; set; }

    [JsonPropertyName("shanten_after")]
    public int? ShantenAfter { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public sealed class ValidateMoveResponse
{
    [JsonPropertyName("valid")]
    public bool Valid { get; set; }

    [JsonPropertyName("errors")]
    public List<string>? Errors { get; set; }
}

public sealed class HealthResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}
