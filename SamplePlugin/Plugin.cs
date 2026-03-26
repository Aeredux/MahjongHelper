using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using System.IO;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using SamplePlugin.Windows;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui;
using FFXIVClientStructs.FFXIV.Component.GUI;
using SamplePlugin.Mahjong;
using System;
using System.Linq;
using System.Collections.Generic;

namespace SamplePlugin;

public sealed class Plugin : IDalamudPlugin
{
    private static readonly string CacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MahjongHelper");
    private static readonly string MappingReportPath = Path.Combine(CacheDirectory, "mapping_progress_report.txt");
    private static readonly string ProbeHistoryPath = Path.Combine(CacheDirectory, "probe_history.log");
    private static readonly string ProbeSignalsPath = Path.Combine(CacheDirectory, "probe_signals.log");
    private static readonly string TileCandidatesPath = Path.Combine(CacheDirectory, "tile_candidates.log");
    private static readonly string UiStateHistoryPath = Path.Combine(CacheDirectory, "mahjong_ui_state_history.log");

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
    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("SamplePlugin");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }
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

    public Plugin()
    {
        _iconCapture = new IconIdCapture(GameInterop);
        _iconMap = new MahjongIconMap();

        AddonLifecycle.RegisterListener(
            AddonEvent.PreDraw,
            "EmjL", // temporary, we’ll fix name later
            OnMahjongDraw
        );
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "A useful message to display in /xlhelp"
        });

        // Tell the UI system that we want our windows to be drawn through the window system
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;

        // This adds a button to the plugin installer entry of this plugin which allows
        // toggling the display status of the configuration ui
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

        // Adds another button doing the same but for the main ui of the plugin
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        // Add a simple message to the log with level set to information
        // Use /xllog to open the log window in-game
        // Example Output: 00:57:54.959 | INF | [SamplePlugin] ===A cool log message from Sample Plugin===
        Log.Information($"===A cool log message from {PluginInterface.Manifest.Name}===");

        // If EmjL addon is already open (player is in a mahjong game), dump immediately on load
        TryImmediateDump();
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
            TrackUiState(uiState, "startup");
            _startupDumpDone = true;
            _nextDumpAtUtc = DateTime.UtcNow.AddSeconds(3);
        }
        catch (Exception ex)
        {
            Log.Error($"Startup dump failed: {ex.Message}");
        }
    }

    private unsafe void OnMahjongDraw(AddonEvent type, AddonArgs args)
    {
        var addonPtr = args.Addon; // AtkUnitBasePtr
        if (addonPtr.IsNull)
        {
            MainWindow.text = "EmjL addonPtr is null";
            return;
        }

        var addon = (AtkUnitBase*)addonPtr.Address;

        // (Optional) quick safety checks
        if (addon == null || addon->RootNode == null)
        {
            MainWindow.text = "addon/root is null";
            return;
        }

        // Hover-learning disabled: unreliable. Pivoting to memory probing.
        // _iconMap.ObserveHover(addon, _lastEligibleIconIds);

        var now = DateTime.UtcNow;

        // Lightweight UI-state sampling can run faster than full dump cadence.
        if (now >= _nextUiStateCaptureAtUtc)
        {
            try
            {
                var uiStateFast = EmjUiReader.Read(addon, _iconCapture, _iconMap);
                MainWindow.text3 = uiStateFast.ToDisplayText();
                TrackUiState(uiStateFast, "realtime");
            }
            catch
            {
                // Never disrupt gameplay from diagnostics.
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
            TrackUiState(uiState, "periodic");
        }
        catch (Exception ex)
        {
            MainWindow.text = $"Dump failed: {ex.Message}";
        }
    }

    public void ExportMappingReport()
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            var report = _iconMap.BuildProgressReport(_iconCapture.IconMap.Values);
            File.WriteAllText(MappingReportPath, report);
            MainWindow.mappingReport = report + Environment.NewLine + $"Exported to: {MappingReportPath}";
        }
        catch (Exception ex)
        {
            MainWindow.mappingReport = $"Export failed: {ex.Message}";
        }
    }

    public void ResetLearnedMappings()
    {
        _iconMap.ResetLearnedMappings();
        MainWindow.mappingReport = _iconMap.BuildProgressReport(_iconCapture.IconMap.Values);
    }

    public bool ResetLearnedMapping(uint iconId)
    {
        var removed = _iconMap.ResetLearnedMapping(iconId);
        MainWindow.mappingReport = _iconMap.BuildProgressReport(_iconCapture.IconMap.Values);
        return removed;
    }

    public void ResetAllData()
    {
        _iconMap.ResetLearnedMappings();
        _iconCapture.ResetCapturedIcons();
        MahjongHandReader.ResetCachedSnapshot();
        _lastEligibleIconIds.Clear();

        try
        {
            if (File.Exists(MappingReportPath))
                File.Delete(MappingReportPath);
        }
        catch
        {
        }

        MainWindow.text = "All MahjongHelper cached data cleared.";
        MainWindow.text2 = "Mahjong Hand Snapshot\nHand tiles: 0\nDrawn: (none)";
        MainWindow.text3 = "Captured: (none)\nCanonical player hand slots: 0\nCanonical player draw slot:\n  (missing)\nPlayer hand slots: 0\nPlayer draw slot:\n  (missing)\nVisible tile candidates: 0";
        MainWindow.mappingReport = _iconMap.BuildProgressReport(Array.Empty<uint>());
    }

    private static HashSet<uint> BuildEligibleIconSet(MahjongHandReader.MahjongHandSnapshot snapshot)
    {
        var set = new HashSet<uint>();
        foreach (var tile in snapshot.HandTiles)
        {
            if (tile.IconId > 0)
                set.Add(tile.IconId);
        }

        if (snapshot.DrawnTile != null && snapshot.DrawnTile.IconId > 0)
            set.Add(snapshot.DrawnTile.IconId);

        return set;
    }

    private void TrackProbeState(string? dumpContent)
    {
        var probeSection = ExtractProbeSection(dumpContent);
        if (string.IsNullOrWhiteSpace(probeSection))
            return;

        var signature = NormalizeProbeSection(probeSection);
        if (signature == _lastProbeStateSignature)
            return;

        _lastProbeStateSignature = signature;

        try
        {
            Directory.CreateDirectory(CacheDirectory);
            var entry =
                $"=== Probe State Change {DateTime.UtcNow:O} ==={Environment.NewLine}" +
                probeSection + Environment.NewLine +
                "========================================" + Environment.NewLine + Environment.NewLine;
            File.AppendAllText(ProbeHistoryPath, entry);

            var emj28State = TryGetAgentEmj28State(probeSection);
            if (emj28State.HasValue && emj28State != _lastAgentEmj28State)
            {
                var previous = _lastAgentEmj28State.HasValue ? _lastAgentEmj28State.Value.ToString() : "(none)";
                var signal = $"{DateTime.UtcNow:O} AgentId.Emj+0x28/+0x08 i32 changed: {previous} -> {emj28State.Value}";
                File.AppendAllText(ProbeSignalsPath, signal + Environment.NewLine);
                _lastAgentEmj28State = emj28State;
            }

            var emj28Bytes = TryExtractAgentEmj28Bytes(probeSection);
            if (emj28Bytes != null)
                TrackTileCandidates(emj28Bytes, emj28State);
        }
        catch
        {
            // Never break gameplay flow due to probe logging.
        }
    }

    private static string ExtractProbeSection(string? dumpContent)
    {
        if (string.IsNullOrWhiteSpace(dumpContent))
            return string.Empty;

        const string startMarker = "--- CLIENT MAHJONG STATE PROBES ---";
        const string endMarker = "--- FULL NODE LIST ---";

        var startIndex = dumpContent.IndexOf(startMarker, StringComparison.Ordinal);
        if (startIndex < 0)
            return string.Empty;

        var endIndex = dumpContent.IndexOf(endMarker, startIndex + startMarker.Length, StringComparison.Ordinal);
        if (endIndex < 0)
            endIndex = dumpContent.Length;

        return dumpContent.Substring(startIndex, endIndex - startIndex).TrimEnd();
    }

    private static string NormalizeProbeSection(string probeSection)
    {
        var lines = probeSection.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var filtered = lines.Where(static line =>
            !line.StartsWith("  ProbeSequence=", StringComparison.Ordinal) &&
            !line.StartsWith("  ProbeUtcTimestamp=", StringComparison.Ordinal) &&
            !line.StartsWith("  ProbeTickCount64=", StringComparison.Ordinal));

        return string.Join("\n", filtered).Trim();
    }

    private static int? TryGetAgentEmj28State(string probeSection)
    {
        var lines = probeSection.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        bool inEmj28Block = false;

        foreach (var line in lines)
        {
            if (line.StartsWith("  AgentId.Emj+0x28 @", StringComparison.Ordinal))
            {
                inEmj28Block = true;
                continue;
            }

            if (inEmj28Block && line.StartsWith("  AgentId.Emj+0x", StringComparison.Ordinal))
                break;

            if (!inEmj28Block)
                continue;

            const string marker = "+0x08: i32=";
            var markerIndex = line.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
                continue;

            var valueStart = markerIndex + marker.Length;
            var valueEnd = line.IndexOf(' ', valueStart);
            if (valueEnd < 0)
                valueEnd = line.Length;

            var token = line.Substring(valueStart, valueEnd - valueStart).Trim();
            if (int.TryParse(token, out var parsed))
                return parsed;
        }

        return null;
    }

    private static byte[]? TryExtractAgentEmj28Bytes(string probeSection)
    {
        var lines = probeSection.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        bool inEmj28Block = false;
        byte[] bytes = new byte[0x200];
        bool sawAnyBytes = false;

        foreach (var line in lines)
        {
            if (line.StartsWith("  AgentId.Emj+0x28 @", StringComparison.Ordinal))
            {
                inEmj28Block = true;
                continue;
            }

            if (!inEmj28Block)
                continue;

            if (line.StartsWith("    dwords:", StringComparison.Ordinal) ||
                line.StartsWith("  AgentId.Emj+0x", StringComparison.Ordinal))
            {
                break;
            }

            if (!line.StartsWith("    +", StringComparison.Ordinal))
                continue;

            var colon = line.IndexOf(':');
            if (colon < 0)
                continue;

            var offsetToken = line.Substring(5, colon - 5).Trim();
            if (!int.TryParse(offsetToken, System.Globalization.NumberStyles.HexNumber, null, out var baseOffset))
                continue;

            var bar = line.IndexOf('|', colon + 1);
            var hexSegment = (bar > colon ? line.Substring(colon + 1, bar - colon - 1) : line[(colon + 1)..]).Trim();
            if (string.IsNullOrWhiteSpace(hexSegment))
                continue;

            var groups = hexSegment.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var byteOffset = baseOffset;
            foreach (var group in groups)
            {
                if (group.Length % 2 != 0)
                    continue;

                for (var i = 0; i < group.Length; i += 2)
                {
                    if (byteOffset < 0 || byteOffset >= bytes.Length)
                        break;

                    if (!byte.TryParse(group.Substring(i, 2), System.Globalization.NumberStyles.HexNumber, null, out var value))
                        break;

                    bytes[byteOffset++] = value;
                    sawAnyBytes = true;
                }
            }
        }

        return sawAnyBytes ? bytes : null;
    }

    private void TrackTileCandidates(byte[] emj28Bytes, int? emj28State)
    {
        if (_lastAgentEmj28Bytes == null || _lastAgentEmj28Bytes.Length != emj28Bytes.Length)
        {
            _lastAgentEmj28Bytes = emj28Bytes;
            return;
        }

        var byteChanges = new List<string>();
        var u16Changes = new List<string>();

        // Focus on regions that look data-like instead of pointer-heavy headers.
        CollectByteChanges(_lastAgentEmj28Bytes, emj28Bytes, 0x70, 0x120, byteChanges);
        CollectByteChanges(_lastAgentEmj28Bytes, emj28Bytes, 0x120, 0x1F0, byteChanges);
        CollectU16Changes(_lastAgentEmj28Bytes, emj28Bytes, 0x70, 0x120, u16Changes);

        if (byteChanges.Count == 0 && u16Changes.Count == 0)
        {
            _lastAgentEmj28Bytes = emj28Bytes;
            return;
        }

        var stateText = emj28State.HasValue ? emj28State.Value.ToString() : "unknown";
        var lines = new List<string>
        {
            $"=== Tile Candidate Delta {DateTime.UtcNow:O} state={stateText} ===",
            "Byte changes (filtered 0x01..0x40):"
        };

        lines.AddRange(byteChanges.Take(40));
        if (byteChanges.Count > 40)
            lines.Add($"... {byteChanges.Count - 40} more byte changes omitted");

        lines.Add("u16 changes (filtered <=0x0200):");
        lines.AddRange(u16Changes.Take(30));
        if (u16Changes.Count > 30)
            lines.Add($"... {u16Changes.Count - 30} more u16 changes omitted");

        lines.Add(string.Empty);

        File.AppendAllText(TileCandidatesPath, string.Join(Environment.NewLine, lines));
        _lastAgentEmj28Bytes = emj28Bytes;
    }

    private static void CollectByteChanges(byte[] previous, byte[] current, int start, int end, List<string> output)
    {
        var max = Math.Min(end, Math.Min(previous.Length, current.Length));
        for (var offset = Math.Max(0, start); offset < max; offset++)
        {
            var oldValue = previous[offset];
            var newValue = current[offset];
            if (oldValue == newValue)
                continue;

            // Keep only plausible compact value flips likely to encode tile-ish or count-ish values.
            if (!InCandidateByteRange(oldValue) && !InCandidateByteRange(newValue))
                continue;

            output.Add($"  +0x{offset:X3}: 0x{oldValue:X2} ({oldValue}) -> 0x{newValue:X2} ({newValue})");
        }
    }

    private static void CollectU16Changes(byte[] previous, byte[] current, int start, int end, List<string> output)
    {
        var max = Math.Min(end, Math.Min(previous.Length, current.Length));
        var alignedStart = Math.Max(0, start);
        if ((alignedStart & 1) != 0)
            alignedStart++;

        for (var offset = alignedStart; offset + 1 < max; offset += 2)
        {
            var oldValue = (ushort)(previous[offset] | (previous[offset + 1] << 8));
            var newValue = (ushort)(current[offset] | (current[offset + 1] << 8));
            if (oldValue == newValue)
                continue;

            if (oldValue > 0x0200 && newValue > 0x0200)
                continue;

            output.Add($"  +0x{offset:X3}: {oldValue} (0x{oldValue:X4}) -> {newValue} (0x{newValue:X4})");
        }
    }

    private static bool InCandidateByteRange(byte value)
    {
        return value >= 0x01 && value <= 0x40;
    }

    private void TrackUiState(EmjUiReader.UiState uiState, string source)
    {
        try
        {
            var signature = BuildUiStateSignature(uiState);
            if (signature == _lastUiStateSignature)
                return;

            _lastUiStateSignature = signature;

            Directory.CreateDirectory(CacheDirectory);
            var entry =
                $"=== Mahjong UI State Change {DateTime.UtcNow:O} source={source} ==={Environment.NewLine}" +
                uiState.ToDisplayText() + Environment.NewLine +
                "============================================================" + Environment.NewLine + Environment.NewLine;

            File.AppendAllText(UiStateHistoryPath, entry);
        }
        catch
        {
            // Keep investigation tooling non-intrusive for gameplay.
        }
    }

    private static string BuildUiStateSignature(EmjUiReader.UiState uiState)
    {
        // Omit capture timestamp and keep stable ordering for change detection.
        var lines = uiState.Slots
            .OrderBy(slot => slot.Kind)
            .ThenBy(slot => slot.SlotIndex)
            .ThenBy(slot => slot.NodeIndex)
            .Select(slot =>
                $"{slot.Kind}|{slot.SlotIndex}|{slot.NodeIndex}|{slot.NodeId}|{slot.NodeType}|{slot.Visible}|{slot.X:F0}|{slot.Y:F0}|{slot.Width}|{slot.Height}|{slot.IconId}|{slot.TileCode ?? string.Empty}");

        return string.Join("\n", lines);
    }

    public void Dispose()
    {
        _iconCapture.Dispose();
        AddonLifecycle.UnregisterListener(OnMahjongDraw);
        // Unregister all actions to not leak anything during disposal of plugin
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        
        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        // In response to the slash command, toggle the display status of our main ui
        MainWindow.Toggle();
    }
    
    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();
}
