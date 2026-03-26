using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;

namespace SamplePlugin.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly string goatImagePath;
    private readonly Plugin plugin;
    public static String text = "default";
    public static String text2 = "default";
    public static String mappingReport = "Mapping report pending...";
    private string resetIconIdInput = "";
    public bool RequestTileDump;

    // We give this window a hidden ID using ##.
    // The user will see "My Amazing Window" as window title,
    // but for ImGui the ID is "My Amazing Window##With a hidden ID"
    public MainWindow(Plugin plugin, string goatImagePath)
        : base("My Amazing Window##With a hidden ID")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(375, 330),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.goatImagePath = goatImagePath;
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

        ImGui.Text($"The random config bool is {plugin.Configuration.SomePropertyToBeSavedAndWithADefault}");

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

        ImGui.Spacing();
        // Normally a BeginChild() would have to be followed by an unconditional EndChild(),
        // ImRaii takes care of this after the scope ends.
        // This works for all ImGui functions that require specific handling, examples are BeginTable() or Indent().
        using (var child = ImRaii.Child("SomeChildWithAScrollbar", Vector2.Zero, true))
        {
            // Check if this child is drawing
            if (child.Success)
            {
                ImGui.Text("Have a goat:");
                var goatImage = Plugin.TextureProvider.GetFromFile(goatImagePath).GetWrapOrDefault();
                if (goatImage != null)
                {
                    using (ImRaii.PushIndent(55f))
                    {
                        ImGui.Image(goatImage.Handle, goatImage.Size);
                    }
                }
                else
                {
                    ImGui.Text("Image not found.");
                }

                ImGuiHelpers.ScaledDummy(20.0f);

                // Example for other services that Dalamud provides.
                // PlayerState provides a wrapper filled with information about the player character.

                var playerState = Plugin.PlayerState;
                if (!playerState.IsLoaded)
                {
                    ImGui.Text("Our local player is currently not logged in.");
                    return;
                }
                
                if (!playerState.ClassJob.IsValid)
                {
                    ImGui.Text("Our current job is currently not valid.");
                    return;
                }
                
                ImGui.AlignTextToFramePadding();
                ImGui.Text($"Current job:");
                
                // Scaling hardcoded pixel values is important, as otherwise users with HUD scales above or below 100%
                // won't be able to see everything.
                ImGui.SameLine(120 * ImGuiHelpers.GlobalScale);
                
                // Get the icon id from a known offset + the class jobs id
                var jobIconId = 62100 + playerState.ClassJob.RowId;
                var iconTexture = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(jobIconId)).GetWrapOrEmpty();
                ImGui.Image(iconTexture.Handle, new Vector2(28, 28) * ImGuiHelpers.GlobalScale);
                
                ImGui.SameLine();
                
                // If you want to see the Macro representation of this SeString use `.ToMacroString()`
                // More info about SeStrings: https://dalamud.dev/plugin-development/sestring/
                ImGui.Text(playerState.ClassJob.Value.Abbreviation.ToString());
                
                ImGui.SameLine();
                ImGui.Text($" [Level {playerState.Level}]");
                
                // Example for querying Lumina, getting the name of our current area.
                var territoryId = Plugin.ClientState.TerritoryType;
                if (Plugin.DataManager.GetExcelSheet<TerritoryType>().TryGetRow(territoryId, out var territoryRow))
                {
                    ImGui.Text($"Current location:");
                    ImGui.SameLine(120 * ImGuiHelpers.GlobalScale);
                    ImGui.Text(territoryRow.PlaceName.Value.Name.ToString());
                }
                else
                {
                    ImGui.Text("Invalid territory.");
                }
            }
        }
    }
}
