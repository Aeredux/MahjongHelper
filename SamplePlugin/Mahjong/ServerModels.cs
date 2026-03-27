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

    [JsonPropertyName("discards")]
    public DiscardPools? Discards { get; set; }

    [JsonPropertyName("dora_indicators")]
    public List<string>? DoraIndicators { get; set; }

    [JsonPropertyName("seat_wind")]
    public string? SeatWind { get; set; }

    [JsonPropertyName("round_wind")]
    public string? RoundWind { get; set; }

    [JsonPropertyName("round_number")]
    public int? RoundNumber { get; set; }
}

public sealed class EvaluateCallRequest
{
    [JsonPropertyName("hand")]
    public List<string> Hand { get; set; } = [];

    [JsonPropertyName("call_tile")]
    public string? CallTile { get; set; }

    [JsonPropertyName("call_type")]
    public string? CallType { get; set; }

    [JsonPropertyName("discards")]
    public DiscardPools? Discards { get; set; }

    [JsonPropertyName("dora_indicators")]
    public List<string>? DoraIndicators { get; set; }

    [JsonPropertyName("seat_wind")]
    public string? SeatWind { get; set; }

    [JsonPropertyName("round_wind")]
    public string? RoundWind { get; set; }
}

public sealed class ValidateMoveRequest
{
    [JsonPropertyName("hand")]
    public List<string> Hand { get; set; } = [];

    [JsonPropertyName("discard")]
    public string? Discard { get; set; }
}

public sealed class DiscardPools
{
    [JsonPropertyName("player")]
    public List<string> Player { get; set; } = [];

    [JsonPropertyName("right")]
    public List<string> Right { get; set; } = [];

    [JsonPropertyName("opposite")]
    public List<string> Opposite { get; set; } = [];

    [JsonPropertyName("left")]
    public List<string> Left { get; set; } = [];
}

// ─── Response Models ───

public sealed class SuggestMoveResponse
{
    [JsonPropertyName("suggestions")]
    public List<DiscardSuggestion> Suggestions { get; set; } = [];

    [JsonPropertyName("currentShanten")]
    public int? Shanten { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public sealed class DiscardSuggestion
{
    [JsonPropertyName("discardTile")]
    public string Tile { get; set; } = "";

    [JsonPropertyName("shantenAfterDiscard")]
    public int? Shanten { get; set; }

    [JsonPropertyName("ukeireCount")]
    public int? Ukeire { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    [JsonPropertyName("reasoning")]
    public string? Reasoning { get; set; }
}

public sealed class EvaluateCallResponse
{
    [JsonPropertyName("should_call")]
    public bool ShouldCall { get; set; }

    [JsonPropertyName("reasoning")]
    public string? Reasoning { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public sealed class ValidateMoveResponse
{
    [JsonPropertyName("valid")]
    public bool Valid { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public sealed class HealthResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}
