using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;

namespace MahjongHelper.Windows;

/// <summary>
/// Shared KamiToolKit node helpers so overlay and settings windows match native FFXIV styling.
/// </summary>
internal static class NativeUi
{
    public static readonly Vector4 White = new(1f, 1f, 1f, 1f);
    public static readonly Vector4 Gray = new(0.6f, 0.6f, 0.6f, 1f);
    public static readonly Vector4 Gold = new(1f, 0.84f, 0f, 1f);
    public static readonly Vector4 Green = new(0.4f, 1f, 0.4f, 1f);
    public static readonly Vector4 Cyan = new(0.4f, 0.9f, 1f, 1f);
    public static readonly Vector4 Red = new(1f, 0.4f, 0.4f, 1f);

    public static TextNode Text(float width, float height, uint fontSize = 12, bool wrap = false)
    {
        var node = new TextNode
        {
            Size = new Vector2(width, height),
            FontSize = fontSize,
            LineSpacing = fontSize + 2,
            FontType = FontType.Axis,
            AlignmentType = AlignmentType.TopLeft,
            TextColor = ColorHelper.GetColor(8),
            TextOutlineColor = ColorHelper.GetColor(7),
        };

        if (wrap)
            node.TextFlags = TextFlags.WordWrap | TextFlags.MultiLine | TextFlags.Emboss;

        return node;
    }

    public static HorizontalLineNode Separator(float width)
        => new()
        {
            Size = new Vector2(width, 4f),
        };
}
