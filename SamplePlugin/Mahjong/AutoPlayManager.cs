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
    private int _consecutiveFailedDiscards; // discards that fired with no state change

    // The suggestion to act on
    private SuggestMoveResponse? _lastSuggestion;
    private EvaluateCallResponse? _lastCallEval;
    private string? _lastGamePhase;
    private IReadOnlyList<EmjUiReader.UiSlot>? _lastHandSlots;
    private Dictionary<EmjUiReader.CallOptions, nint>? _lastCallButtonNodes;
    private DateTime _callPhaseEnteredUtc; // when we first entered a call/decision phase

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
    public void OnGameStateUpdate(string? gamePhase, IReadOnlyList<EmjUiReader.UiSlot>? handSlots,
        Dictionary<EmjUiReader.CallOptions, nint>? callButtonNodes = null)
    {
        var prevPhase = _lastGamePhase;
        _lastGamePhase = gamePhase;
        _lastHandSlots = handSlots;
        _lastCallButtonNodes = callButtonNodes;

        // Log phase transitions for diagnostics
        if (gamePhase != prevPhase)
        {
            Log($"Phase transition: {prevPhase} -> {gamePhase}");
            _consecutiveFailedDiscards = 0; // Reset stuck counter on any phase change
        }

        // Track when we enter a call/decision phase
        var isCallPhase = gamePhase == "CallDecisionPrompt" || gamePhase == "RonDecisionPrompt" ||
                          gamePhase == "TsumoDecisionPrompt" || gamePhase == "RiichiDecisionPrompt";
        var wasCallPhase = prevPhase == "CallDecisionPrompt" || prevPhase == "RonDecisionPrompt" ||
                           prevPhase == "TsumoDecisionPrompt" || prevPhase == "RiichiDecisionPrompt";

        if (isCallPhase && !wasCallPhase)
        {
            _callPhaseEnteredUtc = DateTime.UtcNow;
        }
        else if (!isCallPhase && wasCallPhase)
        {
            // Left a call phase — clear stale call eval so future prompts
            // can trigger the fallback if the server doesn't respond.
            _lastCallEval = null;
        }

        // Fallback: if we've been in a call phase for >3 seconds with no pending action
        // and no call eval received, schedule a pass. This handles cases where the server
        // didn't respond or the call eval request was deduped.
        if (isCallPhase && _pendingAction == null && _lastCallEval == null &&
            _config.AutoPlayEnabled && _config.AutoCallEnabled && !_paused &&
            DateTime.UtcNow > _callPhaseEnteredUtc.AddSeconds(3))
        {
            Log($"Fallback: auto-pass after 3s in {gamePhase} with no call evaluation");
            _pendingAction = "call:pass";
            _lastActionSignature = "call:pass";
            _actionScheduledAtUtc = DateTime.UtcNow;
            _actionExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(500);
        }

        // Always try scheduling a discard when in WaitingForDiscard.
        // TryScheduleDiscard has its own signature dedup so it's safe to call every frame.
        // This is needed because after Update() clears _lastActionSignature,
        // we must re-attempt scheduling even if the phase didn't transition.
        if (gamePhase == "WaitingForDiscard")
        {
            TryScheduleDiscard();
        }

        // Similarly, always try scheduling a call response when in a call phase.
        // After a skip executes and clears the signature, we need to re-schedule
        // if the call prompt persists.
        if (isCallPhase)
        {
            TryScheduleCallResponse();
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

        // If discards keep failing (5+ attempts with no state change), stop retrying.
        // This prevents infinite loops when some unknown state is blocking callbacks.
        // IMPORTANT: Do NOT fire callback 8 here — at AtkVal[0]=30, callback 8 means
        // tsumogiri (discard drawn tile), not skip/pass.
        if (_consecutiveFailedDiscards >= 5 && _pendingAction != null && _pendingAction.StartsWith("discard:"))
        {
            Log($"Stuck-discard detected ({_consecutiveFailedDiscards} fails), pausing auto-discard until next phase change");
            _pendingAction = null;
            _lastActionSignature = "";
            // Don't reset counter — it resets on phase change via ClearPending
            return false;
        }

        var action = _pendingAction;

        _pendingAction = null;
        // Clear the signature so the same action can be re-scheduled if the
        // game state hasn't changed (e.g., a skip/pass didn't take effect).
        _lastActionSignature = "";

        if (action.StartsWith("discard:"))
        {
            // Track whether the discard actually changes the game state
            var preAtk0 = addon->AtkValues != null && addon->AtkValuesCount > 0
                ? addon->AtkValues[0].Int : -1;

            var tileCode = action.Substring(8);
            var result = ExecuteDiscard(addon, tileCode);

            // Check if AtkVal[0] changed (≈ immediate for successful callbacks)
            var postAtk0 = addon->AtkValues != null && addon->AtkValuesCount > 0
                ? addon->AtkValues[0].Int : -1;

            if (result && preAtk0 == postAtk0)
            {
                _consecutiveFailedDiscards++;
                Log($"Discard may have failed (AtkVal[0] unchanged at {preAtk0}), consecutive={_consecutiveFailedDiscards}");
            }
            else
            {
                _consecutiveFailedDiscards = 0;
            }

            return result;
        }
        else if (action == "call:accept")
        {
            _consecutiveFailedDiscards = 0;
            return ExecuteCallResponse(addon, 0);
        }
        else if (action == "call:pass")
        {
            _consecutiveFailedDiscards = 0;
            return ExecuteCallResponse(addon, 1);
        }

        return false;
    }

    /// <summary>Clears pending action (e.g., when game state changes).</summary>
    public void ClearPending()
    {
        _pendingAction = null;
        _lastActionSignature = "";
        _consecutiveFailedDiscards = 0;
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
            // Accept call — click the best available call button
            if (_lastCallButtonNodes == null || _lastCallButtonNodes.Count == 0)
            {
                Log($"Cannot accept call: no button nodes captured");
                return false;
            }

            // Pick the highest-priority call button available
            // Priority: Ron > Tsumo > Kan > Pon > Chi > Riichi
            var priority = new[]
            {
                EmjUiReader.CallOptions.Ron,
                EmjUiReader.CallOptions.Tsumo,
                EmjUiReader.CallOptions.Kan,
                EmjUiReader.CallOptions.Pon,
                EmjUiReader.CallOptions.Chi,
                EmjUiReader.CallOptions.Riichi,
            };

            foreach (var call in priority)
            {
                if (_lastCallButtonNodes.TryGetValue(call, out var btnPtr) && btnPtr != 0)
                {
                    Log($"Executing call accept: clicking {call} button (ptr={btnPtr:X}) via ListItemClick");
                    return AddonClickHelper.TryAcceptCallViaListClick(addon, btnPtr, call.ToString());
                }
            }

            Log($"Cannot accept call: no matching button node found in captured nodes");
            return false;
        }
        else
        {
            // Skip/pass — use callback 8, but ONLY when AtkVal[0]=6 (actual call prompt).
            // Callback 8 at other AtkVal states (e.g., 15, 30) means tsumogiri (discard drawn tile).
            int rawAtk0 = -1;
            try
            {
                if (addon->AtkValues != null && addon->AtkValuesCount > 0)
                    rawAtk0 = addon->AtkValues[0].Int;
            }
            catch { }

            if (rawAtk0 != 6)
            {
                Log($"Skip/pass deferred: rawAtk0={rawAtk0} (need 6). Will retry next frame.");
                return false;
            }

            Log($"Executing call response: skip/pass via callback 8 (rawAtk0={rawAtk0})");
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
