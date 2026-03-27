using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace SamplePlugin.Mahjong;

public static unsafe class EmjUiReader
{
    // Player hand area uses nodes 54–71 (18 raw slots; not all are active at once).
    // The drawn tile is identified by position gap, not a fixed node index.
    private static readonly int[] PlayerHandNodeIndices = Enumerable.Range(54, 18).ToArray();

    public enum SlotKind
    {
        CanonicalPlayerHand,
        CanonicalPlayerDraw,
        PlayerHand,
        PlayerDraw,
        VisibleTileCandidate,
    }

    public sealed record UiSlot(
        SlotKind Kind,
        int SlotIndex,
        int NodeIndex,
        uint NodeId,
        ushort NodeType,
        bool Visible,
        float X,
        float Y,
        int Width,
        int Height,
        uint IconId,
        string? TileCode);

    public sealed record UiState(IReadOnlyList<UiSlot> Slots, DateTime UtcCapturedAt)
    {
        public string ToDisplayText()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Captured: {UtcCapturedAt:O}");

            var canonicalHand = Slots.Where(s => s.Kind == SlotKind.CanonicalPlayerHand).OrderBy(s => s.SlotIndex).ToList();
            var canonicalDraw = Slots.FirstOrDefault(s => s.Kind == SlotKind.CanonicalPlayerDraw);
            var hand = Slots.Where(s => s.Kind == SlotKind.PlayerHand).OrderBy(s => s.SlotIndex).ToList();
            var draw = Slots.FirstOrDefault(s => s.Kind == SlotKind.PlayerDraw);
            var visible = Slots.Where(s => s.Kind == SlotKind.VisibleTileCandidate).OrderBy(s => s.X).ToList();

            sb.AppendLine($"Canonical player hand slots: {canonicalHand.Count}");
            foreach (var slot in canonicalHand)
                sb.AppendLine(DescribeSlot(slot));

            sb.AppendLine("Canonical player draw slot:");
            sb.AppendLine(canonicalDraw == null ? "  (missing)" : DescribeSlot(canonicalDraw));

            sb.AppendLine($"Player hand slots: {hand.Count}");
            foreach (var slot in hand)
                sb.AppendLine(DescribeSlot(slot));

            sb.AppendLine("Player draw slot:");
            sb.AppendLine(draw == null ? "  (missing)" : DescribeSlot(draw));

            sb.AppendLine($"Visible tile candidates: {visible.Count}");
            foreach (var slot in visible)
                sb.AppendLine(DescribeSlot(slot));

            return sb.ToString();
        }

        private static string DescribeSlot(UiSlot slot)
        {
            var tile = slot.TileCode ?? (slot.IconId > 0 ? $"ICON_{slot.IconId}" : "(no-icon)");
            return $"  [{slot.Kind}:{slot.SlotIndex}] node={slot.NodeIndex} id={slot.NodeId} type={slot.NodeType} vis={slot.Visible} pos=({slot.X:F0},{slot.Y:F0}) size=({slot.Width},{slot.Height}) {tile}";
        }
    }

    public static UiState Read(AtkUnitBase* addon, IconIdCapture? iconCapture, MahjongIconMap? iconMap)
    {
        var slots = new List<UiSlot>();

        if (addon == null)
            return new UiState(slots, DateTime.UtcNow);

        var uld = addon->UldManager;

        var rawHand = new List<UiSlot>();
        for (var i = 0; i < PlayerHandNodeIndices.Length; i++)
        {
            var nodeIndex = PlayerHandNodeIndices[i];
            if (!TryReadNodeSlot(uld, nodeIndex, iconCapture, iconMap, SlotKind.PlayerHand, i, out var slot))
                continue;
            rawHand.Add(slot);
            slots.Add(slot);
        }

        var visibleCandidates = new List<UiSlot>();

        for (int i = 0; i < uld.NodeListCount; i++)
        {
            var node = uld.NodeList[i];
            if (node == null)
                continue;

            var type = (int)node->Type;
            if (type != 1055 || node->Width != 42 || node->Height != 55)
                continue;

            bool visible;
            try { visible = node->IsVisible(); }
            catch { visible = false; }
            if (!visible)
                continue;

            uint iconId = 0;
            TryFindIcon(node, iconCapture, out iconId);

            var candidate = new UiSlot(
                SlotKind.VisibleTileCandidate,
                visibleCandidates.Count,
                i,
                node->NodeId,
                (ushort)node->Type,
                visible,
                node->X,
                node->Y,
                node->Width,
                node->Height,
                iconId,
                iconId > 0 ? iconMap?.Resolve(iconId) : null);

            visibleCandidates.Add(candidate);
            slots.Add(candidate);
        }

        var canonicalHand = BuildCanonicalHand(rawHand);
        for (var i = 0; i < canonicalHand.Count; i++)
        {
            var source = canonicalHand[i];
            slots.Add(source with { Kind = SlotKind.CanonicalPlayerHand, SlotIndex = i });
        }

        var canonicalDraw = BuildCanonicalDraw(canonicalHand);
        if (canonicalDraw != null)
        {
            // Remove the draw tile from the canonical hand list so it's not double-counted.
            // Rebuild canonical hand slots without the draw tile and re-add them.
            slots.RemoveAll(s => s.Kind == SlotKind.CanonicalPlayerHand);
            var handWithoutDraw = canonicalHand.Where(s => s.NodeIndex != canonicalDraw.NodeIndex).ToList();
            for (var i = 0; i < handWithoutDraw.Count; i++)
                slots.Add(handWithoutDraw[i] with { Kind = SlotKind.CanonicalPlayerHand, SlotIndex = i });

            slots.Add(canonicalDraw with { Kind = SlotKind.CanonicalPlayerDraw, SlotIndex = 0 });
        }

        return new UiState(slots, DateTime.UtcNow);
    }

    private static List<UiSlot> BuildCanonicalHand(List<UiSlot> rawHand)
    {
        // Known noisy pattern: some fixed hand indices are placeholders at x=0 with no icon.
        // Canonical view prioritizes visible, positioned slots and falls back to icon-positive slots.
        var filtered = rawHand
            .Where(slot => slot.Visible)
            .Where(slot => slot.X > 0 || slot.IconId > 0)
            .OrderBy(slot => slot.X)
            .ThenBy(slot => slot.SlotIndex)
            .ToList();

        if (filtered.Count == 0)
        {
            filtered = rawHand
                .Where(slot => slot.Visible)
                .OrderBy(slot => slot.SlotIndex)
                .ToList();
        }

        if (filtered.Count > 14)
            filtered = filtered.TakeLast(14).ToList();

        return filtered;
    }

    /// <summary>
    /// Detects the drawn tile by finding a position gap significantly larger than
    /// the normal tile spacing. In Mahjong UI, the drawn tile is visually separated
    /// from the sorted hand by a wider gap (typically ~10+ pixels vs ~42 normal).
    /// </summary>
    private static UiSlot? BuildCanonicalDraw(List<UiSlot> canonicalHand)
    {
        if (canonicalHand.Count < 2)
            return null;

        // Tiles should already be sorted by X.
        // Find the largest gap between consecutive tiles.
        float maxGap = 0;
        int maxGapIndex = -1;

        for (int i = 1; i < canonicalHand.Count; i++)
        {
            var gap = canonicalHand[i].X - canonicalHand[i - 1].X;
            if (gap > maxGap)
            {
                maxGap = gap;
                maxGapIndex = i;
            }
        }

        // Normal tile spacing is ~42px (tile width). A drawn tile gap is noticeably larger.
        // Use a threshold of 1.2x normal spacing to detect.
        const float gapThreshold = 50f;

        if (maxGap < gapThreshold || maxGapIndex < 0)
            return null;

        // The drawn tile is the one after the largest gap (rightmost separated tile).
        return canonicalHand[maxGapIndex];
    }

    private static bool TryReadNodeSlot(AtkUldManager uld, int nodeIndex, IconIdCapture? iconCapture, MahjongIconMap? iconMap, SlotKind kind, int slotIndex, out UiSlot slot)
    {
        slot = null!;
        if (nodeIndex < 0 || nodeIndex >= uld.NodeListCount)
            return false;

        var node = uld.NodeList[nodeIndex];
        if (node == null)
            return false;

        bool visible;
        try { visible = node->IsVisible(); }
        catch { visible = false; }

        uint iconId = 0;
        TryFindIcon(node, iconCapture, out iconId);

        slot = new UiSlot(
            kind,
            slotIndex,
            nodeIndex,
            node->NodeId,
            (ushort)node->Type,
            visible,
            node->X,
            node->Y,
            node->Width,
            node->Height,
            iconId,
            iconId > 0 ? iconMap?.Resolve(iconId) : null);

        return true;
    }

    private static bool TryFindIcon(AtkResNode* root, IconIdCapture? capture, out uint iconId)
    {
        iconId = 0;
        var visited = new HashSet<nint>();
        return TryFindIconRecursive(root, capture, visited, 0, out iconId);
    }

    private static bool TryFindIconRecursive(AtkResNode* root, IconIdCapture? capture, HashSet<nint> visited, int depth, out uint iconId)
    {
        iconId = 0;
        if (root == null || depth > 32)
            return false;

        nint addr = (nint)root;
        if (!visited.Add(addr))
            return false;

        try
        {
            if (root->Type == NodeType.Image)
            {
                var image = (AtkImageNode*)root;

                // Primary path: hook-based capture (fastest, works when hook has fired)
                if (capture != null)
                {
                    iconId = capture.GetIconId((nint)image);
                    if (iconId > 0)
                        return true;
                }

                // Fallback: read icon ID directly from the texture resource chain.
                // This works even after mid-game plugin reload when the LoadIconTexture
                // hook hasn't fired yet for already-loaded tile textures.
                iconId = TryReadIconIdFromStruct(image);
                if (iconId > 0)
                    return true;
            }

            for (var child = root->ChildNode; child != null; child = child->NextSiblingNode)
            {
                if (TryFindIconRecursive(child, capture, visited, depth + 1, out iconId))
                    return true;
            }

            if ((int)root->Type >= 1000)
            {
                var componentNode = (AtkComponentNode*)root;
                if (componentNode->Component != null)
                {
                    var childUld = componentNode->Component->UldManager;
                    for (int i = 0; i < childUld.NodeListCount && i < 64; i++)
                    {
                        var child = childUld.NodeList[i];
                        if (child == null) continue;
                        if (TryFindIconRecursive(child, capture, visited, depth + 1, out iconId))
                            return true;
                    }
                }
            }
        }
        catch
        {
        }

        return false;
    }

    /// <summary>
    /// Reads the icon ID directly from the AtkImageNode texture resource chain:
    /// PartsList → Parts[PartId] → UldAsset → AtkTexture.Resource → IconId.
    /// Returns 0 if the chain is broken or the node is not icon-mode.
    /// </summary>
    private static uint TryReadIconIdFromStruct(AtkImageNode* image)
    {
        try
        {
            if (image == null)
                return 0;

            // Icon-mode images have Flags containing 0x80
            if (((byte)image->Flags & 0x80) == 0)
                return 0;

            var partsList = image->PartsList;
            if (partsList == null || partsList->Parts == null)
                return 0;

            if (image->PartId >= partsList->PartCount)
                return 0;

            var part = partsList->Parts[image->PartId];
            if (part.UldAsset == null)
                return 0;

            var tex = part.UldAsset->AtkTexture;
            if (tex.Resource == null)
                return 0;

            return tex.Resource->IconId;
        }
        catch
        {
            return 0;
        }
    }
}
