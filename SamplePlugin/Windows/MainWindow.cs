using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace SamplePlugin.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    public static String text = "default";
    public static String text2 = "default";
    public static String text3 = "Captured: (none)\nPlayer hand slots: 0\nPlayer draw slot:\n  (missing)\nVisible tile candidates: 0";
    public static String mappingReport = "Mapping report pending...";
    public static String handIconIds = "(none)";
    public static String readerStatus = "AddonNotFound";
    public static String normalizedStateText = "Captured: (none)\nNormalized Mahjong State\nAgentState: (missing) [src=Unknown, non-authoritative]";
    public static String diagnosticsText = "Phase C Diagnostics\nReaderStatus: AddonNotFound";
    public static String recentTransitionsText = "(no normalized transitions yet)";
    public static String serverSuggestionText = "(no suggestion yet)";
    public static String serverStatusText = "Not checked";
    private string resetIconIdInput = "";
    public bool RequestTileDump;

    // We give this window a hidden ID using ##.
    // The user will see "My Amazing Window" as window title,
    // but for ImGui the ID is "My Amazing Window##With a hidden ID"
    public MainWindow(Plugin plugin)
        : base("My Amazing Window##With a hidden ID")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(375, 330),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        if (ImGui.Button("Dump Likely Tiles"))
        {
            ImGui.SetClipboardText(text2 ?? "");
        }

        ImGui.SameLine();
        if (ImGui.Button("Copy to Clipboard"))
        {
            ImGui.SetClipboardText(text ?? "");
        }

        ImGui.SameLine();
        if (ImGui.Button("Copy Client Probes"))
        {
            var probes = ExtractClientProbeSection(text);
            ImGui.SetClipboardText(string.IsNullOrEmpty(probes) ? text ?? "" : probes);
        }

        ImGui.SameLine();
        if (ImGui.Button("Copy Mahjong UI State"))
        {
            ImGui.SetClipboardText(text3 ?? "");
        }

        ImGui.SameLine();
        if (ImGui.Button("Copy Mapping Report"))
        {
            ImGui.SetClipboardText(mappingReport ?? "");
        }

        ImGui.SameLine();
        if (ImGui.Button("Export Normalized State"))
        {
            plugin.ExportNormalizedState();
        }

        ImGui.SameLine();
        if (ImGui.Button("Copy Recent Transitions"))
        {
            ImGui.SetClipboardText(plugin.GetRecentTransitionsText());
        }

        ImGui.SameLine();
        if (ImGui.Button("Export Probe Snippet"))
        {
            plugin.ExportProbeSnippet(text);
        }

        // Start a new row to avoid toolbar buttons going off-screen.
        ImGui.NewLine();
        if (ImGui.Button("Export Mapping Report"))
        {
            plugin.ExportMappingReport();
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset Learned Mappings"))
        {
            plugin.ResetLearnedMappings();
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset All Data"))
        {
            plugin.ResetAllData();
        }

        ImGui.SameLine();
        if (ImGui.Button("Copy Comparison Log"))
        {
            try
            {
                var logPath = SamplePlugin.Mahjong.StateComparisonLogger.GetLogPath();
                if (System.IO.File.Exists(logPath))
                {
                    var content = System.IO.File.ReadAllText(logPath);
                    ImGui.SetClipboardText(content);
                }
            }
            catch { }
        }

        ImGui.SameLine();
        if (ImGui.Button("Log: Tile Drawn"))
        {
            plugin.AnnotateComparisonEvent("tile_drawn", "Tile was drawn from wall");
        }

        ImGui.SameLine();
        if (ImGui.Button("Log: Tile Discarded"))
        {
            plugin.AnnotateComparisonEvent("tile_discarded", "Player discarded a tile");
        }

        ImGui.SameLine();
        if (ImGui.Button("Copy Hand Icon IDs"))
        {
            ImGui.SetClipboardText(handIconIds ?? "(none)");
        }

        ImGui.SameLine();
        if (ImGui.Button("Copy Discard/Dora"))
        {
            ImGui.SetClipboardText(ExtractDiscardDoraSection(normalizedStateText));
        }

        ImGui.Separator();
        ImGui.Text($"Reader Status: {readerStatus}");
        ImGui.Text($"Server: {serverStatusText}");

        // Tab bar for organizing content
        if (ImGui.BeginTabBar("DebugTabs", ImGuiTabBarFlags.None))
        {
            if (ImGui.BeginTabItem("Server"))
            {
                ImGui.Text("Server Suggestion");
                ImGui.InputTextMultiline(
                    "##serverSuggestion",
                    ref serverSuggestionText,
                    50_000,
                    new System.Numerics.Vector2(-1, 300),
                    ImGuiInputTextFlags.ReadOnly
                );
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Mapping"))
            {
                ImGui.Text("Mapping Progress");
                ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
                ImGui.InputText("##resetIconIdInput", ref resetIconIdInput, 32);
                ImGui.SameLine();
                if (ImGui.Button("Reset Icon ID") && uint.TryParse(resetIconIdInput, out var iconId))
                {
                    if (!plugin.ResetLearnedMapping(iconId))
                        mappingReport = $"Reset failed for icon {iconId}. It may be locked or unknown." + Environment.NewLine + Environment.NewLine + mappingReport;
                    else
                        resetIconIdInput = "";
                }
                ImGui.InputTextMultiline(
                    "##mappingProgress",
                    ref mappingReport,
                    200_000,
                    new System.Numerics.Vector2(-1, 400),
                    ImGuiInputTextFlags.ReadOnly
                );
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("UI State"))
            {
                ImGui.Text("Mahjong UI State (slot-based scaffold)");
                ImGui.InputTextMultiline(
                    "##mahjongUiState",
                    ref text3,
                    400_000,
                    new System.Numerics.Vector2(-1, 400),
                    ImGuiInputTextFlags.ReadOnly
                );
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Normalized"))
            {
                ImGui.Text("Normalized Mahjong State (probe + node + cache)");
                ImGui.InputTextMultiline(
                    "##mahjongNormalizedState",
                    ref normalizedStateText,
                    200_000,
                    new System.Numerics.Vector2(-1, 400),
                    ImGuiInputTextFlags.ReadOnly
                );
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Diagnostics"))
            {
                ImGui.Text("Diagnostics");
                ImGui.InputTextMultiline(
                    "##mahjongDiagnostics",
                    ref diagnosticsText,
                    120_000,
                    new System.Numerics.Vector2(-1, 400),
                    ImGuiInputTextFlags.ReadOnly
                );
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Transitions"))
            {
                ImGui.Text("Recent Normalized Transitions");
                ImGui.InputTextMultiline(
                    "##mahjongRecentTransitions",
                    ref recentTransitionsText,
                    160_000,
                    new System.Numerics.Vector2(-1, 400),
                    ImGuiInputTextFlags.ReadOnly
                );
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Raw Dump"))
            {
                ImGui.Text("Raw Dump");
                ImGui.InputTextMultiline(
                    "##mahjongDump",
                    ref text,
                    2_000_000,
                    new System.Numerics.Vector2(-1, 400),
                    ImGuiInputTextFlags.ReadOnly
                );
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        ImGui.Separator();
        if (ImGui.Button("Show Settings"))
        {
            plugin.ToggleConfigUi();
        }
    }

    private static string ExtractClientProbeSection(string? dumpText)
    {
        if (string.IsNullOrWhiteSpace(dumpText))
            return string.Empty;

        const string startMarker = "--- CLIENT MAHJONG STATE PROBES ---";
        const string endMarker = "--- FULL NODE LIST ---";

        var startIndex = dumpText.IndexOf(startMarker, StringComparison.Ordinal);
        if (startIndex < 0)
            return string.Empty;

        var endIndex = dumpText.IndexOf(endMarker, startIndex + startMarker.Length, StringComparison.Ordinal);
        if (endIndex < 0)
            endIndex = dumpText.Length;

        return dumpText.Substring(startIndex, endIndex - startIndex).TrimEnd();
    }

    private static string ExtractDiscardDoraSection(string? normalizedText)
    {
        if (string.IsNullOrWhiteSpace(normalizedText))
            return "(no normalized state)";

        var sb = new System.Text.StringBuilder();
        foreach (var line in normalizedText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            if (line.StartsWith("PlayerDiscards:", StringComparison.Ordinal) ||
                line.StartsWith("RightDiscards:", StringComparison.Ordinal) ||
                line.StartsWith("OppositeDiscards:", StringComparison.Ordinal) ||
                line.StartsWith("LeftDiscards:", StringComparison.Ordinal) ||
                line.StartsWith("DoraIndicators:", StringComparison.Ordinal))
            {
                sb.AppendLine(line);
            }
        }

        return sb.Length > 0 ? sb.ToString().TrimEnd() : "(no discard/dora data)";
    }
}
