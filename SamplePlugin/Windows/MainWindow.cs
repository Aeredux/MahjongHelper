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

        ImGui.Separator();
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
            new System.Numerics.Vector2(-1, 180),
            ImGuiInputTextFlags.ReadOnly
        );

        ImGui.Separator();
        ImGui.Text("Mahjong UI State (slot-based scaffold)");
        ImGui.InputTextMultiline(
            "##mahjongUiState",
            ref text3,
            400_000,
            new System.Numerics.Vector2(-1, 180),
            ImGuiInputTextFlags.ReadOnly
        );

        ImGui.Separator();
        ImGui.Text("Raw Dump");
        ImGui.InputTextMultiline(
            "##mahjongDump",
            ref text,
            2_000_000, // max chars
            new System.Numerics.Vector2(-1, 220),
            ImGuiInputTextFlags.ReadOnly
        );

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
}
