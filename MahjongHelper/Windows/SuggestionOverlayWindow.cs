using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;
using MahjongHelper.Mahjong;

namespace MahjongHelper.Windows;

/// <summary>
/// Native FFXIV suggestion overlay (KamiToolKit NativeAddon).
/// Compact mode is a single best-discard line; full mode shows a ranked per-row list.
/// Appears in Print Screen / vanilla screenshots because it lives in the game UI tree.
/// </summary>
public class SuggestionOverlayWindow : NativeAddon
{
    private const int MaxVisibleSuggestions = 8;
    private const float CompactWidth = 360f;
    private const float FullWidth = 400f;

    private readonly Configuration configuration;
    private bool pluginDrivenClose;

    private VerticalListNode? root;
    private HorizontalListNode? compactRow;
    private TextNode? compactDiscardLabel;
    private TextNode? compactTile;
    private TextNode? compactShanten;
    private TextNode? compactUkeire;
    private TextNode? compactStatus;
    private HorizontalListNode? fullHeaderRow;
    private TextNode? shantenLabel;
    private TextNode? shantenValue;
    private TextNode? serverStatusText;
    private HorizontalLineNode? separator;
    private HorizontalListNode? columnHeader;
    private SuggestionRow[]? suggestionRows;
    private TextNode? moreText;
    private TextNode? emptyText;
    private TextNode? callText;
    private TextNode? autoPlayText;
    private bool renderedCompact = true;
    private int lastLayoutSignature = int.MinValue;

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
        Size = new Vector2(CompactWidth, 160f);
        OpenWindowSoundEffectId = 0;
        RespectCloseAll = false;
        DisableCloseTransition = true;
        RememberClosePosition = true;
    }

    /// <summary>
    /// Opens or closes the native addon on visibility edges only.
    /// Open when unallocated; Close when currently visible. Closing is animated, so
    /// a later Open waits until InternalAddon is nulled by destructor (next tick).
    /// Must be called from the game main thread.
    /// </summary>
    public unsafe void ApplyVisibility(bool shouldBeVisible)
    {
        if (shouldBeVisible)
        {
            // Never Open() while a previous addon is still allocated (close animation).
            if (InternalAddon is null)
                Open();
            return;
        }

        if (InternalAddon is not null && InternalAddon->IsVisible)
        {
            pluginDrivenClose = true;
            Close();
        }
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

        compactRow = new HorizontalListNode
        {
            Width = width,
            Height = 28f,
            ItemSpacing = 8f,
            FitHeight = true,
        };
        compactDiscardLabel = NativeUi.Text(58f, 22f, 14);
        compactDiscardLabel.String = "Discard";
        compactTile = NativeUi.Text(48f, 22f, 14);
        compactTile.TextColor = NativeUi.Gold;
        compactShanten = NativeUi.Text(90f, 22f, 14);
        compactShanten.TextColor = NativeUi.Cyan;
        compactUkeire = NativeUi.Text(90f, 22f, 14);
        compactUkeire.TextColor = NativeUi.Green;
        compactStatus = NativeUi.Text(width - 96f, 22f, 14);
        var compactModeButton = new TextButtonNode
        {
            Size = new Vector2(88f, 28f),
            String = "Full",
            OnClick = ToggleCompactMode,
        };
        compactRow.AddNode(compactDiscardLabel);
        compactRow.AddNode(compactTile);
        compactRow.AddNode(compactShanten);
        compactRow.AddNode(compactUkeire);
        compactRow.AddNode(compactStatus);
        compactRow.AddNode(compactModeButton);

        fullHeaderRow = new HorizontalListNode
        {
            Width = width,
            Height = 28f,
            ItemSpacing = 8f,
            FitHeight = true,
        };
        shantenLabel = NativeUi.Text(62f, 22f, 14);
        shantenLabel.String = "Shanten:";
        shantenValue = NativeUi.Text(28f, 22f, 14);
        serverStatusText = NativeUi.Text(width - 180f, 22f, 12);
        var fullModeButton = new TextButtonNode
        {
            Size = new Vector2(88f, 28f),
            String = "Compact",
            OnClick = ToggleCompactMode,
        };
        fullHeaderRow.AddNode(shantenLabel);
        fullHeaderRow.AddNode(shantenValue);
        fullHeaderRow.AddNode(serverStatusText);
        fullHeaderRow.AddNode(fullModeButton);

        separator = NativeUi.Separator(width);
        columnHeader = BuildColumnHeader(width);
        suggestionRows = new SuggestionRow[MaxVisibleSuggestions];
        for (var i = 0; i < suggestionRows.Length; i++)
        {
            suggestionRows[i] = new SuggestionRow(width);
            suggestionRows[i].Row.IsVisible = false;
        }

        moreText = NativeUi.Text(width, 20f);
        moreText.TextColor = NativeUi.Gray;
        emptyText = NativeUi.Text(width, 40f, 12, wrap: true);
        emptyText.TextColor = NativeUi.Gray;
        callText = NativeUi.Text(width, 48f, 12, wrap: true);
        callText.TextColor = NativeUi.Gold;
        autoPlayText = NativeUi.Text(width, 22f, 12);

        root.AddNode(compactRow);
        root.AddNode(fullHeaderRow);
        root.AddNode(separator);
        root.AddNode(columnHeader);
        foreach (var row in suggestionRows)
            root.AddNode(row.Row);
        root.AddNode(moreText);
        root.AddNode(emptyText);
        root.AddNode(callText);
        root.AddNode(autoPlayText);
        root.AttachNode(this);

        renderedCompact = configuration.OverlayCompactMode;
        lastLayoutSignature = int.MinValue;
        RefreshDisplay(forceLayout: true);
    }

    protected override unsafe void OnDraw(AtkUnitBase* addon)
    {
        base.OnDraw(addon);
        if (!IsOpen)
            return;
        RefreshDisplay(forceLayout: false);
    }

    protected override unsafe void OnHide(AtkUnitBase* addon)
    {
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
        compactRow = null;
        compactDiscardLabel = null;
        compactTile = null;
        compactShanten = null;
        compactUkeire = null;
        compactStatus = null;
        fullHeaderRow = null;
        shantenLabel = null;
        shantenValue = null;
        serverStatusText = null;
        separator = null;
        columnHeader = null;
        suggestionRows = null;
        moreText = null;
        emptyText = null;
        callText = null;
        autoPlayText = null;
        lastLayoutSignature = int.MinValue;
        base.OnFinalize(addon);
    }

    private void ToggleCompactMode()
    {
        configuration.OverlayCompactMode = !configuration.OverlayCompactMode;
        configuration.Save();
        RefreshDisplay(forceLayout: true);
    }

    private void RefreshDisplay(bool forceLayout)
    {
        if (root is null || compactRow is null || fullHeaderRow is null ||
            compactDiscardLabel is null || compactTile is null || compactShanten is null ||
            compactUkeire is null || compactStatus is null || shantenLabel is null ||
            shantenValue is null || serverStatusText is null || separator is null ||
            columnHeader is null || suggestionRows is null || moreText is null ||
            emptyText is null || callText is null || autoPlayText is null)
            return;

        if (!forceLayout && !IsOpen)
            return;

        var compact = configuration.OverlayCompactMode;
        var suggestion = LastSuggestion;
        var hasList = suggestion is { Suggestions.Count: > 0 } && string.IsNullOrEmpty(suggestion.Error);
        var callVisible = !string.IsNullOrEmpty(CallRecommendation);
        var autoPlayVisible = AutoPlayEnabled;

        compactRow.IsVisible = compact;
        fullHeaderRow.IsVisible = !compact;
        separator.IsVisible = !compact;
        columnHeader.IsVisible = !compact && hasList;

        if (compact)
            BindCompact(suggestion, hasList);
        else
            BindFullHeader(suggestion);

        var visibleRows = 0;
        if (!compact && hasList && suggestion is not null)
        {
            visibleRows = Math.Min(suggestion.Suggestions.Count, MaxVisibleSuggestions);
            for (var i = 0; i < suggestionRows.Length; i++)
            {
                if (i < visibleRows)
                    suggestionRows[i].Bind(suggestion.Suggestions[i], isTop: i == 0);
                else
                    suggestionRows[i].Hide();
            }

            if (suggestion.Suggestions.Count > MaxVisibleSuggestions)
            {
                moreText.IsVisible = true;
                moreText.String = $"+{suggestion.Suggestions.Count - MaxVisibleSuggestions} more";
            }
            else
            {
                moreText.IsVisible = false;
            }

            emptyText.IsVisible = false;
        }
        else
        {
            foreach (var row in suggestionRows)
                row.Hide();
            moreText.IsVisible = false;
            if (compact)
            {
                emptyText.IsVisible = false;
            }
            else
            {
                emptyText.IsVisible = true;
                if (suggestion == null || suggestion.Suggestions.Count == 0)
                {
                    emptyText.TextColor = NativeUi.Gray;
                    emptyText.String = "No suggestions available.";
                }
                else
                {
                    emptyText.TextColor = NativeUi.Red;
                    emptyText.String = suggestion.Error ?? "No suggestions available.";
                }
            }
        }

        if (callVisible)
        {
            callText.IsVisible = true;
            callText.String = $"Call: {CallRecommendation}";
        }
        else
        {
            callText.IsVisible = false;
        }

        if (autoPlayVisible)
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
        else
        {
            autoPlayText.IsVisible = false;
        }

        var layoutSignature = HashCode.Combine(compact, visibleRows, moreText.IsVisible, emptyText.IsVisible, callVisible, autoPlayVisible);
        if (!forceLayout && compact == renderedCompact && layoutSignature == lastLayoutSignature)
            return;

        renderedCompact = compact;
        lastLayoutSignature = layoutSignature;
        root.Width = Math.Max(200f, ContentSize.X);
        root.RecalculateLayout();
        FitWindowToContent();
    }

    private void BindCompact(SuggestMoveResponse? suggestion, bool hasList)
    {
        if (compactDiscardLabel is null || compactTile is null || compactShanten is null ||
            compactUkeire is null || compactStatus is null)
            return;

        if (!hasList || suggestion is null)
        {
            compactDiscardLabel.IsVisible = false;
            compactTile.IsVisible = false;
            compactShanten.IsVisible = false;
            compactUkeire.IsVisible = false;
            compactStatus.IsVisible = true;
            if (suggestion == null || suggestion.Suggestions.Count == 0)
            {
                compactStatus.TextColor = NativeUi.Gray;
                compactStatus.String = "Waiting for suggestion...";
            }
            else
            {
                compactStatus.TextColor = NativeUi.Red;
                compactStatus.String = $"Error: {suggestion.Error}";
            }

            return;
        }

        var best = suggestion.Suggestions[0];
        compactStatus.IsVisible = false;
        compactDiscardLabel.IsVisible = true;
        compactTile.IsVisible = true;
        compactTile.String = best.Tile;
        compactTile.TextTooltip = best.Reasoning ?? string.Empty;
        compactShanten.IsVisible = suggestion.Shanten.HasValue;
        compactShanten.String = suggestion.Shanten.HasValue ? $"shanten {suggestion.Shanten.Value}" : string.Empty;
        compactShanten.TextColor = NativeUi.ShantenColor(suggestion.Shanten);
        compactUkeire.IsVisible = best.Ukeire.HasValue;
        compactUkeire.String = best.Ukeire.HasValue ? $"ukeire {best.Ukeire.Value}" : string.Empty;
        compactUkeire.TextColor = NativeUi.UkeireColor(best.Ukeire);
    }

    private void BindFullHeader(SuggestMoveResponse? suggestion)
    {
        if (shantenValue is null || serverStatusText is null)
            return;

        shantenValue.String = suggestion?.Shanten?.ToString() ?? "?";
        shantenValue.TextColor = suggestion?.Shanten == null ? NativeUi.Gray : NativeUi.ShantenColor(suggestion.Shanten);
        var status = ServerStatus ?? "?";
        serverStatusText.String = $"[{status}]";
        serverStatusText.TextColor = status.StartsWith("Connected", StringComparison.Ordinal) ? NativeUi.Green : NativeUi.Red;
    }

    private void FitWindowToContent()
    {
        if (root is null)
            return;

        var width = renderedCompact ? CompactWidth : FullWidth;
        var height = ContentStartPosition.Y + Math.Max(48f, root.Height) + 24f;
        if (Math.Abs(Size.Y - height) > 2f || Math.Abs(Size.X - width) > 2f)
            SetWindowSize(width, height);
    }

    private static HorizontalListNode BuildColumnHeader(float width)
    {
        var header = new HorizontalListNode
        {
            Width = width,
            Height = 20f,
            ItemSpacing = 8f,
            FitHeight = true,
        };
        header.AddNode(HeaderCell("Tile", SuggestionRow.TileWidth));
        header.AddNode(HeaderCell("Shanten", SuggestionRow.ShantenWidth));
        header.AddNode(HeaderCell("Ukeire", SuggestionRow.UkeireWidth));
        header.AddNode(HeaderCell("Conf", SuggestionRow.ConfWidth));
        return header;
    }

    private static TextNode HeaderCell(string label, float width)
    {
        var node = NativeUi.Text(width, 20f);
        node.TextColor = NativeUi.Gray;
        node.String = label;
        return node;
    }

    private sealed class SuggestionRow
    {
        public const float TileWidth = 72f;
        public const float ShantenWidth = 64f;
        public const float UkeireWidth = 64f;
        public const float ConfWidth = 48f;

        public HorizontalListNode Row { get; }
        private readonly TextNode tile;
        private readonly TextNode shanten;
        private readonly TextNode ukeire;
        private readonly TextNode conf;

        public SuggestionRow(float width)
        {
            Row = new HorizontalListNode
            {
                Width = width,
                Height = 20f,
                ItemSpacing = 8f,
                FitHeight = true,
            };
            tile = NativeUi.Text(TileWidth, 20f);
            shanten = NativeUi.Text(ShantenWidth, 20f);
            ukeire = NativeUi.Text(UkeireWidth, 20f);
            conf = NativeUi.Text(ConfWidth, 20f);
            Row.AddNode(tile);
            Row.AddNode(shanten);
            Row.AddNode(ukeire);
            Row.AddNode(conf);
        }

        public void Bind(DiscardSuggestion suggestion, bool isTop)
        {
            Row.IsVisible = true;
            tile.String = suggestion.Tile;
            tile.TextColor = isTop ? NativeUi.Gold : NativeUi.White;
            shanten.String = suggestion.Shanten?.ToString() ?? "?";
            shanten.TextColor = NativeUi.Cyan;
            ukeire.String = suggestion.Ukeire?.ToString() ?? "-";
            ukeire.TextColor = NativeUi.UkeireColor(suggestion.Ukeire);
            conf.String = suggestion.Confidence.HasValue ? $"{suggestion.Confidence:F1}" : "-";
            conf.TextColor = NativeUi.White;
            var tooltip = suggestion.Reasoning ?? string.Empty;
            tile.TextTooltip = tooltip;
            shanten.TextTooltip = tooltip;
            ukeire.TextTooltip = tooltip;
            conf.TextTooltip = tooltip;
        }

        public void Hide()
        {
            Row.IsVisible = false;
        }
    }
}
