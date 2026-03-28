using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace SamplePlugin.Mahjong;

/// <summary>
/// Manages automatic tile discards and call decisions.
/// Fed suggestion data from Plugin.cs and executes clicks on the EmjL addon
/// after a configurable delay.
/// </summary>
public sealed class AutoPlayManager
{
    private readonly Configuration _config;
    private readonly MahjongIconMap _iconMap;

    // State tracking
    private DateTime _actionScheduledAtUtc;
    private DateTime _actionExecuteAtUtc;
    private string? _pendingAction; // "discard:TILE" or "call:accept" or "call:pass"
    private string _lastActionSignature = "";
    private bool _paused;

    // The suggestion to act on
    private SuggestMoveResponse? _lastSuggestion;
    private EvaluateCallResponse? _lastCallEval;
    private string? _lastGamePhase;
    private IReadOnlyList<EmjUiReader.UiSlot>? _lastHandSlots;

    private static readonly string LogDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MahjongHelper");

    public bool IsPaused => _paused;
    public string? PendingAction => _pendingAction;
    public DateTime ActionExecuteAtUtc => _actionExecuteAtUtc;

    public AutoPlayManager(Configuration config, MahjongIconMap iconMap)
    {
        _config = config;
        _iconMap = iconMap;
    }

    /// <summary>Toggle pause state for the current turn.</summary>
    public void TogglePause() => _paused = !_paused;

    /// <summary>Resume auto-play.</summary>
    public void Resume() => _paused = false;

    /// <summary>
    /// Called when a new suggestion is received from the server.
    /// Schedules an auto-discard if conditions are met.
    /// </summary>
    public void OnSuggestionReceived(SuggestMoveResponse? suggestion, string? gamePhase,
        IReadOnlyList<EmjUiReader.UiSlot>? handSlots)
    {
        _lastSuggestion = suggestion;
        _lastGamePhase = gamePhase;
        _lastHandSlots = handSlots;
        TryScheduleDiscard();
    }

    /// <summary>
    /// Called when a call evaluation is received from the server.
    /// Schedules an auto-call response if conditions are met.
    /// </summary>
    public void OnCallEvalReceived(EvaluateCallResponse? callEval, string? gamePhase)
    {
        _lastCallEval = callEval;
        _lastGamePhase = gamePhase;
        TryScheduleCallResponse();
    }

    /// <summary>
    /// Called on every game state update (every frame the addon is drawn).
    /// Re-checks scheduling in case the phase changed after a suggestion was already cached.
    /// </summary>
    public void OnGameStateUpdate(string? gamePhase, IReadOnlyList<EmjUiReader.UiSlot>? handSlots)
    {
        var prevPhase = _lastGamePhase;
        _lastGamePhase = gamePhase;
        _lastHandSlots = handSlots;

        // If phase just changed to WaitingForDiscard and we have a pending suggestion, try scheduling
        if (gamePhase == "WaitingForDiscard" && prevPhase != "WaitingForDiscard")
        {
            TryScheduleDiscard();
        }
    }

    /// <summary>
    /// Called every frame from OnFrameworkUpdate. Executes scheduled actions when the delay expires.
    /// Returns true if an action was executed this frame.
    /// </summary>
    public unsafe bool Update(AtkUnitBase* addon)
    {
        if (!_config.AutoPlayEnabled || _paused || _pendingAction == null || addon == null)
            return false;

        if (DateTime.UtcNow < _actionExecuteAtUtc)
            return false;

        var action = _pendingAction;
        _pendingAction = null;

        if (action.StartsWith("discard:"))
        {
            var tileCode = action.Substring(8);
            return ExecuteDiscard(addon, tileCode);
        }
        else if (action == "call:accept")
        {
            return ExecuteCallResponse(addon, 0);
        }
        else if (action == "call:pass")
        {
            return ExecuteCallResponse(addon, 1);
        }

        return false;
    }

    /// <summary>Clears pending action (e.g., when game state changes).</summary>
    public void ClearPending()
    {
        _pendingAction = null;
        _lastActionSignature = "";
    }

    private void TryScheduleDiscard()
    {
        if (!_config.AutoPlayEnabled || !_config.AutoDiscardEnabled || _paused)
            return;

        if (_lastGamePhase != "WaitingForDiscard")
            return;

        if (_lastSuggestion?.Suggestions == null || _lastSuggestion.Suggestions.Count == 0)
            return;

        if (!string.IsNullOrEmpty(_lastSuggestion.Error))
            return;

        var bestTile = _lastSuggestion.Suggestions[0].Tile;
        if (string.IsNullOrEmpty(bestTile))
            return;

        var sig = $"discard:{bestTile}";
        if (sig == _lastActionSignature)
            return;

        _lastActionSignature = sig;
        _pendingAction = sig;
        _actionScheduledAtUtc = DateTime.UtcNow;
        _actionExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(_config.AutoPlayDelayMs);

        Log($"Scheduled: {sig} (execute at +{_config.AutoPlayDelayMs}ms)");
    }

    private void TryScheduleCallResponse()
    {
        if (!_config.AutoPlayEnabled || !_config.AutoCallEnabled || _paused)
            return;

        if (_lastGamePhase != "CallDecisionPrompt" &&
            _lastGamePhase != "RonDecisionPrompt" &&
            _lastGamePhase != "TsumoDecisionPrompt" &&
            _lastGamePhase != "RiichiDecisionPrompt")
            return;

        if (_lastCallEval == null)
            return;

        var action = _lastCallEval.ShouldCall ? "call:accept" : "call:pass";

        if (action == _lastActionSignature)
            return;

        _lastActionSignature = action;
        _pendingAction = action;
        _actionScheduledAtUtc = DateTime.UtcNow;
        _actionExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(_config.AutoPlayDelayMs);

        Log($"Scheduled: {action} shouldCall={_lastCallEval.ShouldCall} conf={_lastCallEval.Confidence} (execute at +{_config.AutoPlayDelayMs}ms)");
    }

    private unsafe bool ExecuteDiscard(AtkUnitBase* addon, string tileCode)
    {
        if (_lastHandSlots == null || _lastHandSlots.Count == 0)
        {
            Log($"Cannot discard {tileCode}: no hand slots available");
            return false;
        }

        // Get the canonical hand (sorted left-to-right, excluding drawn tile)
        var handSlots = _lastHandSlots
            .Where(s => s.Kind == EmjUiReader.SlotKind.CanonicalPlayerHand)
            .Where(s => s.Visible && s.TileCode != null)
            .OrderBy(s => s.SlotIndex)
            .ToList();

        // Check if the drawn tile matches the suggested discard (tsumogiri)
        var drawnSlot = _lastHandSlots
            .Where(s => s.Kind == EmjUiReader.SlotKind.CanonicalPlayerDraw)
            .Where(s => s.Visible && s.TileCode != null)
            .FirstOrDefault();

        if (drawnSlot != null && TileCodesMatch(drawnSlot.TileCode!, tileCode))
        {
            Log($"Executing tsumogiri: {tileCode} (drawn tile matches suggestion)");
            return AddonClickHelper.TryDiscardDrawnTile(addon);
        }

        // Find the hand position (0 = leftmost) of the tile to discard
        // Server returns M5/P5/S5 for red doras, but hand uses M0/P0/S0 — match both
        var matchingSlot = handSlots
            .FirstOrDefault(s => TileCodesMatch(s.TileCode!, tileCode));

        if (matchingSlot == null)
        {
            Log($"Cannot discard {tileCode}: no matching hand slot found in canonical hand");
            return false;
        }

        var handPos = matchingSlot.SlotIndex;
        Log($"Executing discard: {tileCode} handPos={handPos} (slot {matchingSlot.SlotIndex})");
        return AddonClickHelper.TryDiscardTile(addon, handPos);
    }

    /// <summary>
    /// Compares tile codes accounting for red dora normalization.
    /// M0/P0/S0 (red 5s in UI) match M5/P5/S5 (server format).
    /// </summary>
    private static bool TileCodesMatch(string uiCode, string serverCode)
    {
        if (uiCode.Equals(serverCode, StringComparison.OrdinalIgnoreCase))
            return true;

        // Red dora: UI uses M0/P0/S0, server uses M5/P5/S5
        var normalizedUi = uiCode switch
        {
            "M0" => "M5",
            "P0" => "P5",
            "S0" => "S5",
            _ => uiCode
        };
        return normalizedUi.Equals(serverCode, StringComparison.OrdinalIgnoreCase);
    }

    private unsafe bool ExecuteCallResponse(AtkUnitBase* addon, int callIndex)
    {
        if (callIndex == 0)
        {
            // Accept call — not yet implemented, need to discover callback IDs for Pon/Chi/Kan/Ron
            Log($"[DRY-RUN] Accepting call not yet implemented");
            return false;
        }
        else
        {
            // Skip/pass — use callback 9
            Log($"Executing call response: skip/pass");
            return AddonClickHelper.TrySkipCall(addon);
        }
    }

    private static void Log(string message)
    {
        try
        {
            System.IO.Directory.CreateDirectory(LogDir);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(LogDir, "autoplay.log"),
                $"[{DateTime.UtcNow:O}] [AutoPlay] {message}\n");
        }
        catch { }
    }
}
