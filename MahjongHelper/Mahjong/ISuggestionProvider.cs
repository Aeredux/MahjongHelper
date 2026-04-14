namespace MahjongHelper.Mahjong;

/// <summary>
/// Provides tile discard and call decision suggestions for auto-play.
/// Implementations can source suggestions from different backends
/// (in-game AI, remote server, etc.).
/// </summary>
public interface ISuggestionProvider
{
    /// <summary>
    /// Returns the tile code to discard (e.g. "P9", "M3"), or null if
    /// this provider cannot determine a discard right now.
    /// </summary>
    string? GetDiscardTile();

    /// <summary>
    /// Returns the call action: "accept" or "pass", or null if this
    /// provider cannot determine a call decision right now.
    /// </summary>
    string? GetCallAction();
}

/// <summary>
/// Uses the game's own in-game suggestion system (AtkValues[6] + node 45 icon)
/// to determine discards and call decisions.
/// </summary>
public sealed class InGameSuggestionProvider : ISuggestionProvider
{
    private EmjUiReader.InGameSuggestion? _suggestion;

    public void Update(EmjUiReader.InGameSuggestion? suggestion)
    {
        _suggestion = suggestion;
    }

    public string? GetDiscardTile()
    {
        if (_suggestion?.Type == EmjUiReader.SuggestionType.Discard &&
            !string.IsNullOrEmpty(_suggestion.TileName))
            return _suggestion.TileName;
        return null;
    }

    public string? GetCallAction()
    {
        return _suggestion?.Type switch
        {
            EmjUiReader.SuggestionType.Pass => "pass",
            EmjUiReader.SuggestionType.Chi => "accept",
            EmjUiReader.SuggestionType.Pon => "accept",
            EmjUiReader.SuggestionType.Kan => "accept",
            EmjUiReader.SuggestionType.Ron => "accept",
            EmjUiReader.SuggestionType.Tsumo => "accept",
            EmjUiReader.SuggestionType.Riichi => "accept",
            _ => null,
        };
    }
}

/// <summary>
/// Uses the remote mahjong server (localhost:8080) to determine discards
/// and call decisions.
/// </summary>
public sealed class ServerSuggestionProvider : ISuggestionProvider
{
    private SuggestMoveResponse? _lastSuggestion;
    private EvaluateCallResponse? _lastCallEval;

    public void UpdateSuggestion(SuggestMoveResponse? suggestion)
    {
        _lastSuggestion = suggestion;
    }

    public void UpdateCallEval(EvaluateCallResponse? callEval)
    {
        _lastCallEval = callEval;
    }

    public void ClearCallEval()
    {
        _lastCallEval = null;
    }

    public string? GetDiscardTile()
    {
        if (_lastSuggestion?.Suggestions == null || _lastSuggestion.Suggestions.Count == 0)
            return null;
        if (!string.IsNullOrEmpty(_lastSuggestion.Error))
            return null;
        var tile = _lastSuggestion.Suggestions[0].Tile;
        return string.IsNullOrEmpty(tile) ? null : tile;
    }

    public string? GetCallAction()
    {
        if (_lastCallEval == null)
            return null;
        return _lastCallEval.ShouldCall ? "accept" : "pass";
    }
}
