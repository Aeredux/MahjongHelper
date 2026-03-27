using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using SamplePlugin.Mahjong;

namespace SamplePlugin.Windows;

/// <summary>
/// Compact in-game overlay showing Mahjong AI suggestions.
/// Supports two modes:
///   Compact: single line with best discard + shanten
///   Full: ranked suggestion list with ukeire, confidence, reasoning
/// </summary>
public class SuggestionOverlayWindow : Window, IDisposable
{
    private readonly Configuration configuration;

    // Data fed from Plugin.cs
    public SuggestMoveResponse? LastSuggestion { get; set; }
    public string? ServerStatus { get; set; }
    public string? HandDescription { get; set; }
    public int? CurrentTurnIndex { get; set; } // 0=player
    public string? CallRecommendation { get; set; }
    public bool IsPlayerTurn => CurrentTurnIndex == 0;
    public bool AutoPlayEnabled { get; set; }
    public bool AutoPlayPaused { get; set; }
    public string? PendingAutoAction { get; set; }

    public SuggestionOverlayWindow(Configuration configuration)
        : base("Mahjong Helper##Overlay")
    {
        this.configuration = configuration;

        Flags = ImGuiWindowFlags.NoScrollbar
              | ImGuiWindowFlags.NoScrollWithMouse
              | ImGuiWindowFlags.AlwaysAutoResize;

        Size = new Vector2(280, 0);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(200, 40),
            MaximumSize = new Vector2(500, 800),
        };
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        // Semi-transparent background
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.08f, 0.08f, 0.12f, 0.92f));
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor();
    }

    public override void Draw()
    {
        if (configuration.OverlayCompactMode)
            DrawCompact();
        else
            DrawFull();
    }

    private void DrawCompact()
    {
        var suggestion = LastSuggestion;
        if (suggestion == null || suggestion.Suggestions.Count == 0)
        {
            ImGui.TextColored(Gray, "Waiting for suggestion...");
            return;
        }

        if (!string.IsNullOrEmpty(suggestion.Error))
        {
            ImGui.TextColored(Red, $"Error: {suggestion.Error}");
            return;
        }

        var best = suggestion.Suggestions[0];
        var shantenText = suggestion.Shanten.HasValue ? $"shanten {suggestion.Shanten}" : "";

        // Compact: "Discard P4  shanten 3  ukeire 123"
        ImGui.TextColored(White, "Discard");
        ImGui.SameLine();
        ImGui.TextColored(Gold, best.Tile);
        if (!string.IsNullOrEmpty(shantenText))
        {
            ImGui.SameLine();
            ImGui.TextColored(Cyan, shantenText);
        }
        if (best.Ukeire.HasValue)
        {
            ImGui.SameLine();
            ImGui.TextColored(Green, $"ukeire {best.Ukeire}");
        }

        // Toggle hint
        ImGui.SameLine();
        ImGui.TextColored(Gray, " [+]");
        if (ImGui.IsItemClicked())
            configuration.OverlayCompactMode = false;

        // Call recommendation if present
        DrawCallRecommendation();

        // Auto-play status
        DrawAutoPlayStatus();
    }

    private void DrawFull()
    {
        var suggestion = LastSuggestion;

        // Header: shanten + toggle
        DrawHeader(suggestion);

        ImGui.Separator();

        if (suggestion == null || suggestion.Suggestions.Count == 0)
        {
            ImGui.TextColored(Gray, "No suggestions available.");
            DrawCallRecommendation();
            return;
        }

        if (!string.IsNullOrEmpty(suggestion.Error))
        {
            ImGui.TextColored(Red, suggestion.Error);
            return;
        }

        // Ranked suggestion table
        DrawSuggestionTable(suggestion.Suggestions);

        // Call recommendation if present
        DrawCallRecommendation();

        // Auto-play status
        DrawAutoPlayStatus();
    }

    private void DrawHeader(SuggestMoveResponse? suggestion)
    {
        // Shanten
        if (suggestion?.Shanten != null)
        {
            ImGui.TextColored(White, "Shanten:");
            ImGui.SameLine();
            var shantenColor = suggestion.Shanten.Value switch
            {
                0 => Gold,
                1 => Green,
                2 => Cyan,
                _ => White,
            };
            ImGui.TextColored(shantenColor, suggestion.Shanten.Value.ToString());
        }
        else
        {
            ImGui.TextColored(Gray, "Shanten: ?");
        }

        // Server status (right-aligned area)
        ImGui.SameLine();
        var statusColor = ServerStatus != null && ServerStatus.StartsWith("Connected") ? Green : Red;
        ImGui.TextColored(statusColor, $"  [{(ServerStatus ?? "?")}]");

        // Compact toggle
        ImGui.SameLine();
        ImGui.TextColored(Gray, " [-]");
        if (ImGui.IsItemClicked())
            configuration.OverlayCompactMode = true;
    }

    private static void DrawSuggestionTable(List<DiscardSuggestion> suggestions)
    {
        // Column headers
        ImGui.TextColored(Gray, "Tile     Shanten  Ukeire  Conf");
        ImGui.Separator();

        var maxToShow = Math.Min(suggestions.Count, 8);
        for (int i = 0; i < maxToShow; i++)
        {
            var s = suggestions[i];
            var isTop = i == 0;

            // Tile name (highlighted for top pick)
            var tileColor = isTop ? Gold : White;
            ImGui.TextColored(tileColor, PadRight(s.Tile, 9));

            // Shanten after discard
            ImGui.SameLine();
            ImGui.TextColored(Cyan, PadRight(s.Shanten?.ToString() ?? "?", 9));

            // Ukeire
            ImGui.SameLine();
            var ukeireColor = s.Ukeire switch
            {
                > 80 => Green,
                > 40 => Cyan,
                > 0 => White,
                _ => Gray,
            };
            ImGui.TextColored(ukeireColor, PadRight(s.Ukeire?.ToString() ?? "-", 8));

            // Confidence
            ImGui.SameLine();
            var confText = s.Confidence.HasValue ? $"{s.Confidence:F1}" : "-";
            ImGui.TextColored(White, confText);

            // Reasoning tooltip
            if (!string.IsNullOrEmpty(s.Reasoning) && ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.PushTextWrapPos(300);
                ImGui.TextUnformatted(s.Reasoning);
                ImGui.PopTextWrapPos();
                ImGui.EndTooltip();
            }
        }

        if (suggestions.Count > maxToShow)
            ImGui.TextColored(Gray, $"  ... +{suggestions.Count - maxToShow} more");
    }

    private void DrawCallRecommendation()
    {
        if (string.IsNullOrEmpty(CallRecommendation))
            return;

        ImGui.Separator();
        ImGui.TextColored(Gold, "Call:");
        ImGui.SameLine();
        ImGui.TextWrapped(CallRecommendation);
    }

    private void DrawAutoPlayStatus()
    {
        if (!AutoPlayEnabled) return;

        ImGui.Separator();
        if (AutoPlayPaused)
        {
            ImGui.TextColored(Gray, "Auto-Play: PAUSED");
        }
        else if (!string.IsNullOrEmpty(PendingAutoAction))
        {
            ImGui.TextColored(Gold, $"Auto-Play: {PendingAutoAction}...");
        }
        else
        {
            ImGui.TextColored(Green, "Auto-Play: ON");
        }
    }

    // Color constants
    private static readonly Vector4 White = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 Gray = new(0.6f, 0.6f, 0.6f, 1f);
    private static readonly Vector4 Gold = new(1f, 0.84f, 0f, 1f);
    private static readonly Vector4 Green = new(0.4f, 1f, 0.4f, 1f);
    private static readonly Vector4 Cyan = new(0.4f, 0.9f, 1f, 1f);
    private static readonly Vector4 Red = new(1f, 0.4f, 0.4f, 1f);

    private static string PadRight(string s, int width)
        => s.Length >= width ? s : s + new string(' ', width - s.Length);
}
