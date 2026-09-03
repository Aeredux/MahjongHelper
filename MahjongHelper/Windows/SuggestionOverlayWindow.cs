using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;
using MahjongHelper.Mahjong;

namespace MahjongHelper.Windows;

/// <summary>
/// Native FFXIV suggestion overlay (KamiToolKit NativeAddon).
/// Compact mode is a single best-discard line; full mode shows the ranked list.
/// Appears in Print Screen / vanilla screenshots because it lives in the game UI tree.
/// </summary>
public class SuggestionOverlayWindow : NativeAddon
{
    private readonly Configuration configuration;
    private bool pluginDrivenClose;

    private VerticalListNode? root;
    private HorizontalListNode? headerRow;
    private TextNode? headerText;
    private TextButtonNode? modeButton;
    private HorizontalLineNode? separator;
    private TextNode? bodyText;
    private TextNode? callText;
    private TextNode? autoPlayText;
    private bool renderedCompact = true;

    public SuggestMoveResponse? LastSuggestion { get; set; }
    public string? ServerStatus { get; set; }
    public string? HandDescription { get; set; }
    public int? CurrentTurnIndex { get; set; }
    public string? CallRecommendation { get; set; }
    public bool IsPlayerTurn => CurrentTurnIndex == 0;
    public bool AutoPlayEnabled { get; set; }
    public bool AutoPlayPaused { get; set; }
    public string? PendingAutoAction { get; set; }

    [SetsRequiredMembers]
    public SuggestionOverlayWindow(Configuration configuration)
    {
        this.configuration = configuration;
        InternalName = "MahjongHelperOvl";
        Title = "Suggestions";
        Size = new Vector2(320f, 160f);
        OpenWindowSoundEffectId = 0;
        RespectCloseAll = false;
        DisableCloseTransition = true;
        RememberClosePosition = true;
    }

    /// <summary>
    /// Opens or closes the native addon to match overlay visibility.
    /// Must be called from the game main thread.
    /// </summary>
    public void ApplyVisibility(bool shouldBeVisible)
    {
        if (shouldBeVisible)
        {
            if (!IsOpen)
                Open();
            return;
        }

        if (!IsOpen)
            return;

        pluginDrivenClose = true;
        Close();
    }

    protected override unsafe void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan)
    {
        base.OnSetup(addon, atkValueSpan);

        var width = Math.Max(200f, ContentSize.X);

        root = new VerticalListNode
        {
            Position = ContentStartPosition,
            Width = width,
            ItemSpacing = 4f,
            FitContents = true,
            FitWidth = true,
        };

        headerRow = new HorizontalListNode
        {
            Width = width,
            Height = 28f,
            ItemSpacing = 8f,
            FitHeight = true,
        };

        headerText = NativeUi.Text(width - 96f, 28f, 14);
        modeButton = new TextButtonNode
        {
            Size = new Vector2(88f, 28f),
            String = configuration.OverlayCompactMode ? "Full" : "Compact",
            OnClick = ToggleCompactMode,
        };
        headerRow.AddNode(headerText);
        headerRow.AddNode(modeButton);

        separator = NativeUi.Separator(width);
        bodyText = NativeUi.Text(width, 180f, 12, wrap: true);
        callText = NativeUi.Text(width, 48f, 12, wrap: true);
        callText.TextColor = NativeUi.Gold;
        autoPlayText = NativeUi.Text(width, 22f, 12);

        root.AddNode(headerRow);
        root.AddNode(separator);
        root.AddNode(bodyText);
        root.AddNode(callText);
        root.AddNode(autoPlayText);
        root.AttachNode(this);

        renderedCompact = configuration.OverlayCompactMode;
        RefreshDisplay();
    }

    protected override unsafe void OnUpdate(AtkUnitBase* addon)
    {
        base.OnUpdate(addon);
        RefreshDisplay();
    }

    protected override unsafe void OnHide(AtkUnitBase* addon)
    {
        // Closing via the native X (not /mj overlay or leaving EmjL) should persist hidden.
        if (!pluginDrivenClose)
        {
            configuration.OverlayVisible = false;
            configuration.Save();
        }

        pluginDrivenClose = false;
        base.OnHide(addon);
    }

    protected override unsafe void OnFinalize(AtkUnitBase* addon)
    {
        root = null;
        headerRow = null;
        headerText = null;
        modeButton = null;
        separator = null;
        bodyText = null;
        callText = null;
        autoPlayText = null;
        base.OnFinalize(addon);
    }

    private void ToggleCompactMode()
    {
        configuration.OverlayCompactMode = !configuration.OverlayCompactMode;
        configuration.Save();
        RefreshDisplay();
    }

    private void RefreshDisplay()
    {
        if (root is null || headerText is null || modeButton is null || bodyText is null || callText is null || autoPlayText is null || separator is null)
            return;

        var compact = configuration.OverlayCompactMode;
        if (compact != renderedCompact)
        {
            renderedCompact = compact;
            Size = compact ? new Vector2(320f, 160f) : new Vector2(360f, 380f);
            SetWindowSize(Size);
            root.Width = Math.Max(200f, ContentSize.X);
            if (headerRow is not null)
                headerRow.Width = root.Width;
            bodyText.Width = root.Width;
            callText.Width = root.Width;
            autoPlayText.Width = root.Width;
            separator.Width = root.Width;
        }

        modeButton.String = compact ? "Full" : "Compact";

        var suggestion = LastSuggestion;
        headerText.TextColor = NativeUi.White;

        if (compact)
        {
            separator.IsVisible = false;
            bodyText.IsVisible = false;
            headerText.String = BuildCompactLine(suggestion);
            headerText.TextColor = SuggestionHeaderColor(suggestion);
        }
        else
        {
            separator.IsVisible = true;
            bodyText.IsVisible = true;
            headerText.String = BuildFullHeader(suggestion);
            bodyText.String = BuildFullBody(suggestion);
            bodyText.TextTooltip = suggestion?.Suggestions is { Count: > 0 } list
                ? list[0].Reasoning ?? string.Empty
                : string.Empty;
        }

        if (string.IsNullOrEmpty(CallRecommendation))
        {
            callText.IsVisible = false;
        }
        else
        {
            callText.IsVisible = true;
            callText.String = $"Call: {CallRecommendation}";
        }

        if (!AutoPlayEnabled)
        {
            autoPlayText.IsVisible = false;
        }
        else
        {
            autoPlayText.IsVisible = true;
            if (AutoPlayPaused)
            {
                autoPlayText.TextColor = NativeUi.Gray;
                autoPlayText.String = "Auto-Play: PAUSED";
            }
            else if (!string.IsNullOrEmpty(PendingAutoAction))
            {
                autoPlayText.TextColor = NativeUi.Gold;
                autoPlayText.String = $"Auto-Play: {PendingAutoAction}...";
            }
            else
            {
                autoPlayText.TextColor = NativeUi.Green;
                autoPlayText.String = "Auto-Play: ON";
            }
        }

        root.RecalculateLayout();
        FitWindowToContent();
    }

    private void FitWindowToContent()
    {
        if (root is null)
            return;

        var height = ContentStartPosition.Y + Math.Max(48f, root.Height) + 24f;
        var width = renderedCompact ? 320f : 360f;
        if (Math.Abs(Size.Y - height) > 2f || Math.Abs(Size.X - width) > 2f)
            SetWindowSize(width, height);
    }

    private string BuildCompactLine(SuggestMoveResponse? suggestion)
    {
        if (suggestion == null || suggestion.Suggestions.Count == 0)
            return "Waiting for suggestion...";

        if (!string.IsNullOrEmpty(suggestion.Error))
            return $"Error: {suggestion.Error}";

        var best = suggestion.Suggestions[0];
        var sb = new StringBuilder();
        sb.Append("Discard ").Append(best.Tile);
        if (suggestion.Shanten.HasValue)
            sb.Append("  shanten ").Append(suggestion.Shanten.Value);
        if (best.Ukeire.HasValue)
            sb.Append("  ukeire ").Append(best.Ukeire.Value);
        return sb.ToString();
    }

    private string BuildFullHeader(SuggestMoveResponse? suggestion)
    {
        var shanten = suggestion?.Shanten != null ? suggestion.Shanten.Value.ToString() : "?";
        var status = ServerStatus ?? "?";
        return $"Shanten: {shanten}   [{status}]";
    }

    private static string BuildFullBody(SuggestMoveResponse? suggestion)
    {
        if (suggestion == null || suggestion.Suggestions.Count == 0)
            return "No suggestions available.";

        if (!string.IsNullOrEmpty(suggestion.Error))
            return suggestion.Error;

        var sb = new StringBuilder();
        sb.AppendLine("Tile     Shanten  Ukeire  Conf");
        var maxToShow = Math.Min(suggestion.Suggestions.Count, 8);
        for (var i = 0; i < maxToShow; i++)
        {
            var s = suggestion.Suggestions[i];
            var confText = s.Confidence.HasValue ? $"{s.Confidence:F1}" : "-";
            sb.Append(PadRight(s.Tile, 9));
            sb.Append(PadRight(s.Shanten?.ToString() ?? "?", 9));
            sb.Append(PadRight(s.Ukeire?.ToString() ?? "-", 8));
            sb.Append(confText);
            sb.AppendLine();
        }

        if (suggestion.Suggestions.Count > maxToShow)
            sb.Append("  ... +").Append(suggestion.Suggestions.Count - maxToShow).Append(" more");

        return sb.ToString().TrimEnd();
    }

    private static Vector4 SuggestionHeaderColor(SuggestMoveResponse? suggestion)
    {
        if (suggestion == null || suggestion.Suggestions.Count == 0)
            return NativeUi.Gray;
        if (!string.IsNullOrEmpty(suggestion.Error))
            return NativeUi.Red;
        return NativeUi.Gold;
    }

    private static string PadRight(string s, int width)
        => s.Length >= width ? s : s + new string(' ', width - s.Length);
}
