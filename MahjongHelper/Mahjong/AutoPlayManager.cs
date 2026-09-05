using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MahjongHelper.Mahjong;

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
    private int _consecutiveCallAttempts; // call accept/skip attempts with no phase change

    // Game state
    private string? _lastGamePhase;
    private IReadOnlyList<EmjUiReader.UiSlot>? _lastHandSlots;
    private Dictionary<EmjUiReader.CallOptions, nint>? _lastCallButtonNodes;
    private DateTime _callPhaseEnteredUtc; // when we first entered a call/decision phase
    private DateTime _discardPhaseEnteredUtc; // when we first entered WaitingForDiscard
    private List<string>? _preChiSuggestionTiles; // tiles from suggestion before entering chi choice
    private IconIdCapture? _iconCapture;
    private bool _lastCallIntentWasAccept; // true if we deliberately accepted a call (vs game timer auto-accepting)
    private DateTime _acceptExecutedAtUtc; // when _lastCallIntentWasAccept was set
    private string? _riichiDiscardTile; // saved from Riichi/Discard suggestion before clicking Riichi
    private int? _riichiDiscardIconId;
    private int? _riichiDiscardSlot; // post-accept ATK [18]/[1] hand position
    private string? _lastSeenDiscardTile;
    private int? _lastSeenDiscardIconId;
    private bool _awaitingRiichiDiscard; // declared Riichi stuck; waiting to discard the saved tile
    private bool _riichiDeclarePending; // Riichi ListItemClick dispatched; waiting to see if it stuck

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
        _config.ClampAutoPlayDelays();
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

        // New discard turn: drop the previous turn's suggested tile so a Riichi
        // prompt with tile=(none) cannot reuse a stale discard.
        if (gamePhase == "WaitingForDiscard" && prevPhase != "WaitingForDiscard" &&
            prevPhase != "RiichiDecisionPrompt")
        {
            _lastSeenDiscardTile = null;
            _lastSeenDiscardIconId = null;
        }

        // Capture the discard suggestion DURING WaitingForDiscard, before the
        // Riichi button is clicked. Riichi prompts often report tile=(none).
        if (gamePhase == "WaitingForDiscard")
        {
            if (inGameSuggestion?.Type == EmjUiReader.SuggestionType.Discard)
            {
                if (!string.IsNullOrEmpty(inGameSuggestion.TileName))
                    _lastSeenDiscardTile = inGameSuggestion.TileName;
                if (inGameSuggestion.TileIconId is > 0)
                    _lastSeenDiscardIconId = inGameSuggestion.TileIconId;
            }

            var providerTile = _activeProvider.GetDiscardTile();
            if (!string.IsNullOrEmpty(providerTile))
                _lastSeenDiscardTile = providerTile;
        }

        CaptureRiichiDeclaredTile(inGameSuggestion, gamePhase, prevPhase);

        // Log phase transitions for diagnostics
        if (gamePhase != prevPhase)
        {
            Log($"Phase transition: {prevPhase} -> {gamePhase}");
            _consecutiveFailedDiscards = 0; // Reset stuck counter on any phase change
            _scoreAdvanceAttempts = 0; // Reset score screen advance attempts
            _chiChoiceAttempts = 0; // Reset chi choice attempts

            // After a Riichi click we must observe the new phase+calls before
            // dropping pending work. A move to OpponentTurn can be a successful
            // declare (discard already landed) rather than a stale action.
            var holdRiichiPending = _riichiDeclarePending && prevPhase == "RiichiDecisionPrompt";
            if (!holdRiichiPending)
                _consecutiveCallAttempts = 0;

            if (_pendingAction != null && !holdRiichiPending)
            {
                Log($"Clearing stale pending action '{_pendingAction}' due to phase change");
                _pendingAction = null;
                _lastActionSignature = "";
            }
            else if (_pendingAction != null && holdRiichiPending)
            {
                Log($"[RIICHI-DIAG] Phase {prevPhase} -> {gamePhase} after Riichi click — not treating pending '{_pendingAction}' as stale yet. callsHaveRiichi={CallsIncludeRiichi()}");
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
        {
            Log($"[RIICHI-DIAG] Left RiichiDecisionPrompt to {gamePhase} declarePending={_riichiDeclarePending} awaitingDiscard={_awaitingRiichiDiscard} callsHaveRiichi={CallsIncludeRiichi()} savedTile={_riichiDiscardTile ?? "(none)"} slot={_riichiDiscardSlot?.ToString() ?? "(none)"}");
            if (!_riichiDeclarePending && !_awaitingRiichiDiscard && gamePhase != "WaitingForDiscard")
            {
                if (_riichiDiscardTile != null)
                    Log($"[RIICHI-DIAG] Clearing unused riichi discard tile '{_riichiDiscardTile}' (left Riichi to {gamePhase} without a pending declare)");
                ClearRiichiDeclareState(keepPendingAction: true);
            }
        }

        if (isCallPhase && !wasCallPhase)
        {
            _callPhaseEnteredUtc = DateTime.UtcNow;
            _lastCallIntentWasAccept = false; // reset on entering a new call phase

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
        // IMPORTANT: Do NOT auto-pass if we already executed an accept in this phase.
        // After clicking accept (e.g. Riichi), AtkValues[6] clears while the phase
        // lingers — the provider returns null, but the accept was already dispatched.
        // Auto-passing here would cancel the accepted call.
        if (isCallPhase && _pendingAction == null &&
            _activeProvider.GetCallAction() == null &&
            !_lastCallIntentWasAccept &&
            !_riichiDeclarePending &&
            _config.AutoPlayEnabled && _config.AutoCallEnabled && !_paused &&
            DateTime.UtcNow > _callPhaseEnteredUtc.AddSeconds(3))
        {
            Log($"Fallback: auto-pass after 3s in {gamePhase} with no provider decision");
            _pendingAction = "call:pass";
            _lastActionSignature = "call:pass";
            _actionScheduledAtUtc = DateTime.UtcNow;
            _actionExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(_config.NextCallDelayMs(_rng));
        }

        // After clicking Riichi: do not discard in the prompt. Retry the Riichi
        // button until the prompt is gone AND calls no longer include Riichi.
        UpdateRiichiDeclareState(gamePhase);

        // After a stuck declare, discard the saved tile only when the prompt is
        // gone, calls no longer include Riichi, and it is a discard turn.
        if (gamePhase == "WaitingForDiscard" && _awaitingRiichiDiscard && !_riichiDeclarePending &&
            !CallsIncludeRiichi() && _pendingAction == null && _config.AutoPlayEnabled && !_paused)
        {
            Log($"[RIICHI-DIAG] Declaration stuck — scheduling saved-tile discard tile={_riichiDiscardTile ?? "(none)"} icon={_riichiDiscardIconId?.ToString() ?? "(none)"} slot={_riichiDiscardSlot?.ToString() ?? "(none)"}");
            TryScheduleRiichiDiscard();
        }

        // Track when we enter WaitingForDiscard
        if (gamePhase == "WaitingForDiscard" && prevPhase != "WaitingForDiscard")
            _discardPhaseEnteredUtc = DateTime.UtcNow;

        // Always try scheduling a discard when in WaitingForDiscard.
        // TryScheduleDiscard has its own signature dedup so it's safe to call every frame.
        // This is needed because after Update() clears _lastActionSignature,
        // we must re-attempt scheduling even if the phase didn't transition.
        if (gamePhase == "WaitingForDiscard" && !_riichiDeclarePending)
        {
            TryScheduleDiscard();
        }

        // Safety net: if WaitingForDiscard with no pending action for >8 seconds,
        // retry the last live hint. Do not FireCallback 8 (skip while atk0=6).
        if (gamePhase == "WaitingForDiscard" && _pendingAction == null &&
            !_riichiDeclarePending && !_awaitingRiichiDiscard &&
            _config.AutoPlayEnabled && _config.AutoDiscardEnabled && !_paused &&
            DateTime.UtcNow > _discardPhaseEnteredUtc.AddSeconds(8))
        {
            if (!string.IsNullOrEmpty(_lastSeenDiscardTile))
            {
                Log($"Fallback: retrying live hint '{_lastSeenDiscardTile}' after 8s with no pending discard — not tsumogiri/callback 8");
                _pendingAction = $"discard:{_lastSeenDiscardTile}";
                _lastActionSignature = "";
                _actionScheduledAtUtc = DateTime.UtcNow;
                _actionExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(_config.NextDiscardDelayMs(_rng));
            }
            else if (_lastActionSignature != "no-hint-8s")
            {
                Log("Fallback: 8s in WaitingForDiscard with no live hint — not firing callback 8/tsumogiri");
                _lastActionSignature = "no-hint-8s";
            }
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
        // Only auto-select if we deliberately accepted the call. If the game's
        // timer auto-accepted (e.g., we tried to skip but the click was ineffective),
        // don't blindly pick a chi combination.
        if (gamePhase == "CallChoicePrompt" && _config.AutoPlayEnabled && _config.AutoCallEnabled && !_paused)
        {
            if (_lastCallIntentWasAccept)
            {
                TryScheduleCallChoice();
            }
            else if (prevPhase != "CallChoicePrompt")
            {
                Log($"[CHI-CHOICE] Suppressed auto chi-choice: our intent was not accept (game timer may have auto-accepted)");
            }
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
        // Exception: never discard while a Riichi click is still settling, and never
        // while the Riichi button is still up (declaration did not stick).
        var phase = _lastGamePhase ?? "";
        if (IsDiscardAction(_pendingAction) && !IsDiscardAllowedInPhase(phase))
        {
            Log($"Dropping stale discard action '{_pendingAction}' — current phase is {phase} declarePending={_riichiDeclarePending} awaitingRiichi={_awaitingRiichiDiscard} callsHaveRiichi={CallsIncludeRiichi()}");
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

        // AZPC crash: FireCallback 7 + ReceiveEvent spray at atk0=15 native-crashed
        // AddonEmj.ReceiveEvent. Only click when ATK is discard-ready (2/6/30).
        if (IsDiscardAction(_pendingAction))
        {
            var atk0 = ReadAtkInt(addon, 0);
            if (!EmjUiReader.IsDiscardReadyAtk(atk0))
            {
                Log($"[DISCARD] Not discard-ready atk0={atk0} (need 2/6/30) — waiting, not clicking a hand tile");
                _actionExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(_config.NextDiscardDelayMs(_rng));
                return false;
            }
        }

        // Live AZPC: 5 failed hint clicks then FireCallback 8 (labeled tsumogiri)
        // is skip while atk0=6, not a discard. Keep retrying the hinted tile
        // via callback 7; never unstick by tsumogiri/skip.
        if (_consecutiveFailedDiscards >= 5 && _pendingAction != null && _pendingAction.StartsWith("discard:"))
        {
            Log($"Stuck-discard detected ({_consecutiveFailedDiscards} fails for '{_pendingAction}') — retrying hinted tile via callback 7, not callback 8/tsumogiri");
            _consecutiveFailedDiscards = 0;
        }

        // If call accept/skip keeps failing (5+ attempts with no phase change), stop.
        if (_consecutiveCallAttempts >= 5 && _pendingAction != null && _pendingAction.StartsWith("call:"))
        {
            Log($"Stuck-call detected ({_consecutiveCallAttempts} attempts for '{_pendingAction}'), pausing until next phase change");
            _pendingAction = null;
            _lastActionSignature = "";
            return false;
        }

        var action = _pendingAction;
        if (action == null)
            return false;

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
            // Pass the live hint icon even when the last-seen name string
            // differs — matching is by icon id and tile code on the closed list.
            int? hintIcon = _lastSeenDiscardIconId is > 0 ? _lastSeenDiscardIconId : null;

            var result = _awaitingRiichiDiscard
                ? ExecuteDeclaredRiichiDiscard(addon)
                : ExecuteHintedDiscard(addon, tileCode, hintIcon, allowUnhintedDrawn: false);

            if (result)
            {
                _awaitingRiichiDiscard = false;
                _riichiDiscardTile = null;
                _riichiDiscardIconId = null;
                _riichiDiscardSlot = null;
            }

            // Check if AtkVal[0] changed (≈ immediate for successful callbacks)
            var postAtk0 = addon->AtkValues != null && addon->AtkValuesCount > 0
                ? addon->AtkValues[0].Int : -1;

            if (!result)
            {
                // Tile not found in hand (stale icon data / reader mismatch)
                _consecutiveFailedDiscards++;
                Log($"Discard tile not found in hand, consecutive={_consecutiveFailedDiscards}");
            }
            else if (preAtk0 == postAtk0)
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
            _consecutiveCallAttempts++;
            _lastCallIntentWasAccept = true;
            _acceptExecutedAtUtc = DateTime.UtcNow;
            Log($"Executing call:accept (attempt {_consecutiveCallAttempts})");
            // Freeze the declared tile BEFORE ListItemClick — node 45 often
            // clears to tile=(none) as soon as Riichi is pressed.
            if (_lastGamePhase == "RiichiDecisionPrompt")
                CaptureRiichiDeclaredTile(null, _lastGamePhase, _lastGamePhase);
            var accepted = ExecuteCallResponse(addon, 0);
            if (accepted && _lastGamePhase == "RiichiDecisionPrompt")
            {
                _riichiDeclarePending = true;
                _awaitingRiichiDiscard = false;
                _riichiDiscardSlot = ReadPostRiichiDiscardSlot(addon);
                var postCalls = EmjUiReader.ScanAvailableCalls(addon, out _);
                var callsHaveRiichi = postCalls.HasFlag(EmjUiReader.CallOptions.Riichi);
                Log($"[RIICHI-DIAG] Riichi ListItemClick done — skipping same-tick FireCallback 7. tile={_riichiDiscardTile ?? "(none)"} icon={_riichiDiscardIconId?.ToString() ?? "(none)"} atkSlot={_riichiDiscardSlot?.ToString() ?? "(none)"} postClickCalls={postCalls} callsHaveRiichi={callsHaveRiichi} atk0={ReadAtkInt(addon, 0)}");
                if (callsHaveRiichi)
                    Log("[RIICHI-DIAG] Calls still contain Riichi after click — will retry Riichi button, not discard");
                else
                    Log("[RIICHI-DIAG] Calls no longer contain Riichi after click — waiting for prompt to leave before any discard");
            }
            return accepted;
        }
        else if (action == "call:pass")
        {
            _consecutiveFailedDiscards = 0;
            _consecutiveCallAttempts++;
            Log($"Executing call:pass (attempt {_consecutiveCallAttempts})");
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
        else if (action == "riichi-discard")
        {
            _consecutiveFailedDiscards = 0;
            if (_riichiDeclarePending || CallsIncludeRiichi() || _lastGamePhase == "RiichiDecisionPrompt")
            {
                Log($"[RIICHI-DIAG] Refusing riichi-discard — declare not stuck yet phase={_lastGamePhase} declarePending={_riichiDeclarePending} callsHaveRiichi={CallsIncludeRiichi()}");
                _pendingAction = null;
                _lastActionSignature = "";
                return false;
            }
            Log($"[RIICHI-DIAG] Post-declare discard tile={_riichiDiscardTile ?? "(none)"} icon={_riichiDiscardIconId?.ToString() ?? "(none)"} slot={_riichiDiscardSlot?.ToString() ?? "(none)"}");
            var ok = ExecuteDeclaredRiichiDiscard(addon);
            if (ok)
            {
                _awaitingRiichiDiscard = false;
                _riichiDeclarePending = false;
                _riichiDiscardTile = null;
                _riichiDiscardIconId = null;
                _riichiDiscardSlot = null;
            }
            return ok;
        }
        else if (action == "riichi-tsumogiri")
        {
            _consecutiveFailedDiscards = 0;
            Log($"[RIICHI-DIAG] Executing post-declare discard fallback awaitingRiichi={_awaitingRiichiDiscard} declarePending={_riichiDeclarePending} phase={_lastGamePhase} callsHaveRiichi={CallsIncludeRiichi()}");
            if (_riichiDeclarePending || CallsIncludeRiichi() || _lastGamePhase == "RiichiDecisionPrompt")
            {
                Log("[RIICHI-DIAG] Refusing tsumogiri fallback — Riichi prompt/button still up (failed or unsettled declare)");
                _pendingAction = null;
                _lastActionSignature = "";
                return false;
            }
            if (_awaitingRiichiDiscard)
            {
                var ok = ExecuteDeclaredRiichiDiscard(addon);
                if (ok)
                {
                    _awaitingRiichiDiscard = false;
                    _riichiDiscardTile = null;
                    _riichiDiscardIconId = null;
                    _riichiDiscardSlot = null;
                }
                return ok;
            }

            Log("[RIICHI-DIAG] No awaiting-riichi flag — not assuming we are in riichi, not firing callback 8");
            return false;
        }

        return false;
    }

    /// <summary>Clears pending action (e.g., when game state changes).</summary>
    public void ClearPending()
    {
        _pendingAction = null;
        _lastActionSignature = "";
        _consecutiveFailedDiscards = 0;
        _awaitingRiichiDiscard = false;
        _riichiDeclarePending = false;
        _riichiDiscardSlot = null;
    }

    private void TryScheduleDiscard()
    {
        if (!_config.AutoPlayEnabled || !_config.AutoDiscardEnabled || _paused)
            return;

        if (_pendingAction != null)
            return;

        if (_lastGamePhase != "WaitingForDiscard")
            return;

        var bestTile = _activeProvider.GetDiscardTile();

        // After a stuck Riichi declare, use the tile captured before the Riichi click.
        if (_awaitingRiichiDiscard && _riichiDiscardTile != null)
        {
            if (string.IsNullOrEmpty(bestTile) || bestTile != _riichiDiscardTile)
                Log($"[RIICHI-DIAG] Overriding discard tile: provider={bestTile ?? "(null)"} -> saved riichi tile={_riichiDiscardTile}");
            bestTile = _riichiDiscardTile;
        }

        // Post-declare discard: if no tile is known, fall back to the drawn tile via
        // callback 7 (never callback 8). Only after the Riichi button is gone.
        if (string.IsNullOrEmpty(bestTile) && _awaitingRiichiDiscard && !CallsIncludeRiichi())
        {
            Log($"[RIICHI-DIAG] Post-declare discard with no tile suggestion — scheduling saved/drawn via callback 7 (not callback 8)");
            var tsumSig = "riichi-tsumogiri";
            if (tsumSig == _lastActionSignature)
                return;
            _lastActionSignature = tsumSig;
            _pendingAction = tsumSig;
            _actionScheduledAtUtc = DateTime.UtcNow;
            var tsumDelay = _config.NextDiscardDelayMs(_rng);
            _actionExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(tsumDelay);
            return;
        }

        if (string.IsNullOrEmpty(bestTile))
            return;

        var sig = $"discard:{bestTile}";
        if (sig == _lastActionSignature)
            return;

        _lastActionSignature = sig;
        _pendingAction = sig;
        _actionScheduledAtUtc = DateTime.UtcNow;
        var delayMs = _config.NextDiscardDelayMs(_rng);
        _actionExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(delayMs);

        Log($"Scheduled: {sig} provider={_activeProvider.GetType().Name} (execute at +{delayMs}ms)");
    }

    /// <summary>
    /// Schedules a discard after Riichi has actually stuck (prompt gone, no Riichi
    /// in calls). One FireCallback 7 per tick; never in the same tick as the click.
    /// </summary>
    private void TryScheduleRiichiDiscard()
    {
        if (_pendingAction != null)
            return;

        Log($"[RIICHI-DIAG] Scheduling post-riichi discard tile={_riichiDiscardTile ?? "(none)"} icon={_riichiDiscardIconId?.ToString() ?? "(none)"} slot={_riichiDiscardSlot?.ToString() ?? "(none)"}");
        _lastActionSignature = "riichi-discard";
        _pendingAction = "riichi-discard";
        _actionScheduledAtUtc = DateTime.UtcNow;
        _actionExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(_config.NextDiscardDelayMs(_rng));
    }

    private static bool IsDiscardAction(string? action)
        => action != null && (action.StartsWith("discard:") || action == "riichi-tsumogiri" || action == "riichi-discard");

    private bool IsDiscardAllowedInPhase(string phase)
        => phase == "WaitingForDiscard"
           && !_riichiDeclarePending
           && !CallsIncludeRiichi();

    private bool CallsIncludeRiichi()
        => _lastCallButtonNodes != null &&
           _lastCallButtonNodes.ContainsKey(EmjUiReader.CallOptions.Riichi) &&
           _lastCallButtonNodes[EmjUiReader.CallOptions.Riichi] != 0;

    private string FormatCallKeys()
        => _lastCallButtonNodes == null || _lastCallButtonNodes.Count == 0
            ? "None"
            : string.Join(",", _lastCallButtonNodes.Keys);

    /// <summary>
    /// After Riichi ListItemClick: retry the Riichi button until the prompt is gone
    /// and calls no longer include Riichi. Only then discard the saved tile.
    /// OpponentTurn with Riichi still in calls is a failed declare — not in riichi.
    /// OpponentTurn without Riichi in calls is success (ListItemClick already discarded).
    /// </summary>
    private void UpdateRiichiDeclareState(string? gamePhase)
    {
        if (!_riichiDeclarePending)
            return;

        var callsHaveRiichi = CallsIncludeRiichi();
        Log($"[RIICHI-DIAG] Declare pending phase={gamePhase} calls={FormatCallKeys()} callsHaveRiichi={callsHaveRiichi} savedTile={_riichiDiscardTile ?? "(none)"} slot={_riichiDiscardSlot?.ToString() ?? "(none)"} pending={_pendingAction ?? "(none)"}");

        if (callsHaveRiichi || gamePhase == "RiichiDecisionPrompt")
        {
            if (gamePhase == "OpponentTurn")
            {
                Log("[RIICHI-DIAG] Declaration FAILED — left to OpponentTurn but calls still include Riichi. Not in riichi, not discarding saved tile, not tsumogiri.");
                FailRiichiDeclare();
                return;
            }

            TryScheduleRiichiClickRetry();
            return;
        }

        // Prompt gone and Riichi is not in the calls list — declaration stuck.
        if (gamePhase == "OpponentTurn")
        {
            Log("[RIICHI-DIAG] Declaration SUCCESS — prompt gone, calls no longer include Riichi, already OpponentTurn. ListItemClick completed declare+discard; not firing callback 7.");
            CompleteRiichiDeclareSuccess();
            return;
        }

        _riichiDeclarePending = false;
        _awaitingRiichiDiscard = true;
        Log($"[RIICHI-DIAG] Declaration stuck — prompt gone, no Riichi in calls, phase={gamePhase}. Will discard saved tile={_riichiDiscardTile ?? "(none)"} slot={_riichiDiscardSlot?.ToString() ?? "(none)"} via callback 7.");
        if (gamePhase == "WaitingForDiscard" && _pendingAction == null)
            TryScheduleRiichiDiscard();
    }

    private void TryScheduleRiichiClickRetry()
    {
        if (_pendingAction != null)
            return;
        if (!_config.AutoPlayEnabled || !_config.AutoCallEnabled || _paused)
            return;
        if (_consecutiveCallAttempts >= 5)
        {
            Log($"[RIICHI-DIAG] Stuck retrying Riichi button ({_consecutiveCallAttempts} attempts) — pausing, not discarding");
            return;
        }

        _pendingAction = "call:accept";
        _lastActionSignature = "call:accept-riichi-retry";
        _actionScheduledAtUtc = DateTime.UtcNow;
        var delayMs = _config.NextCallDelayMs(_rng);
        _actionExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(delayMs);
        Log($"[RIICHI-DIAG] Retrying Riichi button (not discard) execute at +{delayMs}ms attempts={_consecutiveCallAttempts}");
    }

    private void FailRiichiDeclare()
    {
        if (_pendingAction is "riichi-discard" or "riichi-tsumogiri" or "call:accept")
        {
            Log($"[RIICHI-DIAG] Clearing pending '{_pendingAction}' after failed declare");
            _pendingAction = null;
            _lastActionSignature = "";
        }
        ClearRiichiDeclareState(keepPendingAction: true);
    }

    private void CompleteRiichiDeclareSuccess()
    {
        if (_pendingAction is "riichi-discard" or "riichi-tsumogiri" or "call:accept")
        {
            Log($"[RIICHI-DIAG] Clearing pending '{_pendingAction}' after successful declare (discard already landed)");
            _pendingAction = null;
            _lastActionSignature = "";
        }
        ClearRiichiDeclareState(keepPendingAction: true);
    }

    private void ClearRiichiDeclareState(bool keepPendingAction)
    {
        _riichiDeclarePending = false;
        _awaitingRiichiDiscard = false;
        _riichiDiscardTile = null;
        _riichiDiscardIconId = null;
        _riichiDiscardSlot = null;
        if (!keepPendingAction)
        {
            _pendingAction = null;
            _lastActionSignature = "";
        }
    }

    private void CaptureRiichiDeclaredTile(EmjUiReader.InGameSuggestion? suggestion, string? gamePhase, string? prevPhase)
    {
        if (suggestion?.Type == EmjUiReader.SuggestionType.Riichi)
        {
            var riichiTile = suggestion.TileName ?? "(none)";
            var riichiIcon = suggestion.TileIconId?.ToString() ?? "(none)";
            Log($"[RIICHI-DIAG] Riichi suggestion detected: tile={riichiTile} icon={riichiIcon} phase={gamePhase} prev={prevPhase}");
            if (!string.IsNullOrEmpty(suggestion.TileName))
                _riichiDiscardTile = suggestion.TileName;
            if (suggestion.TileIconId is > 0)
                _riichiDiscardIconId = suggestion.TileIconId;
        }

        if (string.IsNullOrEmpty(_riichiDiscardTile) &&
            (suggestion?.Type == EmjUiReader.SuggestionType.Riichi || gamePhase == "RiichiDecisionPrompt"))
        {
            var providerTile = _activeProvider.GetDiscardTile();
            if (!string.IsNullOrEmpty(providerTile))
            {
                _riichiDiscardTile = providerTile;
                Log($"[RIICHI-DIAG] Riichi tile was none — using provider discard '{_riichiDiscardTile}' captured before the Riichi click");
            }
            else if (!string.IsNullOrEmpty(_lastSeenDiscardTile))
            {
                _riichiDiscardTile = _lastSeenDiscardTile;
                Log($"[RIICHI-DIAG] Riichi tile was none — using last discard suggestion '{_riichiDiscardTile}' captured before the Riichi click");
            }
        }

        if (_riichiDiscardIconId is null && _lastSeenDiscardIconId is > 0 &&
            (suggestion?.Type == EmjUiReader.SuggestionType.Riichi || gamePhase == "RiichiDecisionPrompt"))
        {
            _riichiDiscardIconId = _lastSeenDiscardIconId;
            Log($"[RIICHI-DIAG] Riichi icon was none — using last discard icon {_riichiDiscardIconId}");
        }
    }

    private static unsafe int ReadAtkInt(AtkUnitBase* addon, int index, int fallback = -1)
    {
        if (addon == null || addon->AtkValues == null || index < 0 || index >= addon->AtkValuesCount)
            return fallback;
        try { return addon->AtkValues[index].Int; }
        catch { return fallback; }
    }

    private static unsafe int[] SnapshotAtk(AtkUnitBase* addon, int count = 20)
    {
        var n = addon == null || addon->AtkValues == null ? 0 : Math.Min(count, (int)addon->AtkValuesCount);
        var vals = new int[n];
        for (var i = 0; i < n; i++)
            vals[i] = ReadAtkInt(addon, i);
        return vals;
    }

    private static bool AtkChanged(int[] before, int[] after)
    {
        var n = Math.Min(before.Length, after.Length);
        for (var i = 0; i < n; i++)
        {
            if (before[i] != after[i])
                return true;
        }
        return before.Length != after.Length;
    }

    /// <summary>
    /// After Riichi ListItemClick, ATK [1] and [18] hold the declared discard slot.
    /// Pre-accept [1] is 0, so 0 is not treated as a slot.
    /// </summary>
    private static unsafe int? ReadPostRiichiDiscardSlot(AtkUnitBase* addon)
    {
        var v18 = ReadAtkInt(addon, 18, -1);
        var v1 = ReadAtkInt(addon, 1, -1);
        if (v18 >= 0 && v18 <= 13)
            return v18;
        if (v1 >= 1 && v1 <= 13)
            return v1;
        return null;
    }

    private bool TileObservationMatchesDeclared(MahjongHandReader.MahjongTileObservation t)
    {
        if (_riichiDiscardIconId is > 0 && t.IconId == (uint)_riichiDiscardIconId.Value)
            return true;
        if (!string.IsNullOrEmpty(_riichiDiscardTile))
        {
            if (t.TileCode != null && TileCodesMatch(t.TileCode, _riichiDiscardTile))
                return true;
            var resolved = _iconMap.Resolve(t.IconId);
            if (resolved != null && TileCodesMatch(resolved, _riichiDiscardTile))
                return true;
        }
        return false;
    }

    /// <summary>
    /// After Riichi accept, discard the declared tile via callback 7 (same as a
    /// normal turn). Never FireCallback 8 (skip while atk0=6). Never click the
    /// EmjUiReader gap-heuristic "draw" node. Callback 7 handPos is 0-13 (14 tiles);
    /// pos 13 is the 14th closed tile, not the type-1022 draw visual.
    /// Tries suggestion tile, then ATK [18]/[1], then the drawn tile's matching
    /// closed slot until ATK actually changes.
    /// </summary>
    private unsafe bool ExecuteDeclaredRiichiDiscard(AtkUnitBase* addon)
    {
        _riichiDiscardSlot ??= ReadPostRiichiDiscardSlot(addon);
        var before = SnapshotAtk(addon);
        Log($"[RIICHI-DIAG] ExecuteDeclaredRiichiDiscard tile={_riichiDiscardTile ?? "(none)"} icon={_riichiDiscardIconId?.ToString() ?? "(none)"} slot={_riichiDiscardSlot?.ToString() ?? "(none)"} atk=[{string.Join(" ", before.Select((v, i) => $"[{i}]={v}"))}]");

        var snapshot = MahjongHandReader.Read(addon, _iconCapture, _iconMap);
        var closedHand = MahjongHandReader.ClosedTilesForCallback7(snapshot);
        var drawn = snapshot.DrawnTile;

        var attempts = new List<(int Pos, int? Node, string Reason)>();

        void AddAttempt(int pos, int? node, string reason)
        {
            if (pos is < 0 or > 13)
                return;
            if (attempts.Any(a => a.Pos == pos))
                return;
            attempts.Add((pos, node, reason));
        }

        var hasDeclaredTile = !string.IsNullOrEmpty(_riichiDiscardTile) || _riichiDiscardIconId is > 0;
        if (hasDeclaredTile)
        {
            // If the post-accept ATK slot is the suggested tile, use that copy first
            // (closed hand can contain duplicates of the same code). Slot 13 is the
            // 14th closed tile, not the type-1022 draw node.
            if (_riichiDiscardSlot is >= 0 and <= 13 && _riichiDiscardSlot.Value < closedHand.Count &&
                TileObservationMatchesDeclared(closedHand[_riichiDiscardSlot.Value]))
            {
                var t = closedHand[_riichiDiscardSlot.Value];
                AddAttempt(_riichiDiscardSlot.Value, t.NodeIndex, $"suggestion@atk-slot node={t.NodeIndex} code={t.TileCode}");
            }

            for (var i = 0; i < closedHand.Count && i <= 13; i++)
            {
                var t = closedHand[i];
                if (TileObservationMatchesDeclared(t))
                    AddAttempt(i, t.NodeIndex, $"suggestion-closed pos={i} node={t.NodeIndex} code={t.TileCode} icon={t.IconId}");
            }

            if (drawn != null && TileObservationMatchesDeclared(drawn))
            {
                var drawnPos = Callback7HandPosMatching(closedHand, t =>
                    t.IconId == drawn.IconId ||
                    (t.TileCode != null && drawn.TileCode != null && TileCodesMatch(t.TileCode, drawn.TileCode)));
                if (drawnPos is >= 0)
                    AddAttempt(drawnPos.Value, closedHand[drawnPos.Value].NodeIndex, $"suggestion-drawn-via-closed pos={drawnPos} node={closedHand[drawnPos.Value].NodeIndex} code={drawn.TileCode} — not assuming pos 13 is type-1022");
            }
        }

        if (_riichiDiscardSlot is >= 0 and <= 13)
        {
            int? node = _riichiDiscardSlot.Value < closedHand.Count
                ? closedHand[_riichiDiscardSlot.Value].NodeIndex
                : null;
            AddAttempt(_riichiDiscardSlot.Value, node, $"atk-slot [{_riichiDiscardSlot}] node={node?.ToString() ?? "(none)"}");
        }

        if (drawn != null)
        {
            var drawnPos = Callback7HandPosMatching(closedHand, t =>
                t.IconId == drawn.IconId ||
                (t.TileCode != null && drawn.TileCode != null && TileCodesMatch(t.TileCode, drawn.TileCode)));
            if (drawnPos is >= 0)
                AddAttempt(drawnPos.Value, closedHand[drawnPos.Value].NodeIndex, $"true-tsumogiri via closed pos={drawnPos} node={closedHand[drawnPos.Value].NodeIndex} code={drawn.TileCode} type={drawn.NodeType} (not blindly pos 13)");
        }

        if (attempts.Count == 0)
        {
            Log("[RIICHI-DIAG] No declared tile, ATK slot, or DrawnTile node — refusing to click a random closed-hand node");
            return false;
        }

        var attempt = attempts[0];
        Log($"[RIICHI-DIAG] Discard via callback 7 handPos={attempt.Pos} reason={attempt.Reason} (one attempt this tick, not skip/callback 8, not ReceiveEvent)");
        AddonClickHelper.TryDiscardTile(addon, attempt.Pos);
        var after7 = SnapshotAtk(addon);
        if (AtkChanged(before, after7))
        {
            Log($"[RIICHI-DIAG] Callback 7 pos={attempt.Pos} changed ATK atk0 {before.ElementAtOrDefault(0)}->{after7.ElementAtOrDefault(0)} slot {before.ElementAtOrDefault(18)}->{after7.ElementAtOrDefault(18)}");
            return true;
        }

        Log($"[RIICHI-DIAG] Post-riichi callback 7 pos={attempt.Pos} did not change ATK (still atk0={ReadAtkInt(addon, 0)}) — will retry callback 7 later, not ReceiveEvent");
        return false;
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

        // If we already executed an accept in this phase, don't re-schedule.
        // After clicking (e.g. Riichi), AtkValues[6] may clear while the phase
        // persists — re-scheduling would either double-click or trigger auto-pass.
        // Just wait for the phase transition.
        if (_lastCallIntentWasAccept || _awaitingRiichiDiscard || _riichiDeclarePending)
            return;

        var decision = _activeProvider.GetCallAction();

        // Tsumo and Ron are always winning hands — auto-accept even if the provider
        // has no opinion (AtkValues[6] often doesn't populate for these prompts,
        // so InGameSuggestionProvider returns null).
        if (decision == null && (_lastGamePhase == "TsumoDecisionPrompt" || _lastGamePhase == "RonDecisionPrompt"))
        {
            decision = "accept";
            Log($"Auto-accept for {_lastGamePhase} (provider returned null — never decline a win)");
        }

        // Riichi — AtkValues[6] sometimes doesn't populate "Riichi" even when the
        // Riichi button is visible (atk0=6 fallback detects the phase from buttons).
        // Auto-accept so we don't fall through to the 3-second auto-pass timeout.
        if (decision == null && _lastGamePhase == "RiichiDecisionPrompt")
        {
            decision = "accept";
            Log($"Auto-accept for RiichiDecisionPrompt (provider returned null — Riichi button visible but AtkValues[6] didn't populate)");
        }

        if (decision == null)
            return;

        // Log riichi accept/pass decisions
        if (_lastGamePhase == "RiichiDecisionPrompt")
        {
            CaptureRiichiDeclaredTile(null, _lastGamePhase, _lastGamePhase);
            Log($"[RIICHI-DIAG] Provider decision for Riichi: {decision} provider={_activeProvider.GetType().Name} savedTile={_riichiDiscardTile ?? "(none)"} icon={_riichiDiscardIconId?.ToString() ?? "(none)"}");
        }

        var action = $"call:{decision}";

        if (action == _lastActionSignature)
            return;

        _lastActionSignature = action;
        _pendingAction = action;
        _actionScheduledAtUtc = DateTime.UtcNow;
        var callDelayMs = _config.NextCallDelayMs(_rng);
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
        var delayMs = _config.NextCallDelayMs(_rng);
        _actionExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(delayMs);
        _chiChoiceAttempts++;

        Log($"Scheduled: {sig} attempt={_chiChoiceAttempts} preferred=[{string.Join(",", _preChiSuggestionTiles ?? new List<string>())}] (execute at +{delayMs}ms)");
    }

    /// <summary>
    /// Discard the live hint via FireCallback 7 (same as a working normal turn).
    /// Matches icon id and tile code from the same closed-hand list that is logged
    /// (including node 54). A discard turn has 14 tiles: callback 7 handPos is 0-13.
    /// Eligible index 13 is the 14th closed tile, not an illegal "latest/drawn" and
    /// not the type-1022 visual. Never FireCallback 8. Success requires ATK to change.
    /// </summary>
    private unsafe bool ExecuteHintedDiscard(AtkUnitBase* addon, string tileCode, int? hintIconId, bool allowUnhintedDrawn)
    {
        var before = SnapshotAtk(addon);
        var snapshot = MahjongHandReader.Read(addon, _iconCapture, _iconMap);
        var closedLogged = snapshot.HandTiles.OrderBy(t => t.X).ThenBy(t => t.NodeIndex).ToList();
        var closedHand = MahjongHandReader.ClosedTilesForCallback7(snapshot);
        var drawn = snapshot.DrawnTile;

        bool MatchesHint(MahjongHandReader.MahjongTileObservation t)
        {
            if (hintIconId is > 0 && t.IconId == (uint)hintIconId.Value)
                return true;
            if (!string.IsNullOrEmpty(tileCode))
            {
                if (t.TileCode != null && TileCodesMatch(t.TileCode, tileCode))
                    return true;
                var resolved = _iconMap.Resolve(t.IconId);
                if (resolved != null && TileCodesMatch(resolved, tileCode))
                    return true;
            }
            return false;
        }

        var attempts = new List<(int Pos, int Node, string Reason)>();

        void AddAttempt(int pos, int node, string reason)
        {
            // Discard turn: callback 7 handPos 0-13 (14 tiles). Slot 13 is a
            // closed tile when eligible has 14 entries; it is not type-1022.
            if (pos is < 0 or > 13)
                return;
            if (attempts.Any(a => a.Pos == pos))
                return;
            attempts.Add((pos, node, reason));
        }

        var hasHint = !string.IsNullOrEmpty(tileCode) || hintIconId is > 0;
        if (hasHint)
        {
            // Match from the same closed=[] dump we log, then map that node onto
            // callback 7 pos 0-13 via the filtered 54/59-71 list. Do not skip
            // node 54. Do not cap at 0-12 — a discard turn's 14th tile is pos 13
            // (live AZPC: RED icon 76074 at eligibleIndex 13).
            foreach (var t in closedLogged)
            {
                if (!MatchesHint(t))
                    continue;
                if (!MahjongHandReader.IsCallback7ClosedHandNode(t.NodeIndex))
                {
                    Log($"[DISCARD] Hint '{tileCode}' icon={t.IconId} at node={t.NodeIndex} is a placeholder/extra — not a callback 7 slot");
                    continue;
                }

                var pos = closedHand.FindIndex(c => c.NodeIndex == t.NodeIndex);
                if (pos is >= 0 and <= 13)
                    AddAttempt(pos, t.NodeIndex, $"hint-closed pos={pos} node={t.NodeIndex} code={t.TileCode} icon={t.IconId}");
                else
                    Log($"[DISCARD] Hint '{tileCode}' node={t.NodeIndex} icon={t.IconId} is not in callback 7 slots 0-13 (eligibleIndex={pos})");
            }

            // Eligible list is the callback 7 order — fire any 0-13 hit even if
            // the logged-list walk missed (duplicate nodes / filter edge).
            for (var i = 0; i < closedHand.Count && i <= 13; i++)
            {
                if (MatchesHint(closedHand[i]))
                    AddAttempt(i, closedHand[i].NodeIndex, $"hint-eligible pos={i} node={closedHand[i].NodeIndex} code={closedHand[i].TileCode} icon={closedHand[i].IconId}");
            }

            // Type-1022 is a draw visual, not callback 7 pos 13. If the hint is
            // the drawn tile, discard the closed slot that shares its icon.
            if (drawn != null && MatchesHint(drawn))
            {
                var drawnPos = Callback7HandPosMatching(closedHand, t =>
                    t.IconId == drawn.IconId ||
                    (t.TileCode != null && drawn.TileCode != null && TileCodesMatch(t.TileCode, drawn.TileCode)));
                if (drawnPos is >= 0)
                    AddAttempt(drawnPos.Value, closedHand[drawnPos.Value].NodeIndex, $"hint-drawn-via-closed pos={drawnPos} node={closedHand[drawnPos.Value].NodeIndex} code={drawn.TileCode}");
                else if (attempts.Count == 0)
                    Log($"[DISCARD] Hint '{tileCode}' matches type-1022 draw node={drawn.NodeIndex} but that icon is not in eligible 0-13 — not firing callback 8");
            }
        }

        if (attempts.Count == 0 && allowUnhintedDrawn && drawn != null)
        {
            var drawnPos = Callback7HandPosMatching(closedHand, t =>
                t.IconId == drawn.IconId ||
                (t.TileCode != null && drawn.TileCode != null && TileCodesMatch(t.TileCode, drawn.TileCode)));
            if (drawnPos is >= 0)
                AddAttempt(drawnPos.Value, closedHand[drawnPos.Value].NodeIndex, $"unhinted-drawn-via-closed pos={drawnPos} node={closedHand[drawnPos.Value].NodeIndex} code={drawn.TileCode} type={drawn.NodeType}");
            else
                Log($"[DISCARD] Unhinted draw {drawn.TileCode}(node={drawn.NodeIndex}) is not in eligible 0-13 — not firing callback 8");
        }

        var closedDesc = string.Join(",", closedLogged.Select(t => $"{t.TileCode ?? "?"}(icon={t.IconId} node={t.NodeIndex})"));
        var eligibleDesc = string.Join(",", closedHand.Select((t, i) => $"{i}:{t.TileCode ?? "?"}(icon={t.IconId} node={t.NodeIndex})"));
        var drawnDesc = drawn != null ? $"{drawn.TileCode}(icon={drawn.IconId} node={drawn.NodeIndex} type={drawn.NodeType})" : "none";
        Log($"[DISCARD] Hint tile={tileCode ?? "(none)"} icon={hintIconId?.ToString() ?? "(none)"} atk0={before.ElementAtOrDefault(0)} closed=[{closedDesc}] eligible=[{eligibleDesc}] draw={drawnDesc} attempts={attempts.Count}");

        if (attempts.Count == 0)
        {
            Log($"[DISCARD] No callback-7 slot for hint '{tileCode}' icon={hintIconId?.ToString() ?? "(none)"} — not clicking a latest/drawn tile and not firing callback 8");
            return false;
        }

        var attempt = attempts[0];
        Log($"[DISCARD] FireCallback 7 handPos={attempt.Pos} reason={attempt.Reason} (one attempt this tick, not ReceiveEvent)");
        AddonClickHelper.TryDiscardTile(addon, attempt.Pos);
        var after7 = SnapshotAtk(addon);
        if (AtkChanged(before, after7))
        {
            Log($"[DISCARD] Callback 7 pos={attempt.Pos} changed ATK atk0 {before.ElementAtOrDefault(0)}->{after7.ElementAtOrDefault(0)}");
            return true;
        }

        Log($"[DISCARD] Hint '{tileCode}' callback 7 pos={attempt.Pos} did not change ATK (still atk0={ReadAtkInt(addon, 0)}) — will retry callback 7 later, not ReceiveEvent");
        return false;
    }

    /// <summary>
    /// Callback 7 handPos for a discard turn is 0-13 (14 tiles).
    /// </summary>
    private static int? Callback7HandPosMatching(
        IReadOnlyList<MahjongHandReader.MahjongTileObservation> eligible,
        Func<MahjongHandReader.MahjongTileObservation, bool> predicate)
    {
        for (var i = 0; i < eligible.Count && i <= 13; i++)
        {
            if (predicate(eligible[i]))
                return i;
        }
        return null;
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

            // Fallback: during RiichiDecisionPrompt (and similar self-action prompts),
            // the button scan may capture stale labels from a prior call prompt
            // (e.g. "Chi"/"Skip" text hasn't updated to "Riichi" yet).
            // In these phases, the first non-Skip button is always the accept option,
            // so click it regardless of label.
            if (_lastGamePhase is "RiichiDecisionPrompt" or "TsumoDecisionPrompt" or "RonDecisionPrompt")
            {
                foreach (var kvp in _lastCallButtonNodes)
                {
                    if (kvp.Key != EmjUiReader.CallOptions.Skip && kvp.Value != 0)
                    {
                        Log($"Fallback accept: clicking {kvp.Key} button (ptr={kvp.Value:X}) as proxy for {_lastGamePhase} (label may be stale)");
                        return AddonClickHelper.TryAcceptCallViaListClick(addon, kvp.Value, kvp.Key.ToString());
                    }
                }
            }

            Log($"Cannot accept call: no matching button for phase={_lastGamePhase} in captured nodes [{string.Join(",", _lastCallButtonNodes.Keys)}]");
            return false;
        }
        else
        {
            // Skip/pass — use callback 8 (FireCallback [8, 0]).
            // Per callbackNotes.txt, callback 8 is "discard drawn tile (tsumogiri)
            // ALSO: skip/pass on call prompts." Skip is NOT a list item in the UI,
            // so ListItemClick does NOT work for skip (it accepts instead).
            int rawAtk0 = -1;
            try
            {
                if (addon->AtkValues != null && addon->AtkValuesCount > 0)
                    rawAtk0 = addon->AtkValues[0].Int;
            }
            catch { }

            Log($"Executing call skip via callback 8 (rawAtk0={rawAtk0})");
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
