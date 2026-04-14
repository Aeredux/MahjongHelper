using System;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.System.String;

namespace MahjongHelper.Mahjong.Debug;

public static unsafe class AtkTreeDumper
{
    public sealed class Options
    {
        public int MaxDepth { get; init; } = 10;
        public int MaxNodes { get; init; } = 2000;
        public int MaxSiblingSteps { get; init; } = 5000;
        public bool IncludeText { get; init; } = true;
        public bool IncludeImagePartId { get; init; } = true;
    }

    public static string Dump(AtkUnitBase* addon, Options? options = null)
    {
        options ??= new Options();

        if (addon == null) return "addon == null";
        if (addon->RootNode == null) return "addon->RootNode == null";

        var sb = new StringBuilder(64 * 1024);
        sb.AppendLine($"AtkUnitBase @ 0x{(nint)addon:X}");
        sb.AppendLine($"IsVisible: {addon->IsVisible}");
        sb.AppendLine();

        int visited = 0;
        DumpNodeRecursive(sb, addon->RootNode, 0, options, ref visited);

        sb.AppendLine();
        sb.AppendLine($"Visited nodes: {visited} (cap {options.MaxNodes})");
        sb.AppendLine("=== UldManager NodeList ===");
        try
        {
            var uld = addon->UldManager;
            sb.AppendLine($"NodeListCount={uld.NodeListCount}");

            for (int i = 0; i < uld.NodeListCount && i < 2000; i++)
            {
                var n = uld.NodeList[i];
                if (n == null) continue;

                sb.AppendLine($"{i}: type={n->Type} id={n->NodeId} vis={n->IsVisible()} pos=({n->X},{n->Y}) size=({n->Width},{n->Height})");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(Uld dump failed: {ex.Message})");
        }
        return sb.ToString();
    }

    private static void DumpNodeRecursive(
        StringBuilder sb,
        AtkResNode* node,
        int depth,
        Options opt,
        ref int visited)
    {
        if (node == null) return;
        if (visited >= opt.MaxNodes) return;

        visited++;

        sb.Append(' ', depth * 2);

        // These field names are correct for many recent FFXIVClientStructs builds.
        // If you get compile errors, tell me which symbol it complains about and I’ll adjust.
        var type = (NodeType)node->Type;
        sb.Append($"[{type}]");

        sb.Append($" id={node->NodeId}");
        sb.Append($" vis={node->IsVisible()}");
        sb.Append($" pos=({node->X},{node->Y})");
        sb.Append($" size=({node->Width},{node->Height})");
        // Extras (best-effort, don’t crash if layout differs)
        try
        {
            if (opt.IncludeText && type == NodeType.Text)
            {
                var textNode = (AtkTextNode*)node;
                var text = ReadUtf8String(&textNode->NodeText);
                if (!string.IsNullOrEmpty(text))
                    sb.Append($" text=\"{SanitizeOneLine(text)}\"");
            }
            else if (opt.IncludeImagePartId && type == NodeType.Image)
            {
                var img = (AtkImageNode*)node;
                sb.Append($" partId={img->PartId}");
            }
        }
        catch
        {
            // Ignore field-layout mismatches; still keep the tree.
        }

        sb.AppendLine();

        // --- Component expansion: dump the component's own node tree ---
        try
        {
            if (node->Type == NodeType.Component) // NodeType from clientstructs
            {
                var compNode = (AtkComponentNode*)node;

                // Component may be null if not constructed yet
                if (compNode->Component != null)
                {
                    var compRoot = compNode->Component->UldManager.RootNode;
                    if (compRoot != null)
                    {
                        sb.Append(' ', (depth + 1) * 2);
                        sb.AppendLine("↳ (component root)");
                        DumpNodeRecursive(sb, compRoot, depth + 2, opt, ref visited);
                    }
                }
            }
        }
        catch
        {
            // swallow; component layouts vary, don't crash dumper
        }

        if (depth >= opt.MaxDepth) return;

        var child = node->ChildNode;
        if (child == null) return;

        int siblingSteps = 0;
        while (child != null && visited < opt.MaxNodes && siblingSteps++ < opt.MaxSiblingSteps)
        {
            DumpNodeRecursive(sb, child, depth + 1, opt, ref visited);
            child = child->NextSiblingNode;
        }
    }

    private static string SanitizeOneLine(string s)
        => s.Replace("\r", " ").Replace("\n", " ").Trim();

    private static string ReadUtf8String(Utf8String* s)
    {
        if (s == null)
            return "";

        try
        {
            return Marshal.PtrToStringUTF8((nint)s->StringPtr.Value) ?? "";
        }
        catch
        {
            return "";
        }
    }

    public static string DumpLikelyTiles(AtkUnitBase* addon, int max = 300)
    {
        var sb = new StringBuilder();
        var uld = addon->UldManager;

        sb.AppendLine($"NodeListCount={uld.NodeListCount}");
        sb.AppendLine("Likely tile-sized nodes (w=34,h=45):");

        int count = 0;
        for (int i = 0; i < uld.NodeListCount && count < max; i++)
        {
            var n = uld.NodeList[i];
            if (n == null) continue;

            // Call visibility if needed
            bool vis = false;
            try { vis = n->IsVisible(); } catch { }

            if (!vis) continue;

            if (n->Width == 34 && n->Height == 45)
            {
                sb.AppendLine($"{i}: type={n->Type} id={n->NodeId} pos=({n->X},{n->Y})");
                count++;
            }
        }
        return sb.ToString();
    }

    public static unsafe string DumpTileSlotDetails(AtkUnitBase* addon, int startIndex = 256, int count = 13)
    {
        var sb = new StringBuilder();
        var uld = addon->UldManager;

        sb.AppendLine($"NodeListCount={uld.NodeListCount}");
        sb.AppendLine($"Dumping tile slots: [{startIndex}..{startIndex + count - 1}]");
        sb.AppendLine();

        for (int i = startIndex; i < startIndex + count && i < uld.NodeListCount; i++)
        {
            var n = uld.NodeList[i];
            if (n == null)
            {
                sb.AppendLine($"{i}: null");
                continue;
            }

            bool vis = false;
            try { vis = n->IsVisible(); } catch { }

            sb.Append($"{i}: type={n->Type} id={n->NodeId} vis={vis} pos=({n->X},{n->Y}) size=({n->Width},{n->Height})");

            // Try to find an image part id inside this node
            int partId = TryGetPartIdFromAny(n);
            sb.AppendLine($" partId={partId}");
        }

        return sb.ToString();
    }

    private static unsafe int TryGetPartIdFromAny(AtkResNode* node)
    {
        if (node == null) return 0;

        // 1) Direct reinterpret as AtkImageNode (works for your type=1045 nodes)
        try
        {
            var img = (AtkImageNode*)node;
            var part = img->PartId;

            // sanity: non-zero means "real"
            if (part != 0)
                return part;
        }
        catch { }

        // 2) Fallback: treat as component node and scan its internal tree
        try
        {
            var comp = (AtkComponentNode*)node;
            if (comp->Component != null)
            {
                var root = comp->Component->UldManager.RootNode;
                if (root != null)
                    return FindFirstNonZeroPartId(root);
            }
        }
        catch { }

        return 0;
    }

    private static unsafe int FindFirstNonZeroPartId(AtkResNode* node)
    {
        if (node == null) return 0;

        try
        {
            var img = (AtkImageNode*)node;
            if (img->PartId != 0)
                return img->PartId;
        }
        catch { }

        try
        {
            var c = node->ChildNode;
            int steps = 0;
            while (c != null && steps++ < 500)
            {
                var found = FindFirstNonZeroPartId(c);
                if (found != 0) return found;
                c = c->NextSiblingNode;
            }
        }
        catch { }

        return 0;
    }

    private static unsafe int FindBestImagePartId(AtkResNode* node)
    {
        int bestPart = 0;
        int bestArea = 0;

        void Visit(AtkResNode* n, int depth)
        {
            if (n == null || depth > 8)
                return;

            try
            {
                var img = (AtkImageNode*)n;
                int part = img->PartId;

                // skip placeholders
                if (part != 0 && part != 1 && part != 18)
                {
                    int area = n->Width * n->Height;

                    if (area > bestArea)
                    {
                        bestArea = area;
                        bestPart = part;
                    }
                }
            }
            catch { }

            try
            {
                var c = n->ChildNode;
                int steps = 0;

                while (c != null && steps++ < 200)
                {
                    Visit(c, depth + 1);
                    c = c->NextSiblingNode;
                }
            }
            catch { }
        }

        Visit(node, 0);

        return bestPart;
    }
    public static unsafe string InspectTileSlot(AtkUnitBase* addon, int nodeListIndex)
    {
        var sb = new StringBuilder();
        var uld = addon->UldManager;

        if (nodeListIndex < 0 || nodeListIndex >= uld.NodeListCount)
            return $"Index {nodeListIndex} out of range (0..{uld.NodeListCount - 1})";

        var n = uld.NodeList[nodeListIndex];
        if (n == null) return $"{nodeListIndex}: null";

        bool vis = false;
        try { vis = n->IsVisible(); } catch { }

        sb.AppendLine($"Inspect NodeList[{nodeListIndex}]: type={n->Type} id={n->NodeId} vis={vis} pos=({n->X},{n->Y}) size=({n->Width},{n->Height})");
        sb.AppendLine();

        // 1) Direct cast part id
        sb.AppendLine("Direct reinterpret as AtkImageNode:");
        try
        {
            var img = (AtkImageNode*)n;
            sb.AppendLine($"  PartId={img->PartId}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  (failed: {ex.Message})");
        }

        // 2) If it’s a component node, dump internal component tree image parts
        sb.AppendLine();
        sb.AppendLine("Component internal image parts:");
        try
        {
            var comp = (AtkComponentNode*)n;
            if (comp->Component == null)
            {
                sb.AppendLine("  (Component is null)");
            }
            else
            {
                var root = comp->Component->UldManager.RootNode;
                if (root == null)
                {
                    sb.AppendLine("  (Component root is null)");
                }
                else
                {
                    DumpImagePartsRecursive(sb, root, depth: 0, maxDepth: 6, maxNodes: 400);
                }
            }
        }
        catch
        {
            sb.AppendLine("  (Not a component node / cast failed)");
        }

        return sb.ToString();
    }

    private static unsafe void DumpImagePartsRecursive(StringBuilder sb, AtkResNode* node, int depth, int maxDepth, int maxNodes)
    {
        if (node == null || depth > maxDepth || maxNodes <= 0)
            return;

        // Print image part info if this node looks like an image and has a non-zero PartId
        try
        {
            var img = (AtkImageNode*)node;
            if (img->PartId != 0)
            {
                sb.Append(' ', depth * 2);
                sb.AppendLine($"type={node->Type} id={node->NodeId} size=({node->Width},{node->Height}) partId={img->PartId}");
            }
        }
        catch { }

        try
        {
            var c = node->ChildNode;
            int steps = 0;
            while (c != null && steps++ < 200 && maxNodes-- > 0)
            {
                DumpImagePartsRecursive(sb, c, depth + 1, maxDepth, maxNodes);
                c = c->NextSiblingNode;
            }
        }
        catch { }
    }
}
