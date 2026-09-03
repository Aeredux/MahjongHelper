using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;

namespace MahjongHelper.Windows;

/// <summary>
/// Native FFXIV settings window (KamiToolKit NativeAddon).
/// Covers strategy provider, auto-play toggles, and min/max action delays.
/// </summary>
public class ConfigWindow : NativeAddon
{
    private static readonly string[] ProviderNames = ["In-Game", "Server"];

    private readonly Configuration configuration;
    private VerticalListNode? root;
    private VerticalListNode? autoPlayDetails;
    private StringDropDownNode? providerDropDown;
    private CheckboxNode? autoPlayCheckbox;
    private CheckboxNode? autoDiscardCheckbox;
    private CheckboxNode? autoCallCheckbox;
    private NumericInputNode? discardMinInput;
    private NumericInputNode? discardMaxInput;
    private NumericInputNode? callMinInput;
    private NumericInputNode? callMaxInput;

    public Action<int>? OnStrategyProviderChanged;

    [SetsRequiredMembers]
    public ConfigWindow(Plugin plugin)
    {
        configuration = plugin.Configuration;
        InternalName = "MahjongHelperCfg";
        Title = "Settings";
        Size = new Vector2(400f, 280f);
        RememberClosePosition = true;
    }

    protected override unsafe void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan)
    {
        base.OnSetup(addon, atkValueSpan);

        var width = Math.Max(280f, ContentSize.X);

        root = new VerticalListNode
        {
            Position = ContentStartPosition,
            Width = width,
            ItemSpacing = 6f,
            FitContents = true,
            FitWidth = true,
        };

        root.AddNode(new CategoryTextNode { String = "Auto-Play" });
        root.AddNode(NativeUi.Separator(width));
        root.AddNode(SetString(NativeUi.Text(width, 20f), "Strategy Provider"));

        var providerIndex = Math.Clamp(configuration.StrategyProvider, 0, ProviderNames.Length - 1);
        providerDropDown = new StringDropDownNode
        {
            Size = new Vector2(175f, 28f),
            MaxListOptions = ProviderNames.Length,
            Options = [.. ProviderNames],
            SelectedOption = ProviderNames[providerIndex],
        };
        providerDropDown.OnOptionSelected = OnProviderSelected;
        root.AddNode(providerDropDown);

        autoPlayCheckbox = new CheckboxNode
        {
            Height = 24f,
            String = "Enable Auto-Play",
            IsChecked = configuration.AutoPlayEnabled,
        };
        autoPlayCheckbox.OnClick = OnAutoPlayToggled;
        root.AddNode(autoPlayCheckbox);

        autoPlayDetails = new VerticalListNode
        {
            Width = width,
            ItemSpacing = 6f,
            FitContents = true,
            FitWidth = true,
            IsVisible = configuration.AutoPlayEnabled,
        };

        autoDiscardCheckbox = new CheckboxNode
        {
            Height = 24f,
            String = "Auto-Discard",
            IsChecked = configuration.AutoDiscardEnabled,
        };
        autoDiscardCheckbox.OnClick = enabled =>
        {
            configuration.AutoDiscardEnabled = enabled;
            configuration.Save();
        };

        autoCallCheckbox = new CheckboxNode
        {
            Height = 24f,
            String = "Auto-Call Decisions",
            IsChecked = configuration.AutoCallEnabled,
        };
        autoCallCheckbox.OnClick = enabled =>
        {
            configuration.AutoCallEnabled = enabled;
            configuration.Save();
        };

        autoPlayDetails.AddNode(autoDiscardCheckbox);
        autoPlayDetails.AddNode(autoCallCheckbox);
        autoPlayDetails.AddNode(new CategoryTextNode { String = "Timing" });
        autoPlayDetails.AddNode(NativeUi.Separator(width));
        autoPlayDetails.AddNode(SetString(NativeUi.Text(width, 20f), "Discard Delay (ms)"));
        autoPlayDetails.AddNode(BuildDelayRow(
            configuration.AutoDiscardDelayMinMs,
            configuration.AutoDiscardDelayMaxMs,
            out discardMinInput,
            out discardMaxInput,
            (min, max) =>
            {
                configuration.AutoDiscardDelayMinMs = min;
                configuration.AutoDiscardDelayMaxMs = max;
                configuration.Save();
            }));
        autoPlayDetails.AddNode(SetString(NativeUi.Text(width, 20f), "Call Decision Delay (ms)"));
        autoPlayDetails.AddNode(BuildDelayRow(
            configuration.AutoCallDelayMinMs,
            configuration.AutoCallDelayMaxMs,
            out callMinInput,
            out callMaxInput,
            (min, max) =>
            {
                configuration.AutoCallDelayMinMs = min;
                configuration.AutoCallDelayMaxMs = max;
                configuration.Save();
            }));

        root.AddNode(autoPlayDetails);
        root.AttachNode(this);
        RecalculateWindowSize();
    }

    protected override unsafe void OnFinalize(AtkUnitBase* addon)
    {
        root = null;
        autoPlayDetails = null;
        providerDropDown = null;
        autoPlayCheckbox = null;
        autoDiscardCheckbox = null;
        autoCallCheckbox = null;
        discardMinInput = null;
        discardMaxInput = null;
        callMinInput = null;
        callMaxInput = null;
        base.OnFinalize(addon);
    }

    private void OnProviderSelected(string option)
    {
        var index = Array.IndexOf(ProviderNames, option);
        if (index < 0)
            return;

        configuration.StrategyProvider = index;
        configuration.Save();
        OnStrategyProviderChanged?.Invoke(index);
    }

    private void OnAutoPlayToggled(bool enabled)
    {
        configuration.AutoPlayEnabled = enabled;
        configuration.Save();
        if (autoPlayDetails is not null)
            autoPlayDetails.IsVisible = enabled;
        RecalculateWindowSize();
    }

    private void RecalculateWindowSize()
    {
        if (root is null)
            return;

        root.Width = Math.Max(280f, ContentSize.X);
        root.RecalculateLayout();

        const float maxHeight = 420f;
        const float width = 400f;
        var contentHeight = root.Height + ContentStartPosition.Y + 24f;
        Size = new Vector2(width, Math.Clamp(contentHeight, 200f, maxHeight));
        SetWindowSize(Size);

        root.Size = ContentSize + new Vector2(0f, ContentPadding.Y);
        root.Position = ContentStartPosition;
        root.RecalculateLayout();
    }

    private static HorizontalListNode BuildDelayRow(
        int minValue,
        int maxValue,
        out NumericInputNode minInput,
        out NumericInputNode maxInput,
        Action<int, int> onChanged)
    {
        var row = new HorizontalListNode
        {
            Height = 28f,
            ItemSpacing = 8f,
            FitToContentHeight = true,
        };

        var min = new NumericInputNode
        {
            Size = new Vector2(110f, 24f),
            Min = 200,
            Max = 10000,
            Step = 50,
            Value = minValue,
        };
        var max = new NumericInputNode
        {
            Size = new Vector2(110f, 24f),
            Min = 200,
            Max = 10000,
            Step = 50,
            Value = maxValue,
        };

        min.OnValueUpdate = value =>
        {
            if (value > max.Value)
                max.Value = value;
            onChanged(value, max.Value);
        };
        max.OnValueUpdate = value =>
        {
            if (value < min.Value)
                min.Value = value;
            onChanged(min.Value, value);
        };

        row.AddNode(SetString(NativeUi.Text(32f, 20f), "Min"));
        row.AddNode(min);
        row.AddNode(SetString(NativeUi.Text(32f, 20f), "Max"));
        row.AddNode(max);

        minInput = min;
        maxInput = max;
        return row;
    }

    private static TextNode SetString(TextNode node, string value)
    {
        node.String = value;
        return node;
    }
}
