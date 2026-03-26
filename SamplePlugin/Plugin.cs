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
    private bool _startupDumpDone = false;
    private IconIdCapture _iconCapture = null!;
    private MahjongIconMap _iconMap = null!;
    private HashSet<uint> _lastEligibleIconIds = new();

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

        // You might normally want to embed resources and load them from the manifest stream
        var goatImagePath = Path.Combine(PluginInterface.AssemblyLocation.Directory?.FullName!, "goat.png");

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this, goatImagePath);

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
            var eligibleIconIds = BuildEligibleIconSet(handSnapshot);
            _lastEligibleIconIds = eligibleIconIds;
            _iconMap.ObserveHover(addon, eligibleIconIds);
            MainWindow.mappingReport = _iconMap.BuildProgressReport(_iconCapture.IconMap.Values);

            Log.Information("EmjL already open on plugin load — dumping immediately");
            var dumpContent = TileDataDumper.DumpAndSave(addon, _iconCapture, _iconMap);
            MainWindow.text = dumpContent;
            MainWindow.text2 = handSnapshot.ToDisplayText();
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

        // Learn hover pairs every frame using the latest known eligible icon set.
        _iconMap.ObserveHover(addon, _lastEligibleIconIds);

        // IMPORTANT: throttle so you don’t dump every frame
        // But always dump on first draw after plugin load
        var now = DateTime.UtcNow;
        if (_startupDumpDone && now < _nextDumpAtUtc)
            return;

        _startupDumpDone = true;
        _nextDumpAtUtc = now.AddSeconds(3); // dump every 3 seconds

        // Write structured tile data to file + display in UI
        try
        {
            var handSnapshot = MahjongHandReader.Read(addon, _iconCapture, _iconMap);
            var eligibleIconIds = BuildEligibleIconSet(handSnapshot);
            _lastEligibleIconIds = eligibleIconIds;
            _iconMap.ObserveHover(addon, eligibleIconIds);
            var dumpContent = TileDataDumper.DumpAndSave(addon, _iconCapture, _iconMap);
            MainWindow.text = dumpContent;
            MainWindow.text2 = handSnapshot.ToDisplayText();
            MainWindow.mappingReport = _iconMap.BuildProgressReport(_iconCapture.IconMap.Values);
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
