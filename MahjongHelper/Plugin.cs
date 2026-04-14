using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using System.IO;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using MahjongHelper.Windows;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MahjongHelper.Mahjong;
using MahjongHelper.Mahjong.Debug;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace MahjongHelper;

public sealed partial class Plugin : IDalamudPlugin
{
    private static readonly string CacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MahjongHelper");
    private static readonly string MappingReportPath = Path.Combine(CacheDirectory, "mapping_progress_report.txt");
    private static readonly string ProbeHistoryPath = Path.Combine(CacheDirectory, "probe_history.log");
    private static readonly string ProbeSignalsPath = Path.Combine(CacheDirectory, "probe_signals.log");
    private static readonly string TileCandidatesPath = Path.Combine(CacheDirectory, "tile_candidates.log");
    private static readonly string UiStateHistoryPath = Path.Combine(CacheDirectory, "mahjong_ui_state_history.log");
    private static readonly string NormalizedStateHistoryPath = Path.Combine(CacheDirectory, "normalized_state_history.log");
    private static readonly string NormalizedStateExportPath = Path.Combine(CacheDirectory, "normalized_state_export.txt");
    private static readonly string ProbeSnippetExportPath = Path.Combine(CacheDirectory, "probe_snippet_export.txt");
    private static readonly string StartupDiagDumpPath = Path.Combine(CacheDirectory, "startup_diag.txt");
    private static readonly string ActionProbePath = Path.Combine(CacheDirectory, "action_probe.log");

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    [PluginService]
    private static IGameInteropProvider GameInterop { get; set; } = null!;

    private const string CommandName = "/mj";
    [PluginService]
    private static IAddonLifecycle AddonLifecycle { get; set; } = null!;

    [PluginService]
    private static IGameGui GameGui { get; set; } = null!;

    [PluginService]
    private static IFramework Framework { get; set; } = null!;

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("MahjongHelper");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    private SuggestionOverlayWindow OverlayWindow { get; init; }
    private DateTime _nextDumpAtUtc = DateTime.MinValue;
    private DateTime _nextUiStateCaptureAtUtc = DateTime.MinValue;
    private bool _startupDumpDone = false;
    private IconIdCapture _iconCapture = null!;
    private MahjongIconMap _iconMap = null!;
    private HashSet<uint> _lastEligibleIconIds = new();
    private string _lastProbeStateSignature = string.Empty;
    private int? _lastAgentEmj28State;
    private byte[]? _lastAgentEmj28Bytes;
    private string _lastUiStateSignature = string.Empty;
    private AddonReaderScheduler _readerScheduler = null!;
    private EmjAddonReader _emjReader = null!;
    private DateTime _lastSchedulerTickUtc = DateTime.MinValue;
    private MahjongGameState? _lastMergedState;
    private int? _latestProbeAgentState;
    private string _lastNormalizedStateSignature = string.Empty;
    private DateTime? _lastSuccessfulProbeUpdateUtc;
    private DateTime? _lastSuccessfulNodeUpdateUtc;
    private DateTime? _lastSuccessfulMergeUpdateUtc;
    private string _activeSourcePath = "Unknown";
    private readonly List<string> _recentFailures = [];
    private readonly List<string> _recentTransitions = [];
    private const int MaxRecentFailures = 12;
    private const int MaxRecentTransitions = 40;
    private AddonReaderStatus _lastReaderStatus = AddonReaderStatus.AddonNotFound;
    private MahjongServerClient _serverClient = null!;
    private SuggestMoveResponse? _lastSuggestion;
    private DateTime _nextHealthCheckUtc = DateTime.MinValue;
    private DateTime _nextSuggestUtc = DateTime.MinValue;
    private string _lastSuggestHandSignature = string.Empty;
    private bool _suggestInFlight;
    private bool _callEvalInFlight;
    private string _lastCallEvalSignature = string.Empty;
    private AutoPlayManager _autoPlayManager = null!;
    private nint _lastAddonAddress;
    private EmjUiReader.UiState? _lastUiState;
    private string _lastActionProbeSignature = string.Empty;
    private DateTime _nextAutoplayHeartbeatUtc = DateTime.MinValue;

    public Plugin()
    {
        _iconCapture = new IconIdCapture(GameInterop);
        _iconMap = new MahjongIconMap();
        _serverClient = new MahjongServerClient();
        _emjReader = new EmjAddonReader();
        _readerScheduler = new AddonReaderScheduler(GameGui);
        _readerScheduler.AddObservedAddon(_emjReader);
        _lastSchedulerTickUtc = DateTime.UtcNow;

        AddonLifecycle.RegisterListener(
            AddonEvent.PreDraw,
            "EmjL", // temporary, we’ll fix name later
            OnMahjongDraw
        );
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        _autoPlayManager = new AutoPlayManager(Configuration, _iconMap);

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);
        OverlayWindow = new SuggestionOverlayWindow(Configuration);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(OverlayWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "/mj — toggle debug window | /mj overlay | /mj compact | /mj auto | /mj pause | /mj mark discard|call | /mj probecallback <a> <b> [run] | /mj clicktile <nodeIndex> [run]"
        });

        // Tell the UI system that we want our windows to be drawn through the window system
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;

        // This adds a button to the plugin installer entry of this plugin which allows
        // toggling the display status of the configuration ui
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

        // Adds another button doing the same but for the main ui of the plugin
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        Framework.Update += OnFrameworkUpdate;

        // Add a simple message to the log with level set to information
        // Use /xllog to open the log window in-game
        // Example Output: 00:57:54.959 | INF | [MahjongHelper] ===A cool log message from Sample Plugin===
        Log.Information($"===A cool log message from {PluginInterface.Manifest.Name}===");

        // If EmjL addon is already open (player is in a mahjong game), dump immediately on load
        TryImmediateDump();

        // Always dump startup diagnostics so pipeline state can be inspected offline
        DumpStartupDiagnostics();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;
        var delta = (float)Math.Max(0.0, (now - _lastSchedulerTickUtc).TotalSeconds);
        _lastSchedulerTickUtc = now;

        _readerScheduler.Update(delta);
        _iconCapture.FlushIfNeeded();

        var readerStatus = _emjReader.Status;
        if (MainWindow.IsOpen)
            MainWindow.readerStatus = readerStatus.ToString();

        if (readerStatus != _lastReaderStatus)
        {
            if (readerStatus != AddonReaderStatus.NoErrors)
                RecordFailure($"Reader status changed to {readerStatus}");

            _lastReaderStatus = readerStatus;
        }

        if (MainWindow.IsOpen)
        {
            MainWindow.diagnosticsText = BuildDiagnosticsText();
            MainWindow.recentTransitionsText = BuildRecentTransitionsText();
            MainWindow.serverStatusText = _serverClient.GetStatusText();
        }

        // Auto-play status line (only when debug window visible)
        if (MainWindow.IsOpen)
        {
            var ap = Configuration.AutoPlayEnabled;
            var phase = _autoPlayManager.PendingAction != null
                ? $"pending={_autoPlayManager.PendingAction}"
                : _lastMergedState?.GamePhase.Value ?? "?";
            var paused = _autoPlayManager.IsPaused ? " [PAUSED]" : "";
            var provider = Configuration.AutoPlayEnabled ? _autoPlayManager.ActiveProviderName : "";
            MainWindow.autoPlayStatusText = ap
                ? $"ON ({provider}) {phase}{paused}"
                : "Off";
        }

        // Feed overlay data
        OverlayWindow.IsOpen = Configuration.OverlayVisible && _lastReaderStatus == AddonReaderStatus.NoErrors;
        OverlayWindow.ServerStatus = _serverClient.GetStatusText();
        if (_lastMergedState != null)
        {
            OverlayWindow.HandDescription = _lastMergedState.HandDescription.Value;
            OverlayWindow.CurrentTurnIndex = _lastMergedState.CurrentTurn.Value switch
            {
                "Player" => 0,
                "Right" => 1,
                "Opposite" => 2,
                "Left" => 3,
                _ => null,
            };
        }

        // Auto-play: feed status to overlay and execute scheduled actions
        OverlayWindow.AutoPlayEnabled = Configuration.AutoPlayEnabled;
        OverlayWindow.AutoPlayPaused = _autoPlayManager.IsPaused;
        OverlayWindow.PendingAutoAction = _autoPlayManager.PendingAction;
        if (Configuration.AutoPlayEnabled && _lastAddonAddress != 0)
        {
            unsafe
            {
                var addonPtr = (AtkUnitBase*)_lastAddonAddress;
                _autoPlayManager.Update(addonPtr);
            }
        }

        // Periodic server health check (every 30 seconds)
        if (now >= _nextHealthCheckUtc)
        {
            _nextHealthCheckUtc = now.AddSeconds(30);
            Task.Run(async () =>
            {
                try { await _serverClient.CheckHealthAsync().ConfigureAwait(false); }
                catch { }
            });
        }
    }

    private unsafe void TryImmediateDump()
    {
        try
        {
            var addonPtr = GameGui.GetAddonByName("EmjL");
            if (addonPtr.IsNull) return;

            var addon = (AtkUnitBase*)addonPtr.Address;
            if (addon == null || addon->RootNode == null) return;

            var handSnapshot = MahjongHandReader.Read(addon, _iconCapture, _iconMap);
            var uiState = EmjUiReader.Read(addon, _iconCapture, _iconMap);
            _lastUiState = uiState;
            var eligibleIconIds = BuildEligibleIconSet(handSnapshot);
            _lastEligibleIconIds = eligibleIconIds;
            // Hover-learning disabled: unreliable mappings. Pivoting to memory probing.
            // _iconMap.ObserveHover(addon, eligibleIconIds);
            MainWindow.mappingReport = _iconMap.BuildProgressReport(_iconCapture.IconMap.Values);

            Log.Information("EmjL already open on plugin load — dumping immediately");
            var dumpContent = TileDataDumper.DumpAndSave(addon, _iconCapture, _iconMap);
            MainWindow.text = dumpContent;
            MainWindow.text2 = handSnapshot.ToDisplayText();
            MainWindow.text3 = uiState.ToDisplayText();
            TrackProbeState(dumpContent);
            _lastSuccessfulNodeUpdateUtc = DateTime.UtcNow;
            var merged = MahjongGameStateBuilder.Merge(_latestProbeAgentState, uiState, _lastMergedState);
            HandleMergedStateUpdate(merged, "startup");
            TrackUiState(uiState, "startup");
            _startupDumpDone = true;
            _nextDumpAtUtc = DateTime.UtcNow.AddSeconds(3);
        }
        catch (Exception ex)
        {
            Log.Error($"Startup dump failed: {ex.Message}");
            RecordFailure($"Startup dump failed: {ex.Message}");
        }
    }

    private void OnMahjongFinalize(AddonEvent type, AddonArgs args)
    {
        Log.Information("EmjL addon finalized — clearing stale addon address");
        _lastAddonAddress = 0;
        _autoPlayManager.ClearPending();
    }

    private unsafe void OnMahjongDraw(AddonEvent type, AddonArgs args)
    {
        var addonPtr = args.Addon; // AtkUnitBasePtr
        if (addonPtr.IsNull)
        {
            MainWindow.text = "EmjL addonPtr is null";
            RecordFailure("OnMahjongDraw: EmjL addonPtr is null");
            return;
        }

        var addon = (AtkUnitBase*)addonPtr.Address;
        _lastAddonAddress = addonPtr.Address;

        // (Optional) quick safety checks
        if (addon == null || addon->RootNode == null)
        {
            MainWindow.text = "addon/root is null";
            RecordFailure("OnMahjongDraw: addon/root is null");
            return;
        }

        // Hover-learning disabled: unreliable. Pivoting to memory probing.
        // _iconMap.ObserveHover(addon, _lastEligibleIconIds);

        // Capture AgentId.Emj state for comparison-based tile discovery
        try
        {
            var agentModule = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentModule.Instance();
            if (agentModule != null)
            {
                var emjAgent = agentModule->GetAgentByInternalId(FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId.Emj);
                if (emjAgent != null)
                {
                    StateComparisonLogger.CaptureSnapshot((nint)emjAgent);
                }
            }
        }
        catch
        {
            // Never crash from diagnostics
            RecordFailure("OnMahjongDraw: failed to capture AgentId.Emj snapshot");
        }

        var now = DateTime.UtcNow;

        // Lightweight UI-state sampling can run faster than full dump cadence.
        if (now >= _nextUiStateCaptureAtUtc)
        {
            try
            {
                var uiStateFast = EmjUiReader.Read(addon, _iconCapture, _iconMap);
                _lastUiState = uiStateFast;
                MainWindow.text3 = uiStateFast.ToDisplayText();
                _lastSuccessfulNodeUpdateUtc = DateTime.UtcNow;
                TrackUiState(uiStateFast, "realtime");
                var merged = MahjongGameStateBuilder.Merge(_latestProbeAgentState, uiStateFast, _lastMergedState);
                HandleMergedStateUpdate(merged, "realtime");
                UpdateHandIconDisplay(uiStateFast);
            }
            catch (Exception ex)
            {
                // Never disrupt gameplay from diagnostics.
                RecordFailure($"Realtime uiState capture failed: {ex.Message}");
            }

            _nextUiStateCaptureAtUtc = now.AddMilliseconds(250);
        }

        // IMPORTANT: throttle so you don’t dump every frame
        // But always dump on first draw after plugin load
        if (_startupDumpDone && now < _nextDumpAtUtc)
            return;

        _startupDumpDone = true;
        _nextDumpAtUtc = now.AddSeconds(3); // dump every 3 seconds

        // Write structured tile data to file + display in UI
        try
        {
            var handSnapshot = MahjongHandReader.Read(addon, _iconCapture, _iconMap);
            var uiState = EmjUiReader.Read(addon, _iconCapture, _iconMap);
            _lastUiState = uiState;
            var eligibleIconIds = BuildEligibleIconSet(handSnapshot);
            _lastEligibleIconIds = eligibleIconIds;
            // Hover-learning disabled: unreliable mappings. Pivoting to memory probing.
            // _iconMap.ObserveHover(addon, eligibleIconIds);
            var dumpContent = TileDataDumper.DumpAndSave(addon, _iconCapture, _iconMap);
            MainWindow.text = dumpContent;
            MainWindow.text2 = handSnapshot.ToDisplayText();
            MainWindow.text3 = uiState.ToDisplayText();
            MainWindow.mappingReport = _iconMap.BuildProgressReport(_iconCapture.IconMap.Values);
            TrackProbeState(dumpContent);
            _lastSuccessfulNodeUpdateUtc = DateTime.UtcNow;
            var merged = MahjongGameStateBuilder.Merge(_latestProbeAgentState, uiState, _lastMergedState);
            HandleMergedStateUpdate(merged, "periodic");
            TrackUiState(uiState, "periodic");
        }
        catch (Exception ex)
        {
            MainWindow.text = $"Dump failed: {ex.Message}";
            RecordFailure($"Periodic dump failed: {ex.Message}");
        }
    }

    private void HandleMergedStateUpdate(MahjongGameState merged, string source)
    {
        _lastMergedState = merged;
        MainWindow.normalizedStateText = merged.ToDisplayText();
        _lastSuccessfulMergeUpdateUtc = DateTime.UtcNow;

        // Always forward game state to auto-play manager, even if the normalized
        // signature hasn't changed. The manager needs continuous updates to detect
        // phase transitions that the signature dedup may miss (e.g., AtkVal[0]
        // changing while hand/discards remain the same).
        _autoPlayManager.OnGameStateUpdate(merged.GamePhase.Value, _lastUiState?.Slots,
            _lastUiState?.GameInfo?.CallButtonNodes,
            _lastUiState?.GameInfo?.Suggestion,
            _iconCapture);

        // Always try to request suggestions and call evaluations — phase may
        // have changed even if the normalized signature hasn't.
        TryRequestSuggestion(merged);
        TryRequestCallEvaluation(merged);

        var signature = BuildNormalizedStateSignature(merged);
        if (signature == _lastNormalizedStateSignature)
        {
            _activeSourcePath = BuildActiveSourcePath(merged);

            // Periodic heartbeat: log AtkVal[0] and phase every 5s even when deduped,
            // so we can diagnose stuck-state issues.
            if (DateTime.UtcNow >= _nextAutoplayHeartbeatUtc)
            {
                _nextAutoplayHeartbeatUtc = DateTime.UtcNow.AddSeconds(5);
                var agentState = merged.AgentState.Value;
                var phase = merged.GamePhase.Value;
                var calls = merged.AvailableCalls.Value;
                var handCount = merged.HandIconIds.Value?.Count ?? 0;
                // Also log raw AtkVal[0] from the addon for comparison
                int rawAtk0 = -1;
                unsafe
                {
                    if (_lastAddonAddress != 0)
                    {
                        var hbAddon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)_lastAddonAddress;
                        if (hbAddon->AtkValues != null && hbAddon->AtkValuesCount > 0)
                            rawAtk0 = hbAddon->AtkValues[0].Int;
                    }
                }
                LogToFile("autoplay.log",
                    $"[Heartbeat] phase={phase} agent={agentState} rawAtk0={rawAtk0} calls={calls} sug={merged.InGameSuggestion.Value ?? "?"} hand={handCount} pending={_autoPlayManager.PendingAction}");
            }

            return;
        }

        _lastNormalizedStateSignature = signature;
        _activeSourcePath = BuildActiveSourcePath(merged);

        var transition =
            $"{DateTime.UtcNow:O} source={source} active={_activeSourcePath} " +
            $"agent={SafeFieldValue(merged.AgentState.Value)} handCount={(merged.HandIconIds.Value?.Count ?? 0)} draw={SafeFieldValue(merged.DrawIconId.Value)}";

        AppendRecentTransition(transition);

        // Passive action-probe snapshot for callback discovery
        LogActionProbe(merged, source);

        try
        {
            Directory.CreateDirectory(CacheDirectory);
            var entry =
                $"=== Normalized State Change {DateTime.UtcNow:O} source={source} active={_activeSourcePath} ==={Environment.NewLine}" +
                merged.ToDisplayText() + Environment.NewLine +
                "=================================================================" + Environment.NewLine + Environment.NewLine;
            File.AppendAllText(NormalizedStateHistoryPath, entry);
        }
        catch (Exception ex)
        {
            RecordFailure($"Normalized transition log failed: {ex.Message}");
        }
    }

    private void TryRequestSuggestion(MahjongGameState state)
    {
        // Only request when server is healthy and we're not already in-flight
        if (_serverClient.IsHealthy != true || _suggestInFlight)
            return;

        // Only request during actual gameplay phases
        var phase = state.GamePhase.Value;
        if (phase == "BetweenRounds" || phase == "GameOver" || phase == "Unknown")
            return;

        // Only request when player has a hand with tiles
        var request = GameStateMapper.BuildSuggestMoveRequest(state, _iconMap);
        if (request == null || request.Hand.Count == 0)
            return;

        // Only request when player has 14 tiles total (13 hand + drawn, needs to discard)
        if (GameStateMapper.GetTotalTileCount(request) < 14)
            return;

        // Avoid re-requesting for the same hand
        var handSig = string.Join(",", request.Hand) + "|" + (request.DrawnTile ?? "");
        if (handSig == _lastSuggestHandSignature)
            return;

        // Throttle: at most once per second
        if (DateTime.UtcNow < _nextSuggestUtc)
            return;

        _lastSuggestHandSignature = handSig;
        _suggestInFlight = true;
        _nextSuggestUtc = DateTime.UtcNow.AddSeconds(1);

        Task.Run(async () =>
        {
            try
            {
                var response = await _serverClient.SuggestMoveAsync(request).ConfigureAwait(false);
                _lastSuggestion = response;
                OverlayWindow.LastSuggestion = response;
                if (response != null)
                {
                    MainWindow.serverSuggestionText = FormatSuggestion(response);
                    _autoPlayManager.OnSuggestionReceived(response,
                        _lastMergedState?.GamePhase.Value,
                        _lastUiState?.Slots);
                }
                else
                {
                    MainWindow.serverSuggestionText = $"Server error: {_serverClient.LastError ?? "unknown"}";
                }
            }
            catch (Exception ex)
            {
                MainWindow.serverSuggestionText = $"Request failed: {ex.Message}";
            }
            finally
            {
                _suggestInFlight = false;
            }
        });
    }

    private void TryRequestCallEvaluation(MahjongGameState state)
    {
        // Clear call eval state when NOT in a call/decision phase.
        // This must be based on game phase, not call buttons, because stale
        // button nodes persist after prompts are dismissed.
        var phase = state.GamePhase.Value;
        if (phase != "CallDecisionPrompt" && phase != "RonDecisionPrompt" &&
            phase != "TsumoDecisionPrompt" && phase != "RiichiDecisionPrompt")
        {
            if (!string.IsNullOrEmpty(OverlayWindow.CallRecommendation))
                OverlayWindow.CallRecommendation = null;
            _lastCallEvalSignature = string.Empty;
            return;
        }

        if (_serverClient.IsHealthy != true || _callEvalInFlight)
            return;

        // Only evaluate when call buttons are actually detected
        var calls = state.AvailableCalls.Value;
        if (string.IsNullOrEmpty(calls) || calls == "None")
            return;

        // Determine call type (pick the most significant available call)
        string callType;
        if (calls.Contains("Ron")) callType = "RON";
        else if (calls.Contains("Tsumo")) callType = "TSUMO";
        else if (calls.Contains("Kan")) callType = "KAN";
        else if (calls.Contains("Pon")) callType = "PON";
        else if (calls.Contains("Chi")) callType = "CHI";
        else if (calls.Contains("Riichi")) callType = "RIICHI";
        else return;

        // Determine the call tile: last tile in the most recent opponent discard pool
        var callTile = GetLastOpponentDiscard(state);

        // Avoid re-requesting for the same call situation
        var sig = $"{calls}|{callTile}";
        if (sig == _lastCallEvalSignature)
            return;

        _lastCallEvalSignature = sig;
        _callEvalInFlight = true;

        var request = GameStateMapper.BuildEvaluateCallRequest(state, _iconMap, callTile, callType);
        if (request == null)
        {
            _callEvalInFlight = false;
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                var response = await _serverClient.EvaluateCallAsync(request).ConfigureAwait(false);
                if (response != null)
                {
                    var action = response.ShouldCall ? "YES" : "NO";
                    var confText = response.Confidence.HasValue ? $" ({response.Confidence:P0})" : "";
                    var reason = !string.IsNullOrEmpty(response.Reasoning) ? $" — {response.Reasoning}" : "";
                    OverlayWindow.CallRecommendation = $"{callType.ToUpperInvariant()} {callTile ?? "?"}: {action}{confText}{reason}";
                    _autoPlayManager.OnCallEvalReceived(response, _lastMergedState?.GamePhase.Value);
                }
                else
                {
                    OverlayWindow.CallRecommendation = $"{callType}: server error";
                }
            }
            catch
            {
                OverlayWindow.CallRecommendation = $"{callType}: request failed";
            }
            finally
            {
                _callEvalInFlight = false;
            }
        });
    }

    /// <summary>
    /// Gets the last discarded tile from the opponent whose turn it was (the call tile).
    /// Checks right, opposite, left discard pools and returns the last tile from whichever
    /// has the most recent discard.
    /// </summary>
    private static string? GetLastOpponentDiscard(MahjongGameState state)
    {
        // For riichi/tsumo, the call tile is from the player's own draw
        var phase = state.GamePhase.Value;
        if (phase == "RiichiDecisionPrompt" || phase == "TsumoDecisionPrompt")
            return null;

        // Check opponent discard pools for the most recent tile
        var pools = new[]
        {
            state.RightDiscards.Value,
            state.OppositeDiscards.Value,
            state.LeftDiscards.Value,
        };

        // The call tile is the last discard from the opponent whose turn just ended.
        // We use CurrentTurn to identify, but if unknown, pick the longest pool's last tile.
        var turn = state.CurrentTurn.Value;
        IReadOnlyList<string>? targetPool = turn switch
        {
            "Right" => state.RightDiscards.Value,
            "Opposite" => state.OppositeDiscards.Value,
            "Left" => state.LeftDiscards.Value,
            _ => null,
        };

        if (targetPool is { Count: > 0 })
            return targetPool[^1];

        // Fallback: find the pool with the most tiles and take the last one
        foreach (var pool in pools)
        {
            if (pool is { Count: > 0 })
                return pool[^1];
        }

        return null;
    }

    private static string FormatSuggestion(SuggestMoveResponse response)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(response.Error))
        {
            sb.AppendLine($"Server error: {response.Error}");
            return sb.ToString();
        }

        if (response.Shanten.HasValue)
            sb.AppendLine($"Shanten: {response.Shanten}");

        if (response.Suggestions.Count == 0)
        {
            sb.AppendLine("No suggestions");
            return sb.ToString();
        }

        sb.AppendLine($"Suggestions ({response.Suggestions.Count}):");
        foreach (var s in response.Suggestions)
        {
            var parts = new List<string> { $"  {s.Tile}" };
            if (s.Shanten.HasValue)
                parts.Add($"shanten={s.Shanten}");
            if (s.Ukeire.HasValue)
                parts.Add($"ukeire={s.Ukeire}");
            if (s.Confidence.HasValue)
                parts.Add($"conf={s.Confidence:P0}");
            sb.AppendLine(string.Join(" ", parts));
            if (!string.IsNullOrEmpty(s.Reasoning))
                sb.AppendLine($"    {s.Reasoning}");
        }

        return sb.ToString();
    }

    private static string BuildNormalizedStateSignature(MahjongGameState state)
    {
        var handIds = state.HandIconIds.Value == null ? string.Empty : string.Join(",", state.HandIconIds.Value);
        var playerDiscards = state.PlayerDiscards.Value == null ? string.Empty : string.Join(",", state.PlayerDiscards.Value);
        var rightDiscards = state.RightDiscards.Value == null ? string.Empty : string.Join(",", state.RightDiscards.Value);
        var oppositeDiscards = state.OppositeDiscards.Value == null ? string.Empty : string.Join(",", state.OppositeDiscards.Value);
        var leftDiscards = state.LeftDiscards.Value == null ? string.Empty : string.Join(",", state.LeftDiscards.Value);
        var doraIndicators = state.DoraIndicators.Value == null ? string.Empty : string.Join(",", state.DoraIndicators.Value);
        var riichiStatus = state.RiichiStatus.Value == null ? string.Empty : string.Join(",", state.RiichiStatus.Value);
        return string.Join("|",
            state.AgentState.Value.ToString(),
            state.AgentState.Source,
            handIds,
            state.HandIconIds.Source,
            state.DrawIconId.Value.ToString(),
            state.DrawIconId.Source,
            state.HandDescription.Value ?? string.Empty,
            state.HandDescription.Source,
            playerDiscards,
            rightDiscards,
            oppositeDiscards,
            leftDiscards,
            doraIndicators,
            state.SeatWind.Value.ToString(),
            state.RoundWind.Value.ToString(),
            state.RoundNumber.Value.ToString(),
            state.PlayerScore.Value.ToString(),
            state.RightScore.Value.ToString(),
            state.OppositeScore.Value.ToString(),
            state.LeftScore.Value.ToString(),
            riichiStatus,
            state.AvailableCalls.Value ?? string.Empty,
            state.GamePhase.Value ?? string.Empty,
            state.CurrentTurn.Value ?? string.Empty);
    }

    private static string BuildActiveSourcePath(MahjongGameState state)
    {
        var sources = new HashSet<MahjongStateSource>
        {
            state.AgentState.Source,
            state.HandIconIds.Source,
            state.DrawIconId.Source,
            state.HandDescription.Source,
            state.SeatWind.Source,
            state.GamePhase.Source,
        };

        sources.Remove(MahjongStateSource.Unknown);
        return sources.Count == 0 ? "Unknown" : string.Join("+", sources.OrderBy(s => s.ToString()));
    }

    public void Dispose()
    {
        _iconCapture.Dispose();
        _serverClient.Dispose();
        AddonLifecycle.UnregisterListener(OnMahjongDraw);
        AddonLifecycle.UnregisterListener(OnMahjongFinalize);
        Framework.Update -= OnFrameworkUpdate;
        // Unregister all actions to not leak anything during disposal of plugin
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        
        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();
        OverlayWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        var lower = trimmed.ToLowerInvariant();

        if (lower == "overlay")
        {
            Configuration.OverlayVisible = !Configuration.OverlayVisible;
            Configuration.Save();
        }
        else if (lower == "compact")
        {
            Configuration.OverlayCompactMode = !Configuration.OverlayCompactMode;
            Configuration.Save();
        }
        else if (lower == "auto" || lower == "autoplay")
        {
            Configuration.AutoPlayEnabled = !Configuration.AutoPlayEnabled;
            Configuration.Save();
            if (!Configuration.AutoPlayEnabled)
                _autoPlayManager.ClearPending();
        }
        else if (lower == "pause")
        {
            _autoPlayManager.TogglePause();
        }
        else if (lower == "mark discard")
        {
            AnnotateComparisonEvent("manual_discard", "User manually discarded a tile");
            AppendRecentTransition($"{DateTime.UtcNow:O} manual mark: discard");
        }
        else if (lower == "mark call")
        {
            AnnotateComparisonEvent("manual_call", "User manually accepted/declined a call");
            AppendRecentTransition($"{DateTime.UtcNow:O} manual mark: call");
        }
        else if (lower.StartsWith("probecallback "))
        {
            // Usage: /mj probecallback <a> <b> [run]
            var parts = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && int.TryParse(parts[1], out var a) && int.TryParse(parts[2], out var b))
            {
                var execute = parts.Length >= 4 && parts[3] == "run";
                unsafe
                {
                    var addonPtr = _lastAddonAddress != 0 ? (AtkUnitBase*)_lastAddonAddress : null;
                    AddonClickHelper.TryFireProbeCallbackEx(addonPtr, 0, new[] { a, b }, execute);
                }
                AppendRecentTransition($"{DateTime.UtcNow:O} probe callback a={a} b={b} execute={execute}");
            }
            else
            {
                AppendRecentTransition($"{DateTime.UtcNow:O} invalid probecallback args: '{trimmed}'");
            }
        }
        else if (lower.StartsWith("firecb "))
        {
            // Usage: /mj firecb <val1> <val2> ... [run]
            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                var values = new List<int>();
                var execute = false;
                for (int i = 1; i < parts.Length; i++)
                {
                    if (parts[i].ToLowerInvariant() == "run")
                    {
                        execute = true;
                        break;
                    }
                    if (int.TryParse(parts[i], out var val))
                        values.Add(val);
                }

                if (values.Count > 0)
                {
                    unsafe
                    {
                        var addonPtr = _lastAddonAddress != 0 ? (AtkUnitBase*)_lastAddonAddress : null;
                        AddonClickHelper.TryFireProbeCallbackEx(addonPtr, 0, values.ToArray(), execute);
                    }
                    AppendRecentTransition($"{DateTime.UtcNow:O} firecb values=[{string.Join(",", values)}] execute={execute}");
                }
                else
                {
                    AppendRecentTransition($"{DateTime.UtcNow:O} invalid firecb args: no values");
                }
            }
            else
            {
                AppendRecentTransition($"{DateTime.UtcNow:O} invalid firecb args: '{trimmed}'");
            }
        }
        else if (lower.StartsWith("callsweep"))
        {
            // Usage: /mj callsweep [run]
            // Sweeps callback IDs 0-15 with second values 0-5 during a call prompt.
            // Dry-run by default (logs what would be tried). Add "run" to execute one-at-a-time.
            // Only safe to run DURING an active call prompt (AtkValues[0]=6).
            var parts = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var execute = parts.Length >= 2 && parts[1] == "run";

            unsafe
            {
                var addonPtr = _lastAddonAddress != 0 ? (AtkUnitBase*)_lastAddonAddress : null;
                if (addonPtr == null)
                {
                    AppendRecentTransition($"{DateTime.UtcNow:O} callsweep: no addon");
                }
                else
                {
                    // Check if we're actually in a call prompt
                    var atkVal0 = addonPtr->AtkValuesCount > 0 ? addonPtr->AtkValues[0].Int : -1;
                    AddonClickHelper.LogAtkSnapshot(addonPtr, "callsweep-pre");

                    if (!execute)
                    {
                        // Dry-run: just log current state
                        AppendRecentTransition($"{DateTime.UtcNow:O} callsweep DRY-RUN atkVal[0]={atkVal0} — use '/mj callsweep run' during a call prompt to execute");
                        AddonClickHelper.LogCallSweepDryRun(addonPtr);
                    }
                    else
                    {
                        // Execute: try ONE callback (the next untried one) and log result
                        // Reads the sweep progress from the log to determine which to try next
                        AppendRecentTransition($"{DateTime.UtcNow:O} callsweep EXECUTE atkVal[0]={atkVal0}");
                        AddonClickHelper.ExecuteNextCallSweepProbe(addonPtr);
                    }
                }
            }
        }
        else if (lower == "acceptcall" || lower.StartsWith("acceptcall "))
        {
            // Usage: /mj acceptcall [chi|pon|kan|ron|tsumo|riichi]
            // Accepts the highest-priority (or specified) call using the working ListItemClick method.
            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            EmjUiReader.CallOptions? targetCall = null;
            if (parts.Length >= 2)
            {
                var p = parts[1].ToLowerInvariant();
                if (p == "pon") targetCall = EmjUiReader.CallOptions.Pon;
                else if (p == "chi") targetCall = EmjUiReader.CallOptions.Chi;
                else if (p == "kan") targetCall = EmjUiReader.CallOptions.Kan;
                else if (p == "ron") targetCall = EmjUiReader.CallOptions.Ron;
                else if (p == "tsumo") targetCall = EmjUiReader.CallOptions.Tsumo;
                else if (p == "riichi") targetCall = EmjUiReader.CallOptions.Riichi;
            }

            var buttonNodes = _lastUiState?.GameInfo?.CallButtonNodes;
            if (buttonNodes == null || buttonNodes.Count == 0)
            {
                var msg = $"{DateTime.UtcNow:O} acceptcall: no call button nodes captured (is a call prompt visible?)";
                AppendRecentTransition(msg);
                LogToFile("autoplay.log", msg);
            }
            else
            {
                unsafe
                {
                    var addonPtr = _lastAddonAddress != 0 ? (AtkUnitBase*)_lastAddonAddress : null;
                    if (addonPtr == null)
                    {
                        var msg = $"{DateTime.UtcNow:O} acceptcall: no addon";
                        AppendRecentTransition(msg);
                        LogToFile("autoplay.log", msg);
                    }
                    else
                    {
                        nint btnPtr = 0;
                        string callName = "?";
                        if (targetCall.HasValue && buttonNodes.TryGetValue(targetCall.Value, out var specificPtr))
                        {
                            btnPtr = specificPtr;
                            callName = targetCall.Value.ToString();
                        }
                        else if (!targetCall.HasValue)
                        {
                            var priority = new[] {
                                EmjUiReader.CallOptions.Ron, EmjUiReader.CallOptions.Tsumo,
                                EmjUiReader.CallOptions.Kan, EmjUiReader.CallOptions.Pon,
                                EmjUiReader.CallOptions.Chi, EmjUiReader.CallOptions.Riichi,
                            };
                            foreach (var c in priority)
                            {
                                if (buttonNodes.TryGetValue(c, out var p) && p != 0)
                                {
                                    btnPtr = p;
                                    callName = c.ToString();
                                    break;
                                }
                            }
                        }

                        if (btnPtr != 0)
                        {
                            var result = AddonClickHelper.TryAcceptCallViaListClick(addonPtr, btnPtr, callName);
                            var msg = $"{DateTime.UtcNow:O} acceptcall {callName} ptr={btnPtr:X} result={result}";
                            AppendRecentTransition(msg);
                            LogToFile("autoplay.log", msg);
                        }
                        else
                        {
                            var msg = $"{DateTime.UtcNow:O} acceptcall: target call '{targetCall}' not found in captured buttons";
                            AppendRecentTransition(msg);
                            LogToFile("autoplay.log", msg);
                        }
                    }
                }
            }
        }
        else if (lower.StartsWith("clickcall"))
        {
            // Usage: /mj clickcall [pon|chi|kan|ron|tsumo|riichi] [method] [run]
            // 'run' is required for methods 1-8 (click actions). Method 0 (diagnostic) always runs.
            // Methods: 0=diagnostic, 1-5=legacy, 6=ECommons-style, 7=replay-all-events, 8=parent-chain-events
            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            EmjUiReader.CallOptions? targetCall = null;
            var method = 0;
            var execute = false;

            for (int i = 1; i < parts.Length; i++)
            {
                var p = parts[i].ToLowerInvariant();
                if (p == "run") execute = true;
                else if (int.TryParse(p, out var m)) method = m;
                else if (p == "pon") targetCall = EmjUiReader.CallOptions.Pon;
                else if (p == "chi") targetCall = EmjUiReader.CallOptions.Chi;
                else if (p == "kan") targetCall = EmjUiReader.CallOptions.Kan;
                else if (p == "ron") targetCall = EmjUiReader.CallOptions.Ron;
                else if (p == "tsumo") targetCall = EmjUiReader.CallOptions.Tsumo;
                else if (p == "riichi") targetCall = EmjUiReader.CallOptions.Riichi;
            }

            // Method 0 (diagnostic) always executes even without 'run'
            if (method == 0) execute = true;

            var buttonNodes = _lastUiState?.GameInfo?.CallButtonNodes;
            if (buttonNodes == null || buttonNodes.Count == 0)
            {
                var msg = $"{DateTime.UtcNow:O} clickcall: no call button nodes captured (is a call prompt visible?)";
                AppendRecentTransition(msg);
                LogToFile("autoplay.log", msg);
            }
            else if (!execute)
            {
                // Dry run: log available buttons
                foreach (var kvp in buttonNodes)
                {
                    var msg = $"{DateTime.UtcNow:O} clickcall DRY-RUN: {kvp.Key} ptr={kvp.Value:X}";
                    AppendRecentTransition(msg);
                    LogToFile("autoplay.log", msg);
                }
                var hint = $"{DateTime.UtcNow:O} clickcall: use '/mj clickcall [type] [method] run' to execute (method 0=diag always runs)";
                AppendRecentTransition(hint);
                LogToFile("autoplay.log", hint);
            }
            else
            {
                unsafe
                {
                    var addonPtr = _lastAddonAddress != 0 ? (AtkUnitBase*)_lastAddonAddress : null;
                    if (addonPtr == null)
                    {
                        var msg = $"{DateTime.UtcNow:O} clickcall: no addon";
                        AppendRecentTransition(msg);
                        LogToFile("autoplay.log", msg);
                    }
                    else
                    {
                        // Pick the target call (specified or highest-priority available)
                        nint btnPtr = 0;
                        string callName = "?";
                        if (targetCall.HasValue && buttonNodes.TryGetValue(targetCall.Value, out var specificPtr))
                        {
                            btnPtr = specificPtr;
                            callName = targetCall.Value.ToString();
                        }
                        else if (!targetCall.HasValue)
                        {
                            // Auto: highest priority
                            var priority = new[] {
                                EmjUiReader.CallOptions.Ron, EmjUiReader.CallOptions.Tsumo,
                                EmjUiReader.CallOptions.Kan, EmjUiReader.CallOptions.Pon,
                                EmjUiReader.CallOptions.Chi, EmjUiReader.CallOptions.Riichi,
                            };
                            foreach (var c in priority)
                            {
                                if (buttonNodes.TryGetValue(c, out var p) && p != 0)
                                {
                                    btnPtr = p;
                                    callName = c.ToString();
                                    break;
                                }
                            }
                        }

                        if (btnPtr != 0)
                        {
                            var result = AddonClickHelper.TryAcceptCallViaListClick(addonPtr, btnPtr, callName);
                            var msg = $"{DateTime.UtcNow:O} clickcall EXECUTE {callName} via ListItemClick result={result}";
                            AppendRecentTransition(msg);
                            LogToFile("autoplay.log", msg);
                        }
                        else
                        {
                            var msg = $"{DateTime.UtcNow:O} clickcall: target call '{targetCall}' not found in captured buttons";
                            AppendRecentTransition(msg);
                            LogToFile("autoplay.log", msg);
                        }
                    }
                }
            }
        }
        else if (lower.StartsWith("clicktile "))
        {
            // Usage: /mj clicktile <nodeIndex> [method] [run]
            // method: 0=all, 1=comp listener, 2=addon recv, 3=event 0x17, 4=callback, 5=event 0x09
            var parts = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && int.TryParse(parts[1], out var nodeIndex))
            {
                var method = 0;
                var execute = false;
                for (int i = 2; i < parts.Length; i++)
                {
                    if (parts[i] == "run") execute = true;
                    else if (int.TryParse(parts[i], out var m)) method = m;
                }
                unsafe
                {
                    var addonPtr = _lastAddonAddress != 0 ? (AtkUnitBase*)_lastAddonAddress : null;
                    AddonClickHelper.TryClickTileNode(addonPtr, nodeIndex, execute, method);
                }
                AppendRecentTransition($"{DateTime.UtcNow:O} click tile node={nodeIndex} method={method} execute={execute}");
            }
            else
            {
                AppendRecentTransition($"{DateTime.UtcNow:O} invalid clicktile args: '{trimmed}'");
            }
        }
        else
        {
            // Default: toggle main debug window
            MainWindow.Toggle();
        }
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();
}
