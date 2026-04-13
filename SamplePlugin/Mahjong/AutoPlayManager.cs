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

    // Suggestion providers
    private readonly InGameSuggestionProvider _inGameProvider = new();
    private readonly ServerSuggestionProvider _serverProvider = new();
    private ISuggestionProvider _activeProvider;

    // State tracking
    private readonly Random _rng = new();
    private DateTime _actionScheduledAtUtc;
    private DateTime _actionExecuteAtUtc;
    private string? _pendingAction; // "discard:TILE" or "call:accept" or "call:pass"
    private string _lastActionSignature = "";
    private bool _paused;
    private int _consecutiveFailedDiscards; // discards that fired with no state change

    // Game state
    private string? _lastGamePhase;
    private IReadOnlyList<EmjUiReader.UiSlot>? _lastHandSlots;
    private Dictionary<EmjUiReader.CallOptions, nint>? _lastCallButtonNodes;
    private DateTime _callPhaseEnteredUtc; // when we first entered a call/decision phase
    private List<string>? _preChiSuggestionTiles; // tiles from suggestion before entering chi choice
    private IconIdCapture? _iconCapture;

    private static readonly string LogDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MahjongHelper");

    public bool IsPaused => _paused;
    public string? PendingAction => _pendingAction;
    public DateTime ActionExecuteAtUtc => _actionExecuteAtUtc;
    public string ActiveProviderName => _activeProvider.GetType().Name.Replace("SuggestionProvider", "");

    public AutoPlayManager(Configuration config, MahjongIconMap iconMap)
    {
        _config = config;
        _iconMap = iconMap;
        _activeProvider = _inGameProvider; // default to in-game suggestions
    }

    /// <summary>Toggle pause state for the current turn.</summary>
    public void TogglePause() => _paused = !_paused;

    /// <summary>Resume auto-play.</summary>
    public void Resume() => _paused = false;

    /// <summary>Switch to in-game suggestion provider.</summary>
    public void UseInGameProvider() { _activeProvider = _inGameProvider; Log("Switched to InGame suggestion provider"); }

    /// <summary>Switch to server suggestion provider.</summary>
    public void UseServerProvider() { _activeProvider = _serverProvider; Log("Switched to Server suggestion provider"); }

    /// <summary>
    /// Called when a new suggestion is received from the server.
    /// Updates the server provider and re-checks scheduling.
    /// </summary>
    public void OnSuggestionReceived(SuggestMoveResponse? suggestion, string? gamePhase,
        IReadOnlyList<EmjUiReader.UiSlot>? handSlots)
    {
        _serverProvider.UpdateSuggestion(suggestion);
        _lastGamePhase = gamePhase;
        _lastHandSlots = handSlots;
        TryScheduleDiscard();
    }

    /// <summary>
    /// Called when a call evaluation is received from the server.
    /// Updates the server provider and re-checks scheduling.
    /// </summary>
    public void OnCallEvalReceived(EvaluateCallResponse? callEval, string? gamePhase)
    {
        _serverProvider.UpdateCallEval(callEval);
        _lastGamePhase = gamePhase;
        TryScheduleCallResponse();
    }

    /// <summary>
    /// Called on every game state update (every frame the addon is drawn).
    /// Re-checks scheduling in case the phase changed after a suggestion was already cached.
    /// </summary>
    public void OnGameStateUpdate(string? gamePhase, IReadOnlyList<EmjUiReader.UiSlot>? handSlots,
        Dictionary<EmjUiReader.CallOptions, nint>? callButtonNodes = null,
        EmjUiReader.InGameSuggestion? inGameSuggestion = null,
        IconIdCapture? iconCapture = null)
    {
        var prevPhase = _lastGamePhase;
        _lastGamePhase = gamePhase;
        _lastHandSlots = handSlots;
        _lastCallButtonNodes = callButtonNodes;
        _iconCapture = iconCapture;

        // Update the in-game provider with latest suggestion data
        _inGameProvider.Update(inGameSuggestion);

        // Log riichi suggestion events for diagnostics
        if (inGameSuggestion?.Type == EmjUiReader.SuggestionType.Riichi)
        {
            var riichiTile = inGameSuggestion.TileName ?? "(none)";
            var riichiIcon = inGameSuggestion.TileIconId?.ToString() ?? "(none)";
            Log($"[RIICHI-DIAG] Riichi suggestion detected: tile={riichiTile} icon={riichiIcon} phase={gamePhase} prev={prevPhase}");
        }

        // Log phase transitions for diagnostics
        if (gamePhase != prevPhase)
        {
            Log($"Phase transition: {prevPhase} -> {gamePhase}");
            _consecutiveFailedDiscards = 0; // Reset stuck counter on any phase change
            _scoreAdvanceAttempts = 0; // Reset score screen advance attempts
            _chiChoiceAttempts = 0; // Reset chi choice attempts

            // Clear pending actions from the old phase so stale discards/calls
            // don't execute in the wrong phase (e.g. discard firing during riichi prompt).
            if (_pendingAction != null)
            {
                Log($"Clearing stale pending action '{_pendingAction}' due to phase change");
                _pendingAction = null;
                _lastActionSignature = "";
            }
        }

        // Track when we enter a call/decision phase
        var isCallPhase = gamePhase == "CallDecisionPrompt" || gamePhase == "RonDecisionPrompt" ||
                          gamePhase == "TsumoDecisionPrompt" || gamePhase == "RiichiDecisionPrompt";
        var wasCallPhase = prevPhase == "CallDecisionPrompt" || prevPhase == "RonDecisionPrompt" ||
                           prevPhase == "TsumoDecisionPrompt" || prevPhase == "RiichiDecisionPrompt";

        // Log riichi phase transitions specifically
        if (gamePhase == "RiichiDecisionPrompt" && prevPhase != "RiichiDecisionPrompt")
            Log($"[RIICHI-DIAG] Entered RiichiDecisionPrompt from {prevPhase}");
        if (prevPhase == "RiichiDecisionPrompt" && gamePhase != "RiichiDecisionPrompt")
            Log($"[RIICHI-DIAG] Left RiichiDecisionPrompt to {gamePhase}");

        if (isCallPhase && !wasCallPhase)
        {
            _callPhaseEnteredUtc = DateTime.UtcNow;

            // Remember chi suggestion tiles for use in the choice sub-menu
            if (inGameSuggestion?.Type == EmjUiReader.SuggestionType.Chi &&
                inGameSuggestion.TileName != null)
            {
                _preChiSuggestionTiles = new List<string> { inGameSuggestion.TileName };
                Log($"[CHI-CHOICE] Remembered pre-chi tile: {inGameSuggestion.TileName}");
            }
        }
        else if (!isCallPhase && wasCallPhase)
        {
            // Left a call phase — clear stale call eval so future prompts
            // can trigger the fallback if the server doesn't respond.
            _serverProvider.ClearCallEval();
        }

        // Fallback: if we've been in a call phase for >3 seconds with no pending action
        // and the active provider still can't decide, schedule a pass.
        if (isCallPhase && _pendingAction == null &&
            _activeProvider.GetCallAction() == null &&
            _config.AutoPlayEnabled && _config.AutoCallEnabled && !_paused &&
            DateTime.UtcNow > _callPhaseEnteredUtc.AddSeconds(3))
        {
            Log($"Fallback: auto-pass after 3s in {gamePhase} with no provider decision");
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

        // Auto-advance score screen when between rounds.
        if (gamePhase == "BetweenRounds" && _config.AutoPlayEnabled && !_paused)
        {
            TryScheduleScoreAdvance();
        }

        // Handle chi/pon choice sub-menu (atk0=25).
        if (gamePhase == "CallChoicePrompt" && _config.AutoPlayEnabled && _config.AutoCallEnabled && !_paused)
        {
            TryScheduleCallChoice();
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

        // Phase safety: verify the pending action matches the current phase.
        // This prevents a stale discard from firing during a call prompt (or vice versa).
        var phase = _lastGamePhase ?? "";
        if (_pendingAction.StartsWith("discard:") && phase != "WaitingForDiscard")
        {
            Log($"Dropping stale discard action '{_pendingAction}' — current phase is {phase}");
            _pendingAction = null;
            _lastActionSignature = "";
            return false;
        }
        if (_pendingAction.StartsWith("call:") && phase != "CallDecisionPrompt" &&
            phase != "RonDecisionPrompt" && phase != "TsumoDecisionPrompt" &&
            phase != "RiichiDecisionPrompt")
        {
            Log($"Dropping stale call action '{_pendingAction}' — current phase is {phase}");
            _pendingAction = null;
            _lastActionSignature = "";
            return false;
        }
        if (_pendingAction == "chi-choice" && phase != "CallChoicePrompt")
        {
            Log($"Dropping stale chi-choice action — current phase is {phase}");
            _pendingAction = null;
            _lastActionSignature = "";
            return false;
        }

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
        else if (action == "advance")
        {
            _consecutiveFailedDiscards = 0;
            return AddonClickHelper.TryAdvanceScoreScreen(addon);
        }
        else if (action == "chi-choice")
        {
            _consecutiveFailedDiscards = 0;
            return AddonClickHelper.TrySelectCallChoice(addon, _preChiSuggestionTiles, _iconMap, _iconCapture);
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

        var bestTile = _activeProvider.GetDiscardTile();
        if (string.IsNullOrEmpty(bestTile))
            return;

        var sig = $"discard:{bestTile}";
        if (sig == _lastActionSignature)
            return;

        _lastActionSignature = sig;
        _pendingAction = sig;
        _actionScheduledAtUtc = DateTime.UtcNow;
        var delayMs = _rng.Next(_config.AutoDiscardDelayMinMs, _config.AutoDiscardDelayMaxMs + 1);
        _actionExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(delayMs);

        Log($"Scheduled: {sig} provider={_activeProvider.GetType().Name} (execute at +{delayMs}ms)");
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

        // Don't overwrite an already-scheduled call action — honor the first
        // decision the provider gave us for this call phase. The in-game suggestion
        // text can flip between frames (e.g. Pass → Chi), which would silently
        // replace a scheduled pass with an accept.
        if (_pendingAction != null && _pendingAction.StartsWith("call:"))
        {
            var wouldBe = _activeProvider.GetCallAction();
            if (wouldBe != null && $"call:{wouldBe}" != _pendingAction)
                Log($"[CALL-GUARD] Blocked overwrite: keeping '{_pendingAction}', provider now says '{wouldBe}'");
            return;
        }

        var decision = _activeProvider.GetCallAction();
        if (decision == null)
            return;

        // Log riichi accept/pass decisions
        if (_lastGamePhase == "RiichiDecisionPrompt")
            Log($"[RIICHI-DIAG] Provider decision for Riichi: {decision} provider={_activeProvider.GetType().Name}");

        var action = $"call:{decision}";

        if (action == _lastActionSignature)
            return;

        _lastActionSignature = action;
        _pendingAction = action;
        _actionScheduledAtUtc = DateTime.UtcNow;
        var callDelayMs = _rng.Next(_config.AutoCallDelayMinMs, _config.AutoCallDelayMaxMs + 1);
        _actionExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(callDelayMs);

        Log($"Scheduled: {action} provider={_activeProvider.GetType().Name} (execute at +{callDelayMs}ms)");
    }

    private int _scoreAdvanceAttempts; // track retry count for score screen

    private void TryScheduleScoreAdvance()
    {
        if (_pendingAction != null)
            return;

        // Stop after 5 attempts — may not work for this screen
        if (_scoreAdvanceAttempts >= 5)
            return;

        var sig = "advance";
        if (sig == _lastActionSignature)
            return;

        _lastActionSignature = sig;
        _pendingAction = sig;
        _actionScheduledAtUtc = DateTime.UtcNow;
        // Use a longer delay for score screen to let animations finish
        _actionExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(3000);
        _scoreAdvanceAttempts++;

        Log($"Scheduled: {sig} attempt={_scoreAdvanceAttempts} (execute at +3000ms)");
    }

    private int _chiChoiceAttempts;

    private void TryScheduleCallChoice()
    {
        if (_pendingAction != null)
            return;

        if (_chiChoiceAttempts >= 3)
            return;

        var sig = "chi-choice";
        if (sig == _lastActionSignature)
            return;

        _lastActionSignature = sig;
        _pendingAction = sig;
        _actionScheduledAtUtc = DateTime.UtcNow;
        var delayMs = _rng.Next(_config.AutoCallDelayMinMs, _config.AutoCallDelayMaxMs + 1);
        _actionExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(delayMs);
        _chiChoiceAttempts++;

        Log($"Scheduled: {sig} attempt={_chiChoiceAttempts} preferred=[{string.Join(",", _preChiSuggestionTiles ?? new List<string>())}] (execute at +{delayMs}ms)");
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
            var handTiles = string.Join(",", handSlots.Select(s => $"{s.TileCode}(icon={s.IconId} idx={s.SlotIndex})"));
            var drawnTileInfo = drawnSlot != null ? $"{drawnSlot.TileCode}(icon={drawnSlot.IconId})" : "none";
            var totalSlots = _lastHandSlots.Count;
            var canonCount = handSlots.Count;
            Log($"Cannot discard {tileCode}: no matching hand slot found in canonical hand ({canonCount} canonical, {totalSlots} total). hand=[{handTiles}] draw={drawnTileInfo}");
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
            // Accept call — click the button matching the current game phase.
            // Phase-specific selection prevents stale button nodes from a prior
            // prompt (e.g. Chi lingering after a pon skip) from being clicked
            // when we're in a different phase (e.g. RiichiDecisionPrompt).
            if (_lastCallButtonNodes == null || _lastCallButtonNodes.Count == 0)
            {
                Log($"Cannot accept call: no button nodes captured");
                return false;
            }

            // Build a phase-specific priority list so we click the correct button.
            EmjUiReader.CallOptions[] priority;
            switch (_lastGamePhase)
            {
                case "RiichiDecisionPrompt":
                    priority = new[] { EmjUiReader.CallOptions.Riichi };
                    break;
                case "TsumoDecisionPrompt":
                    priority = new[] { EmjUiReader.CallOptions.Tsumo };
                    break;
                case "RonDecisionPrompt":
                    priority = new[] { EmjUiReader.CallOptions.Ron };
                    break;
                case "CallDecisionPrompt":
                default:
                    // For generic call prompts, prefer the strongest call available.
                    priority = new[]
                    {
                        EmjUiReader.CallOptions.Ron,
                        EmjUiReader.CallOptions.Tsumo,
                        EmjUiReader.CallOptions.Kan,
                        EmjUiReader.CallOptions.Pon,
                        EmjUiReader.CallOptions.Chi,
                        EmjUiReader.CallOptions.Riichi,
                    };
                    break;
            }

            foreach (var call in priority)
            {
                if (_lastCallButtonNodes.TryGetValue(call, out var btnPtr) && btnPtr != 0)
                {
                    Log($"Executing call accept: clicking {call} button (ptr={btnPtr:X}) phase={_lastGamePhase} via ListItemClick");
                    return AddonClickHelper.TryAcceptCallViaListClick(addon, btnPtr, call.ToString());
                }
            }

            Log($"Cannot accept call: no matching button for phase={_lastGamePhase} in captured nodes [{string.Join(",", _lastCallButtonNodes.Keys)}]");
            return false;
        }
        else
        {
            // Skip/pass — click the Skip button node via ListItemClick first.
            // Callback 8 only works when rawAtk0=6, but the call prompt can appear
            // while rawAtk0 is still 15 (opponent turn) or 9 (transition).
            // Clicking the actual Skip button dismisses the UI immediately.
            int rawAtk0 = -1;
            try
            {
                if (addon->AtkValues != null && addon->AtkValuesCount > 0)
                    rawAtk0 = addon->AtkValues[0].Int;
            }
            catch { }

            // Try clicking the Skip button node if we have it
            if (_lastCallButtonNodes != null &&
                _lastCallButtonNodes.TryGetValue(EmjUiReader.CallOptions.Skip, out var skipPtr) && skipPtr != 0)
            {
                Log($"Executing call skip: clicking Skip button (ptr={skipPtr:X} rawAtk0={rawAtk0}) via ListItemClick");
                return AddonClickHelper.TryAcceptCallViaListClick(addon, skipPtr, "Skip");
            }

            // Fallback to callback 8
            Log($"Executing call response: skip/pass via callback 8 (rawAtk0={rawAtk0}) — no Skip button node");
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
