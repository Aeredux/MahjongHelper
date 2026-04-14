using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MahjongHelper.Mahjong;

namespace MahjongHelper.Mahjong.Debug;

/// <summary>
/// Writes structured tile data from the EmjL addon to a file for offline analysis.
/// Output path: %APPDATA%/MahjongHelper/tile_dump.txt
///
/// SAFETY: All memory reads are wrapped in try/catch. No raw pointer arithmetic
/// beyond known struct field offsets. Uses Marshal.ReadByte for any exploratory reads.
/// </summary>
public static unsafe class TileDataDumper
{
    private static readonly string DumpDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MahjongHelper");

    private static readonly string DumpFilePath = Path.Combine(DumpDirectory, "tile_dump.txt");
    private static long _dumpSequence = 0;
    private static long _probeSequence = 0;

    public static string GetDumpFilePath() => DumpFilePath;

    public static string DumpAndSave(AtkUnitBase* addon, IconIdCapture? iconCapture = null, MahjongIconMap? iconMap = null)
    {
        var sb = new StringBuilder(64 * 1024);

        try
        {
            DumpCore(sb, addon, iconCapture, iconMap);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"\n!!! TOP-LEVEL DUMP ERROR: {ex.GetType().Name}: {ex.Message} !!!");
        }

        var content = sb.ToString();

        try
        {
            Directory.CreateDirectory(DumpDirectory);
            File.WriteAllText(DumpFilePath, content);
        }
        catch
        {
            // Silently fail file write — never crash the plugin
        }

        return content;
    }

    private static void DumpCore(StringBuilder sb, AtkUnitBase* addon, IconIdCapture? iconCapture, MahjongIconMap? iconMap)
    {
        var uld = addon->UldManager;
        var sequence = System.Threading.Interlocked.Increment(ref _dumpSequence);
        var now = DateTime.Now;
        var utcNow = DateTime.UtcNow;
        var tick = Environment.TickCount64;

        sb.AppendLine($"=== Mahjong Tile Dump — {now:yyyy-MM-dd HH:mm:ss.fff} ===");
        sb.AppendLine($"DumpSequence={sequence}");
        sb.AppendLine($"UtcTimestamp={utcNow:O}");
        sb.AppendLine($"TickCount64={tick}");
        sb.AppendLine($"NodeListCount={uld.NodeListCount}");
        sb.AppendLine($"Output: {DumpFilePath}");
        sb.AppendLine();

        // ─── Section 1: AtkValues ───
        DumpAtkValues(sb, addon);

        // ─── Section 1b: Captured Icon IDs (from LoadIconTexture hook) ───
        DumpCapturedIconIds(sb, addon, iconCapture);

        // ─── Section 1c: Resolved hand snapshot ───
        DumpResolvedHandSnapshot(sb, addon, iconCapture, iconMap);

        // ─── Section 1d: Client Mahjong state probes ───
        DumpClientMahjongState(sb);

        // ─── Section 1e: Spatial tile classification (discard pools, dora, etc.) ───
        DumpSpatialTileClassification(sb, addon, iconCapture, iconMap);

        // ─── Section 1f: UI element discovery (text nodes, buttons, wind/riichi/call prompts) ───
        DumpUiElementDiscovery(sb, addon);

        // ─── Section 1g: Dora indicator discovery (center board area) ───
        DumpDoraDiscovery(sb, addon, iconCapture, iconMap);

        // ─── Section 1h: Call prompt discovery ───
        DumpCallPromptDiscovery(sb, addon);

        // ─── Section 2: Complete node list ───
        sb.AppendLine("--- FULL NODE LIST ---");
        sb.AppendLine("idx | nodeId | vis | pos(x,y) | size(w,h) | type | flags | color");
        for (int i = 0; i < uld.NodeListCount; i++)
        {
            try
            {
                var n = uld.NodeList[i];
                if (n == null) { sb.AppendLine($"{i,4} | (null)"); continue; }

                bool vis = false;
                try { vis = n->IsVisible(); } catch { }

                uint colorRGB = 0;
                try { colorRGB = n->Color.RGBA; } catch { }

                sb.AppendLine($"{i,4} | {n->NodeId,8} | {(vis ? "Y" : "N")} | ({n->X,6:F0},{n->Y,6:F0}) | ({n->Width,4},{n->Height,4}) | {(int)n->Type,5} | 0x{n->NodeFlags:X} | 0x{colorRGB:X8}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"{i,4} | ERROR: {ex.Message}");
            }
        }
        sb.AppendLine();

        // ─── Section 3: Tile-sized nodes (34x45) — deep component inspection ───
        sb.AppendLine("--- TILE NODES (34x45 visible) — DEEP INSPECTION ---");
        for (int i = 0; i < uld.NodeListCount; i++)
        {
            try
            {
                var n = uld.NodeList[i];
                if (n == null) continue;

                bool vis = false;
                try { vis = n->IsVisible(); } catch { }
                if (!vis) continue;
                if (n->Width != 34 || n->Height != 45) continue;

                sb.AppendLine($"  NodeList[{i}] id={n->NodeId} type={(int)n->Type} pos=({n->X:F0},{n->Y:F0}) flags=0x{n->NodeFlags:X}");
                DumpNodeDeep(sb, n, indent: 4);
                sb.AppendLine();
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  NodeList[{i}] ERROR: {ex.Message}");
            }
        }

        // ─── Section 4: Struct sizes for offset verification ───
        sb.AppendLine("--- STRUCT SIZES ---");
        sb.AppendLine($"sizeof(AtkResNode) = 0x{sizeof(AtkResNode):X}");
        sb.AppendLine($"sizeof(AtkImageNode) = 0x{sizeof(AtkImageNode):X}");
        sb.AppendLine($"sizeof(AtkTextNode) = 0x{sizeof(AtkTextNode):X}");
        sb.AppendLine($"sizeof(AtkComponentNode) = 0x{sizeof(AtkComponentNode):X}");
        sb.AppendLine($"sizeof(AtkComponentBase) = 0x{sizeof(AtkComponentBase):X}");
        sb.AppendLine($"sizeof(AtkUnitBase) = 0x{sizeof(AtkUnitBase):X}");
        sb.AppendLine();

        // ─── Section 5: Pointer Fingerprint Comparison ───
        DumpPointerFingerprints(sb, addon);

        // ─── Section 6: Follow addon+0x288 pointer array (14 pointers = 13 hand + 1 drawn) ───
        DumpAddonPointerArray(sb, (nint)addon);

        // ─── Section 6: Icon ID scan in tile component data ───
        DumpIconIdScan(sb, addon);

        // ─── Section 7: Safe memory read of addon extension area ───
        sb.AppendLine("--- ADDON MEMORY (safe read, +0x200 to +0xA00) ---");
        SafeHexDump(sb, (nint)addon, 0x200, 0xA00, 32);
        sb.AppendLine();

        // ─── Section 8: Other visible component nodes ───
        sb.AppendLine("--- OTHER VISIBLE COMPONENT NODES ---");
        for (int i = 0; i < uld.NodeListCount; i++)
        {
            try
            {
                var n = uld.NodeList[i];
                if (n == null) continue;

                bool vis = false;
                try { vis = n->IsVisible(); } catch { }
                if (!vis) continue;
                if (n->Width == 34 && n->Height == 45) continue; // Already in section 3
                if ((int)n->Type < 1000) continue; // Only component nodes

                sb.AppendLine($"  NodeList[{i}] id={n->NodeId} type={(int)n->Type} pos=({n->X:F0},{n->Y:F0}) size=({n->Width},{n->Height})");
                DumpNodeDeep(sb, n, indent: 4);
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  NodeList[{i}] ERROR: {ex.Message}");
            }
        }
        sb.AppendLine();
    }

    private static void DumpResolvedHandSnapshot(StringBuilder sb, AtkUnitBase* addon, IconIdCapture? iconCapture, MahjongIconMap? iconMap)
    {
        sb.AppendLine("--- RESOLVED HAND SNAPSHOT ---");
        var snapshot = MahjongHandReader.Read(addon, iconCapture, iconMap);
        sb.Append(snapshot.ToDisplayText());

        var learned = iconMap?.Snapshot();
        if (learned != null && learned.Count > 0)
        {
            sb.AppendLine("Learned icon map:");
            foreach (var pair in learned.OrderBy(pair => pair.Key))
                sb.AppendLine($"  {pair.Key} -> {pair.Value}");
        }

        sb.AppendLine();
    }

    private static void DumpClientMahjongState(StringBuilder sb)
    {
        var probeSequence = System.Threading.Interlocked.Increment(ref _probeSequence);
        var probeUtc = DateTime.UtcNow;
        var probeTick = Environment.TickCount64;

        sb.AppendLine("--- CLIENT MAHJONG STATE PROBES ---");
        sb.AppendLine($"  ProbeSequence={probeSequence}");
        sb.AppendLine($"  ProbeUtcTimestamp={probeUtc:O}");
        sb.AppendLine($"  ProbeTickCount64={probeTick}");

        try
        {
            var uiState = UIState.Instance();
            if (uiState == null)
            {
                sb.AppendLine("  UIState.Instance() = null");
            }
            else
            {
                var uiStateAddr = (nint)uiState;
                var emjAddr = (nint)(&uiState->Emj);
                sb.AppendLine($"  UIState=0x{uiStateAddr:X}");
                DumpOpaqueStruct(sb, "UIState->Emj", emjAddr, 0x38, 16, 0x40, 4);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  UIState probe failed: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            var emjModule = EmjModule.Instance();
            if (emjModule == null)
            {
                sb.AppendLine("  EmjModule.Instance() = null");
            }
            else
            {
                var emjModuleAddr = (nint)emjModule;
                sb.AppendLine($"  EmjModule=0x{emjModuleAddr:X} tileSet={emjModule->TileSet} hideHints={emjModule->HideHints} hideDangerousTileMarker={emjModule->HideDangerousTileMarker} hideChatLog={emjModule->HideChatLog} hideTileNames={emjModule->HideTileNames} hiRes={emjModule->ShowHighResolutionLayout}");
                DumpOpaqueStruct(sb, "EmjModule", emjModuleAddr, 0xD0, 16, 0x40, 4);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  EmjModule probe failed: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            var agentModule = AgentModule.Instance();
            if (agentModule == null)
            {
                sb.AppendLine("  AgentModule.Instance() = null");
            }
            else
            {
                var agentModuleAddr = (nint)agentModule;
                var emjAgent = agentModule->GetAgentByInternalId(AgentId.Emj);
                sb.AppendLine($"  AgentModule=0x{agentModuleAddr:X}");
                if (emjAgent == null)
                {
                    sb.AppendLine("  AgentId.Emj = null");
                }
                else
                {
                    var emjAgentAddr = (nint)emjAgent;
                    sb.AppendLine($"  AgentId.Emj=0x{emjAgentAddr:X} addonId={emjAgent->AddonId}");
                    DumpOpaqueStruct(sb, "AgentId.Emj", emjAgentAddr, 0xA0, 16, 0x40, 6);
                    DumpAgentEmjDeepPointers(sb, emjAgentAddr);
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  Agent probe failed: {ex.GetType().Name}: {ex.Message}");
        }

        sb.AppendLine();
    }

    private static void DumpAgentEmjDeepPointers(StringBuilder sb, nint agentAddr)
    {
        sb.AppendLine("  AgentId.Emj deep pointers:");

        // Candidate fields in AgentInterface-derived objects where gameplay state pointers often live.
        int[] candidateOffsets = { 0x28, 0x30, 0x40, 0x48, 0x58, 0x60, 0x70 };

        foreach (var off in candidateOffsets)
        {
            if (!IsReadable(agentAddr + off, 8))
            {
                sb.AppendLine($"    +0x{off:X2}: (not readable)");
                continue;
            }

            var ptr = Marshal.ReadIntPtr(agentAddr + off);
            if (ptr == 0 || ptr < 0x10000)
            {
                sb.AppendLine($"    +0x{off:X2}: 0x{ptr:X} (null/invalid)");
                continue;
            }

            sb.AppendLine($"    +0x{off:X2}: 0x{ptr:X}");

            int dumpSize = 0x200;
            while (dumpSize > 0 && !IsReadable(ptr, dumpSize))
                dumpSize /= 2;

            if (dumpSize <= 0)
            {
                sb.AppendLine("      target not readable");
                continue;
            }

            DumpOpaqueStruct(sb, $"AgentId.Emj+0x{off:X2}", ptr, dumpSize, 16, 0x40, 2);
            DumpNestedPointers(sb, ptr, dumpSize, maxNested: 3);
        }
    }

    private static void DumpNestedPointers(StringBuilder sb, nint baseAddr, int size, int maxNested)
    {
        sb.AppendLine("      nested pointers:");

        int dumped = 0;
        for (int off = 0; off + 8 <= size && dumped < maxNested; off += 8)
        {
            if (!IsReadable(baseAddr + off, 8))
                continue;

            var ptr = Marshal.ReadIntPtr(baseAddr + off);
            if (ptr == 0 || ptr < 0x10000 || !IsReadable(ptr, 0x40))
                continue;

            dumped++;
            sb.AppendLine($"        +0x{off:X2}: 0x{ptr:X}");
            SafeHexDump(sb, ptr, 0x00, 0x40, 16, "          ");
        }

        if (dumped == 0)
            sb.AppendLine("        (none)");
    }

    private static void DumpOpaqueStruct(StringBuilder sb, string label, nint addr, int size, int bytesPerLine, int pointerTargetDumpSize, int maxPointerTargets)
    {
        sb.AppendLine($"  {label} @ 0x{addr:X} size=0x{size:X}");
        if (!IsReadable(addr, size))
        {
            sb.AppendLine("    (not readable)");
            return;
        }

        SafeHexDump(sb, addr, 0x00, size, bytesPerLine, "    ");
        DumpInterestingDwords(sb, addr, size);
        DumpPointerFields(sb, addr, size, pointerTargetDumpSize, maxPointerTargets);
    }

    private static void DumpInterestingDwords(StringBuilder sb, nint addr, int size)
    {
        sb.AppendLine("    dwords:");

        bool any = false;
        for (int off = 0; off + 4 <= size; off += 4)
        {
            if (!IsReadable(addr + off, 4))
                continue;

            int signed = Marshal.ReadInt32(addr + off);
            uint unsigned = unchecked((uint)signed);
            if (unsigned == 0)
                continue;

            any = true;
            sb.AppendLine($"      +0x{off:X2}: i32={signed} u32={unsigned} 0x{unsigned:X8}");
        }

        if (!any)
            sb.AppendLine("      (all zero)");
    }

    private static void DumpPointerFields(StringBuilder sb, nint addr, int size, int targetDumpSize, int maxTargets)
    {
        sb.AppendLine("    pointers:");

        bool any = false;
        int dumpedTargets = 0;
        var seenTargets = new HashSet<nint>();

        for (int off = 0; off + 8 <= size; off += 8)
        {
            if (!IsReadable(addr + off, 8))
                continue;

            nint ptr = Marshal.ReadIntPtr(addr + off);
            if (ptr == 0 || ptr < 0x10000)
                continue;

            any = true;
            bool readable = IsReadable(ptr, 1);
            sb.AppendLine($"      +0x{off:X2}: 0x{ptr:X} {(readable ? "(readable)" : "(unreadable)")}");

            if (!readable || dumpedTargets >= maxTargets || !seenTargets.Add(ptr))
                continue;

            int dumpSize = targetDumpSize;
            while (dumpSize > 0 && !IsReadable(ptr, dumpSize))
                dumpSize /= 2;

            if (dumpSize <= 0)
                continue;

            dumpedTargets++;
            SafeHexDump(sb, ptr, 0x00, dumpSize, 16, "        ");
        }

        if (!any)
            sb.AppendLine("      (none)");
    }

    // ─── UI element discovery for wind, riichi, call prompts, game phase ───

    /// <summary>
    /// Scans all visible text nodes and non-tile component nodes in the EmjL addon
    /// to discover wind indicators, riichi status, call prompt buttons, round/turn info,
    /// and other game state UI elements.
    /// </summary>
    private static void DumpUiElementDiscovery(StringBuilder sb, AtkUnitBase* addon)
    {
        sb.AppendLine("--- UI ELEMENT DISCOVERY (text nodes, buttons, indicators) ---");

        var uld = addon->UldManager;

        // 1) All visible text nodes — these carry wind names, round info, score, etc.
        sb.AppendLine("Visible text nodes:");
        for (int i = 0; i < uld.NodeListCount; i++)
        {
            try
            {
                var n = uld.NodeList[i];
                if (n == null) continue;
                if (n->Type != NodeType.Text) continue;

                bool vis = false;
                try { vis = n->IsVisible(); } catch { }
                if (!vis) continue;

                var txt = (AtkTextNode*)n;
                string text = "";
                try
                {
                    text = Marshal.PtrToStringUTF8((nint)txt->NodeText.StringPtr.Value) ?? "";
                }
                catch { text = "(read-err)"; }

                uint parentId = 0;
                try { if (n->ParentNode != null) parentId = n->ParentNode->NodeId; } catch { }

                sb.AppendLine($"  [{i,3}] id={n->NodeId,8} pos=({n->X:F0},{n->Y:F0}) size=({n->Width},{n->Height}) parent={parentId} text=\"{text.Replace("\n", "\\n").Trim()}\"");
            }
            catch { }
        }
        sb.AppendLine();

        // 2) All visible text nodes inside component nodes (buttons, labels inside containers)
        sb.AppendLine("Visible component-hosted text nodes:");
        for (int i = 0; i < uld.NodeListCount; i++)
        {
            try
            {
                var n = uld.NodeList[i];
                if (n == null) continue;
                if ((int)n->Type < 1000) continue;

                bool vis = false;
                try { vis = n->IsVisible(); } catch { }
                if (!vis) continue;

                // Skip tile-sized components (already classified)
                if ((n->Width == 34 && n->Height == 45) || (n->Width == 42 && n->Height == 55))
                    continue;

                var comp = (AtkComponentNode*)n;
                if (comp->Component == null) continue;

                var childUld = comp->Component->UldManager;
                var texts = new List<string>();

                for (int j = 0; j < childUld.NodeListCount && j < 64; j++)
                {
                    try
                    {
                        var cn = childUld.NodeList[j];
                        if (cn == null || cn->Type != NodeType.Text) continue;

                        bool cVis = false;
                        try { cVis = cn->IsVisible(); } catch { }
                        if (!cVis) continue;

                        var txt = (AtkTextNode*)cn;
                        string text = "";
                        try
                        {
                            text = Marshal.PtrToStringUTF8((nint)txt->NodeText.StringPtr.Value) ?? "";
                        }
                        catch { text = "(read-err)"; }

                        if (!string.IsNullOrWhiteSpace(text))
                            texts.Add($"child[{j}]id={cn->NodeId} \"{text.Replace("\n", "\\n").Trim()}\"");
                    }
                    catch { }
                }

                if (texts.Count > 0)
                    sb.AppendLine($"  [{i,3}] id={n->NodeId,8} type={(int)n->Type} pos=({n->X:F0},{n->Y:F0}) size=({n->Width},{n->Height}): {string.Join(" | ", texts)}");
            }
            catch { }
        }
        sb.AppendLine();

        // 3) Non-tile visible component nodes summary (potential buttons, indicators)
        sb.AppendLine("Non-tile visible component nodes (potential buttons/indicators):");
        for (int i = 0; i < uld.NodeListCount; i++)
        {
            try
            {
                var n = uld.NodeList[i];
                if (n == null) continue;
                if ((int)n->Type < 1000) continue;

                bool vis = false;
                try { vis = n->IsVisible(); } catch { }
                if (!vis) continue;

                // Skip tile-sized components
                if ((n->Width == 34 && n->Height == 45) || (n->Width == 42 && n->Height == 55))
                    continue;

                uint parentId = 0;
                try { if (n->ParentNode != null) parentId = n->ParentNode->NodeId; } catch { }

                // Count children and identify child types
                int childCount = 0;
                int textChildren = 0;
                int imageChildren = 0;
                try
                {
                    var comp = (AtkComponentNode*)n;
                    if (comp->Component != null)
                    {
                        var childUld = comp->Component->UldManager;
                        childCount = childUld.NodeListCount;
                        for (int j = 0; j < childUld.NodeListCount && j < 64; j++)
                        {
                            try
                            {
                                var cn = childUld.NodeList[j];
                                if (cn == null) continue;
                                if (cn->Type == NodeType.Text) textChildren++;
                                else if (cn->Type == NodeType.Image) imageChildren++;
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                sb.AppendLine($"  [{i,3}] id={n->NodeId,8} type={(int)n->Type} pos=({n->X:F0},{n->Y:F0}) size=({n->Width},{n->Height}) parent={parentId} children={childCount} (text={textChildren},img={imageChildren})");
            }
            catch { }
        }
        sb.AppendLine();

        // 4) Visible NineGrid/Image nodes that are NOT tile-related (riichi sticks, indicators)
        sb.AppendLine("Visible non-tile image nodes (potential riichi sticks, indicators):");
        for (int i = 0; i < uld.NodeListCount; i++)
        {
            try
            {
                var n = uld.NodeList[i];
                if (n == null) continue;
                if (n->Type != NodeType.Image && n->Type != NodeType.NineGrid) continue;

                bool vis = false;
                try { vis = n->IsVisible(); } catch { }
                if (!vis) continue;

                uint parentId = 0;
                try { if (n->ParentNode != null) parentId = n->ParentNode->NodeId; } catch { }

                string extra = "";
                if (n->Type == NodeType.Image)
                {
                    try
                    {
                        var img = (AtkImageNode*)n;
                        extra = $" partId={img->PartId} flags=0x{img->Flags:X}";
                    }
                    catch { }
                }

                sb.AppendLine($"  [{i,3}] id={n->NodeId,8} type={n->Type} pos=({n->X:F0},{n->Y:F0}) size=({n->Width},{n->Height}) parent={parentId}{extra}");
            }
            catch { }
        }
        sb.AppendLine();

        // 5) AtkValues summary focusing on game state values (wind, round, scores, phase)
        sb.AppendLine("AtkValues game state candidates:");
        try
        {
            var valCount = addon->AtkValuesCount;
            for (int i = 0; i < valCount && i < 500; i++)
            {
                try
                {
                    var val = addon->AtkValues[i];
                    // Highlight non-zero ints that look like game state (small values = wind/round/phase)
                    if (val.Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int ||
                        val.Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt)
                    {
                        var v = val.Int;
                        // Small ints (0-20) could be wind, round, phase, riichi state
                        if (v >= 0 && v <= 20)
                            sb.AppendLine($"  [{i,3}] type={val.Type,-14} int={v} <<< SMALL_INT (wind/round/phase?)");
                        // Score-like values
                        else if (v >= 1000 && v <= 100000 && v % 100 == 0)
                            sb.AppendLine($"  [{i,3}] type={val.Type,-14} int={v} <<< SCORE?");
                    }
                    // Highlight non-empty strings (tile names, wind names, status labels)
                    else if (val.Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String ||
                             val.Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String8)
                    {
                        try
                        {
                            string s = val.String.ToString();
                            if (!string.IsNullOrWhiteSpace(s))
                                sb.AppendLine($"  [{i,3}] type={val.Type,-14} str=\"{s}\" <<< STRING");
                        }
                        catch { }
                    }
                    else if (val.Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Bool)
                    {
                        sb.AppendLine($"  [{i,3}] type={val.Type,-14} bool={val.Int != 0} <<< BOOL (toggle?)");
                    }
                }
                catch { }
            }
        }
        catch { }
        sb.AppendLine();
    }

    // ─── Spatial tile classification for discard pool / dora discovery ───

    /// <summary>
    /// Dumps all visible non-tile component nodes and their text/image children
    /// to help identify call prompt buttons (chi, pon, kan, ron, tsumo, riichi, skip).
    /// </summary>
    private static void DumpCallPromptDiscovery(StringBuilder sb, AtkUnitBase* addon)
    {
        sb.AppendLine("--- CALL PROMPT DISCOVERY ---");
        try
        {
            var uld = addon->UldManager;
            for (int i = 0; i < uld.NodeListCount; i++)
            {
                try
                {
                    var n = uld.NodeList[i];
                    if (n == null) continue;
                    var type = (int)n->Type;
                    if (type < 1000) continue;

                    // Skip known tile types
                    if (type == 1021 || type == 1022 || type == 1023 || type == 1024 || type == 1055)
                        continue;

                    bool vis = false;
                    try { vis = n->IsVisible(); } catch { }
                    if (!vis) continue;

                    // Skip tile-sized nodes
                    if ((n->Width == 34 && n->Height == 45) || (n->Width == 42 && n->Height == 55))
                        continue;

                    var comp = (AtkComponentNode*)n;
                    if (comp->Component == null) continue;
                    var childUld = comp->Component->UldManager;

                    // Collect text and image info from children
                    var childInfo = new List<string>();
                    for (int j = 0; j < childUld.NodeListCount && j < 64; j++)
                    {
                        try
                        {
                            var cn = childUld.NodeList[j];
                            if (cn == null) continue;

                            bool cVis = false;
                            try { cVis = cn->IsVisible(); } catch { }

                            if (cn->Type == NodeType.Text)
                            {
                                var txt = (AtkTextNode*)cn;
                                string text = "";
                                try { text = Marshal.PtrToStringUTF8((nint)txt->NodeText.StringPtr.Value) ?? ""; } catch { }
                                if (!string.IsNullOrWhiteSpace(text))
                                    childInfo.Add($"text[{j}]={text.Trim()} vis={cVis} id={cn->NodeId}");
                            }
                            else if (cn->Type == NodeType.Image)
                            {
                                uint iconId = 0;
                                try
                                {
                                    var img = (AtkImageNode*)cn;
                                    if (img->PartsList != null && img->PartId < img->PartsList->PartCount)
                                    {
                                        var part = img->PartsList->Parts[img->PartId];
                                        if (part.UldAsset != null && part.UldAsset->AtkTexture.Resource != null)
                                            iconId = part.UldAsset->AtkTexture.Resource->IconId;
                                    }
                                }
                                catch { }
                                if (iconId > 0)
                                    childInfo.Add($"img[{j}]=icon{iconId} vis={cVis} id={cn->NodeId}");
                            }
                        }
                        catch { }
                    }

                    // Only dump components that have text or icon children
                    if (childInfo.Count > 0)
                    {
                        uint parentId = 0;
                        try { if (n->ParentNode != null) parentId = n->ParentNode->NodeId; } catch { }
                        sb.AppendLine($"  [{i,3}] id={n->NodeId} type={type} pos=({n->X:F0},{n->Y:F0}) size=({n->Width},{n->Height}) parent={parentId}");
                        foreach (var info in childInfo)
                            sb.AppendLine($"      {info}");
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  ERROR: {ex.Message}");
        }
        sb.AppendLine();
    }

    /// <summary>
    /// Scans for potential dora indicator tiles by walking the RootNode child tree.
    /// During gameplay, dora indicators may be rendered in a special board area component,
    /// not as standalone 34x45 tiles in the NodeList. This dump helps discover the structure.
    /// Also checks the center board node (NodeID 16 area) for tile-like image children.
    /// </summary>
    private static void DumpDoraDiscovery(StringBuilder sb, AtkUnitBase* addon, IconIdCapture? iconCapture, MahjongIconMap? iconMap)
    {
        sb.AppendLine("--- DORA INDICATOR DISCOVERY ---");

        try
        {
            var root = addon->RootNode;
            if (root == null)
            {
                sb.AppendLine("  (RootNode is null)");
                sb.AppendLine();
                return;
            }

            // Walk all visible component nodes in the NodeList and look for
            // tile-sized image children with icon IDs in the Mahjong range (76041-76077).
            // Exclude known discard types (1021-1024) and hand type (1055).
            var uld = addon->UldManager;
            sb.AppendLine("Visible components with Mahjong-range icons (excluding discards/hand):");
            for (int i = 0; i < uld.NodeListCount; i++)
            {
                try
                {
                    var n = uld.NodeList[i];
                    if (n == null) continue;
                    var type = (int)n->Type;
                    if (type < 1000) continue;

                    // Skip known types
                    if (type == 1021 || type == 1022 || type == 1023 || type == 1024 || type == 1055)
                        continue;

                    bool vis = false;
                    try { vis = n->IsVisible(); } catch { }
                    if (!vis) continue;

                    // Search component's children for icon-mode image nodes
                    var comp = (AtkComponentNode*)n;
                    if (comp->Component == null) continue;
                    var childUld = comp->Component->UldManager;

                    for (int j = 0; j < childUld.NodeListCount && j < 64; j++)
                    {
                        try
                        {
                            var cn = childUld.NodeList[j];
                            if (cn == null || cn->Type != NodeType.Image) continue;

                            var img = (AtkImageNode*)cn;
                            // Try reading icon ID from texture resource chain
                            uint iconId = 0;
                            try
                            {
                                if (img->PartsList != null && img->PartId < img->PartsList->PartCount)
                                {
                                    var part = img->PartsList->Parts[img->PartId];
                                    if (part.UldAsset != null && part.UldAsset->AtkTexture.Resource != null)
                                        iconId = part.UldAsset->AtkTexture.Resource->IconId;
                                }
                            }
                            catch { }

                            if (iconId >= 76041 && iconId <= 76077)
                            {
                                var tileCode = iconMap?.Resolve(iconId) ?? $"ICON_{iconId}";
                                uint parentId = 0;
                                try { if (n->ParentNode != null) parentId = n->ParentNode->NodeId; } catch { }
                                sb.AppendLine($"  [{i,3}] id={n->NodeId} type={type} pos=({n->X:F0},{n->Y:F0}) size=({n->Width},{n->Height}) parent={parentId} -> child[{j}] iconId={iconId} tile={tileCode}");
                            }
                        }
                        catch { }
                    }

                    // Also check via IconIdCapture hook
                    if (iconCapture != null)
                    {
                        for (int j = 0; j < childUld.NodeListCount && j < 64; j++)
                        {
                            try
                            {
                                var cn = childUld.NodeList[j];
                                if (cn == null || cn->Type != NodeType.Image) continue;
                                var addr = (nint)cn;
                                if (iconCapture.IconMap.TryGetValue(addr, out var capturedIcon) && capturedIcon >= 76041 && capturedIcon <= 76077)
                                {
                                    var tileCode = iconMap?.Resolve(capturedIcon) ?? $"ICON_{capturedIcon}";
                                    sb.AppendLine($"  [{i,3}] id={n->NodeId} type={type} (hook) -> child[{j}] iconId={capturedIcon} tile={tileCode}");
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  ERROR: {ex.Message}");
        }

        sb.AppendLine();
    }

    /// <summary>
    /// Dumps all visible tile-like nodes grouped by spatial position to discover
    /// discard pools, dora indicators, and other tile regions in the EmjL addon.
    /// Tile nodes are either type 1045 (34x45, used for discard/dora) or type 1055 (42x55, player hand).
    /// Grouping by Y-band helps identify which player's discard pool each belongs to.
    /// </summary>
    private static void DumpSpatialTileClassification(StringBuilder sb, AtkUnitBase* addon, IconIdCapture? iconCapture, MahjongIconMap? iconMap)
    {
        sb.AppendLine("--- SPATIAL TILE CLASSIFICATION ---");

        var uld = addon->UldManager;
        var allTiles = new List<(int NodeIndex, uint NodeId, int NodeType, bool Visible, float X, float Y, int Width, int Height, uint IconId, string? TileCode, float ParentX, float ParentY)>();

        for (int i = 0; i < uld.NodeListCount; i++)
        {
            try
            {
                var n = uld.NodeList[i];
                if (n == null) continue;

                var type = (int)n->Type;
                // Include all component tile types: 1045 (34x45 discards), 1055 (42x55 hand),
                // 1022 (drawn tile / discard), and any other tile-like components
                if (type < 1000) continue;

                bool vis = false;
                try { vis = n->IsVisible(); } catch { }

                // Include both visible and invisible tiles for full layout mapping
                var isTileSize = (n->Width == 34 && n->Height == 45) ||
                                 (n->Width == 42 && n->Height == 55) ||
                                 (n->Width == 40 && n->Height == 52);

                if (!isTileSize) continue;

                uint iconId = 0;
                if (vis)
                    EmjUiReader.TryFindIconPublic(n, iconCapture, out iconId);

                string? tileCode = iconId > 0 ? iconMap?.Resolve(iconId) : null;

                // Get parent node position for absolute positioning context
                float parentX = 0, parentY = 0;
                try
                {
                    if (n->ParentNode != null)
                    {
                        parentX = n->ParentNode->X;
                        parentY = n->ParentNode->Y;
                    }
                }
                catch { }

                allTiles.Add((i, n->NodeId, type, vis, n->X, n->Y, n->Width, n->Height, iconId, tileCode, parentX, parentY));
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  NodeList[{i}] scan error: {ex.Message}");
            }
        }

        sb.AppendLine($"Total tile-sized component nodes found: {allTiles.Count}");
        sb.AppendLine($"  Visible: {allTiles.Count(t => t.Visible)}");
        sb.AppendLine($"  With icon: {allTiles.Count(t => t.IconId > 0)}");
        sb.AppendLine();

        // Group by size for classification
        var bySize = allTiles.GroupBy(t => $"{t.Width}x{t.Height}").OrderBy(g => g.Key);
        foreach (var group in bySize)
        {
            sb.AppendLine($"  Size {group.Key}: {group.Count()} total, {group.Count(t => t.Visible)} visible, {group.Count(t => t.IconId > 0)} with icon");
        }
        sb.AppendLine();

        // Group by node type
        var byType = allTiles.GroupBy(t => t.NodeType).OrderBy(g => g.Key);
        foreach (var group in byType)
        {
            sb.AppendLine($"  Type {group.Key}: {group.Count()} total, {group.Count(t => t.Visible)} visible");
        }
        sb.AppendLine();

        // Group visible tiles with icons by Y-band (quantized to 20px bands)
        // This helps identify spatial regions: player hand, discard pools, dora area
        sb.AppendLine("Visible tiles with icons, grouped by Y-band (20px):");
        var visibleWithIcons = allTiles.Where(t => t.Visible && t.IconId > 0).OrderBy(t => t.Y).ThenBy(t => t.X).ToList();
        var yBands = visibleWithIcons.GroupBy(t => ((int)t.Y / 20) * 20).OrderBy(g => g.Key);
        foreach (var band in yBands)
        {
            sb.AppendLine($"  Y~{band.Key}: {band.Count()} tiles");
            foreach (var t in band.OrderBy(t => t.X))
            {
                var tile = t.TileCode ?? (t.IconId > 0 ? $"ICON_{t.IconId}" : "(no-icon)");
                sb.AppendLine($"    [{t.NodeIndex,3}] id={t.NodeId,8} type={t.NodeType} pos=({t.X:F0},{t.Y:F0}) size=({t.Width},{t.Height}) parent=({t.ParentX:F0},{t.ParentY:F0}) {tile}");
            }
        }
        sb.AppendLine();

        // Group ALL visible tile-sized nodes by parent node ID for structural grouping
        sb.AppendLine("Visible tile nodes grouped by parent node:");
        var visibleTiles = allTiles.Where(t => t.Visible).ToList();
        // Read parent node IDs
        var parentGroups = new Dictionary<uint, List<(int NodeIndex, uint NodeId, int NodeType, float X, float Y, int Width, int Height, uint IconId, string? TileCode)>>();
        for (int i = 0; i < uld.NodeListCount; i++)
        {
            var tile = visibleTiles.FirstOrDefault(t => t.NodeIndex == i);
            if (tile.NodeIndex == 0 && i != 0) continue;
            if (!tile.Visible) continue;

            try
            {
                var n = uld.NodeList[i];
                if (n == null || n->ParentNode == null) continue;
                var parentId = n->ParentNode->NodeId;
                if (!parentGroups.ContainsKey(parentId))
                    parentGroups[parentId] = new();
                parentGroups[parentId].Add((tile.NodeIndex, tile.NodeId, tile.NodeType, tile.X, tile.Y, tile.Width, tile.Height, tile.IconId, tile.TileCode));
            }
            catch { }
        }
        foreach (var (parentId, children) in parentGroups.OrderBy(p => p.Key))
        {
            var withIcons = children.Count(c => c.IconId > 0);
            sb.AppendLine($"  Parent id={parentId}: {children.Count} children, {withIcons} with icon");
            foreach (var c in children.OrderBy(c => c.X).ThenBy(c => c.Y))
            {
                var tile = c.TileCode ?? (c.IconId > 0 ? $"ICON_{c.IconId}" : "(no-icon)");
                sb.AppendLine($"    [{c.NodeIndex,3}] id={c.NodeId,8} type={c.NodeType} pos=({c.X:F0},{c.Y:F0}) size=({c.Width},{c.Height}) {tile}");
            }
        }
        sb.AppendLine();

        // List all visible 34x45 nodes (candidate discard/dora tiles) explicitly
        sb.AppendLine("All visible 34x45 tile nodes (candidate discard/dora):");
        var smallTiles = allTiles.Where(t => t.Visible && t.Width == 34 && t.Height == 45).OrderBy(t => t.Y).ThenBy(t => t.X).ToList();
        foreach (var t in smallTiles)
        {
            var tile = t.TileCode ?? (t.IconId > 0 ? $"ICON_{t.IconId}" : "(no-icon)");
            sb.AppendLine($"  [{t.NodeIndex,3}] id={t.NodeId,8} type={t.NodeType} pos=({t.X:F0},{t.Y:F0}) parent=({t.ParentX:F0},{t.ParentY:F0}) {tile}");
        }
        sb.AppendLine();

        // Node index range summary for key tile sizes
        if (allTiles.Count > 0)
        {
            sb.AppendLine("Node index ranges:");
            var hand55 = allTiles.Where(t => t.Width == 42 && t.Height == 55).ToList();
            if (hand55.Count > 0)
                sb.AppendLine($"  42x55 (hand): indices {hand55.Min(t => t.NodeIndex)}..{hand55.Max(t => t.NodeIndex)}");
            var discard45 = allTiles.Where(t => t.Width == 34 && t.Height == 45).ToList();
            if (discard45.Count > 0)
                sb.AppendLine($"  34x45 (discard/dora): indices {discard45.Min(t => t.NodeIndex)}..{discard45.Max(t => t.NodeIndex)}");
            var draw52 = allTiles.Where(t => t.Width == 40 && t.Height == 52).ToList();
            if (draw52.Count > 0)
                sb.AppendLine($"  40x52 (draw/other): indices {draw52.Min(t => t.NodeIndex)}..{draw52.Max(t => t.NodeIndex)}");
        }
        sb.AppendLine();
    }

    // ─── AtkValues ───

    private static void DumpAtkValues(StringBuilder sb, AtkUnitBase* addon)
    {
        sb.AppendLine("--- ATK VALUES ---");
        try
        {
            var valCount = addon->AtkValuesCount;
            sb.AppendLine($"AtkValuesCount={valCount}");

            for (int i = 0; i < valCount && i < 500; i++)
            {
                try
                {
                    var val = addon->AtkValues[i];
                    var vType = val.Type;
                    sb.Append($"  [{i,3}] type={vType,-14}");

                    switch (vType)
                    {
                        case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int:
                            sb.Append($" int={val.Int}");
                            break;
                        case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt:
                            sb.Append($" uint={val.UInt}");
                            break;
                        case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Bool:
                            sb.Append($" bool={val.Int != 0}");
                            break;
                        case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String:
                        case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String8:
                            try
                            {
                                sb.Append($" str=\"{val.String}\"");
                            }
                            catch
                            {
                                sb.Append($" str=(read failed)");
                            }
                            break;
                        case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.ManagedString:
                            sb.Append($" managed_raw={val.Int}");
                            break;
                        default:
                            sb.Append($" raw={val.Int}");
                            break;
                    }
                    sb.AppendLine();
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"  [{i,3}] ERROR: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  AtkValues dump failed: {ex.Message}");
        }
        sb.AppendLine();
    }

    // ─── Captured Icon IDs from LoadIconTexture hook ───

    private static void DumpCapturedIconIds(StringBuilder sb, AtkUnitBase* addon, IconIdCapture? capture)
    {
        sb.AppendLine("--- CAPTURED ICON IDS (LoadIconTexture hook) ---");
        if (capture == null)
        {
            sb.AppendLine("  (hook not available)");
            sb.AppendLine();
            return;
        }

        sb.AppendLine($"  Total captured entries: {capture.IconMap.Count}");
        var recent = capture.GetRecentCaptures();
        sb.AppendLine($"  Recent captures: {recent.Count}");
        foreach (var r in recent)
            sb.AppendLine($"    t={r.time:HH:mm:ss} addr=0x{r.addr:X} iconId={r.iconId}");
        sb.AppendLine();

        var uld = addon->UldManager;

        // Hand tiles (34x45, type >= 1000)
        sb.AppendLine("  Hand tiles (34x45 components, all child images):");
        for (int i = 0; i < uld.NodeListCount; i++)
        {
            try
            {
                var n = uld.NodeList[i];
                if (n == null) continue;
                bool vis = false;
                try { vis = n->IsVisible(); } catch { }
                if (!vis) continue;
                if (n->Width != 34 || n->Height != 45) continue;
                if ((int)n->Type < 1000) continue;

                bool printed = false;
                DumpCapturedIconsFromNodeTree(sb, capture, n, $"    NodeList[{i,3}] id={n->NodeId,3} type={(int)n->Type}", ref printed);

                if (!printed)
                    sb.AppendLine($"    NodeList[{i,3}] id={n->NodeId,3} type={(int)n->Type} -> iconId=(none)");
            }
            catch { }
        }

        // Also show drawn tile (type 1022, index 107 area, 40x52 size)
        sb.AppendLine();
        sb.AppendLine("  Drawn tile (40x52 or type=1022):");
        for (int i = 0; i < uld.NodeListCount; i++)
        {
            try
            {
                var n = uld.NodeList[i];
                if (n == null) continue;
                if ((int)n->Type != 1022) continue;
                bool vis = false;
                try { vis = n->IsVisible(); } catch { }
                if (!vis) continue;

                bool printed = false;
                DumpCapturedIconsFromNodeTree(sb, capture, n, $"    NodeList[{i,3}] id={n->NodeId,3} type={(int)n->Type}", ref printed);

                if (!printed)
                    sb.AppendLine($"    NodeList[{i,3}] id={n->NodeId,3} type={(int)n->Type} -> iconId=(none)");
            }
            catch { }
        }

        sb.AppendLine();
        sb.AppendLine("  Full-size hand candidates (42x55 type=1055 components, all child images):");
        for (int i = 0; i < uld.NodeListCount; i++)
        {
            try
            {
                var n = uld.NodeList[i];
                if (n == null) continue;
                if ((int)n->Type != 1055) continue;
                bool vis = false;
                try { vis = n->IsVisible(); } catch { }
                if (!vis) continue;
                if (n->Width != 42 || n->Height != 55) continue;

                bool printed = false;
                DumpCapturedIconsFromNodeTree(sb, capture, n, $"    NodeList[{i,3}] id={n->NodeId,7} type={(int)n->Type}", ref printed);

                if (!printed)
                    sb.AppendLine($"    NodeList[{i,3}] id={n->NodeId,7} type={(int)n->Type} -> iconId=(none)");
            }
            catch { }
        }

        sb.AppendLine();
    }

    private static void DumpCapturedIconsFromNodeTree(StringBuilder sb, IconIdCapture capture, AtkResNode* root, string prefix, ref bool printed)
    {
        if (root == null) return;

        try
        {
            if (root->Type == NodeType.Image)
            {
                var img = (AtkImageNode*)root;
                uint iconId = capture.GetIconId((nint)img);
                if (iconId > 0)
                {
                    sb.AppendLine($"{prefix} childId={root->NodeId,3} flags=0x{(byte)img->Flags:X2} addr=0x{(nint)img:X} -> iconId={iconId}");
                    printed = true;
                }
            }

            for (var child = root->ChildNode; child != null; child = child->NextSiblingNode)
                DumpCapturedIconsFromNodeTree(sb, capture, child, prefix, ref printed);

            if ((int)root->Type >= 1000)
            {
                var comp = (AtkComponentNode*)root;
                if (comp->Component != null)
                {
                    var childUld = comp->Component->UldManager;
                    for (int j = 0; j < childUld.NodeListCount && j < 50; j++)
                    {
                        var child = childUld.NodeList[j];
                        if (child == null) continue;
                        DumpCapturedIconsFromNodeTree(sb, capture, child, prefix, ref printed);
                    }
                }
            }
        }
        catch { }
    }

    // ─── Pointer Fingerprint Comparison ───

    /// <summary>
    /// Collects AtkImageNode+0xC0 pointer for every visible 34x45 tile's face image (flags=0x80),
    /// then groups them by pointer value, inner pointer (+0x08 of target), and target byte fingerprint.
    /// Also logs current hover state from AtkValues for correlation.
    /// </summary>
    private static void DumpPointerFingerprints(StringBuilder sb, AtkUnitBase* addon)
    {
        sb.AppendLine("--- POINTER FINGERPRINT COMPARISON ---");

        // Log hover state from AtkValues for correlation
        try
        {
            var valCount = addon->AtkValuesCount;
            string hoveredName = "(none)";
            int hoveredIconId = 0;

            if (valCount > 2)
            {
                try
                {
                    var v1 = addon->AtkValues[1];
                    if (v1.Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String ||
                        v1.Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String8)
                        hoveredName = $"{v1.String}";
                }
                catch { }

                try
                {
                    var v2 = addon->AtkValues[2];
                    if (v2.Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int ||
                        v2.Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt)
                        hoveredIconId = v2.Int;
                }
                catch { }
            }

            // Check AtkValues[3..15] for a possible hovered tile index
            sb.AppendLine($"Hover state: name=\"{hoveredName}\" iconId={hoveredIconId}");
            sb.AppendLine($"AtkValuesCount={valCount}");
            sb.AppendLine("AtkValues[0..15] snapshot:");
            for (int vi = 0; vi < Math.Min(16, (int)valCount); vi++)
            {
                try
                {
                    var v = addon->AtkValues[vi];
                    sb.AppendLine($"  [{vi,2}] type={v.Type,-14} int={v.Int} uint={v.UInt}");
                }
                catch { }
            }

            // Dump ALL remaining AtkValues, highlight icon-range values
            if (valCount > 16)
            {
                sb.AppendLine();
                sb.AppendLine($"AtkValues[16..{valCount - 1}] (full dump):");
                for (int vi = 16; vi < (int)valCount; vi++)
                {
                    try
                    {
                        var v = addon->AtkValues[vi];
                        string marker = "";
                        if (v.Int >= 76000 && v.Int <= 76200) marker = " <<< ICON RANGE";
                        else if (v.Int >= 1 && v.Int <= 200) marker = " <<< SMALL INT";
                        string strContent = "";
                        if (v.Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String8 ||
                            v.Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String ||
                            v.Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.ManagedString)
                        {
                            try
                            {
                                string decoded = v.String.ToString();
                                if (!string.IsNullOrEmpty(decoded))
                                    strContent = $" str=\"{decoded}\"";
                            }
                            catch { strContent = " str=(err)"; }
                        }
                        sb.AppendLine($"  [{vi,3}] type={v.Type,-14} int={v.Int} uint={v.UInt}{marker}{strContent}");
                    }
                    catch { }
                }
            }
        }
        catch { }
        sb.AppendLine();

        sb.AppendLine("Collecting +0xC0 pointer from each tile's face image (flags=0x80)...");
        sb.AppendLine();

        var uld = addon->UldManager;
        var entries = new System.Collections.Generic.List<(int nodeIdx, int nodeId, int nodeType, nint ptrValue, nint partsListAddr, int iconId, string texInfo, string rawIconScan)>();

        for (int i = 0; i < uld.NodeListCount; i++)
        {
            try
            {
                var n = uld.NodeList[i];
                if (n == null) continue;

                bool vis = false;
                try { vis = n->IsVisible(); } catch { }
                if (!vis) continue;
                if (n->Width != 34 || n->Height != 45) continue;
                if ((int)n->Type < 1000) continue;

                var comp = (AtkComponentNode*)n;
                if (comp->Component == null) continue;
                var childUld = comp->Component->UldManager;

                for (int j = 0; j < childUld.NodeListCount && j < 20; j++)
                {
                    try
                    {
                        var child = childUld.NodeList[j];
                        if (child == null || child->Type != NodeType.Image) continue;
                        var img = (AtkImageNode*)child;
                        if ((byte)img->Flags != 0x80) continue;

                        nint imgAddr = (nint)img;
                        nint ptrValue = 0;
                        int iconId = -1;
                        string texInfo = "";

                        // Diagnostic: compare raw +0xC0 against PartsList field
                        nint partsListAddr = (nint)img->PartsList;
                        if (IsReadable(imgAddr + 0xC0, 8))
                            ptrValue = Marshal.ReadIntPtr(imgAddr + 0xC0);

                        // Diagnostic: walk UldAsset texture chain for TextureType and IconID
                        try
                        {
                            if (img->PartsList != null && img->PartId < img->PartsList->PartCount && img->PartsList->Parts != null)
                            {
                                var part = img->PartsList->Parts[img->PartId];
                                if (part.UldAsset != null)
                                {
                                    var tex = part.UldAsset->AtkTexture;
                                    texInfo = $"TexType={(int)tex.TextureType} IsReady={tex.IsTextureReady}";

                                    if (tex.Resource != null)
                                    {
                                        texInfo += $" IconID={tex.Resource->IconId}";
                                        iconId = (int)tex.Resource->IconId;
                                    }
                                    else
                                    {
                                        texInfo += " Resource=NULL";
                                    }
                                }
                            }
                        }
                        catch (Exception ex) { texInfo += $" ERR:{ex.Message}"; }

                        // Also scan the raw AtkImageNode for any int32 in icon ID range
                        string rawIconScan = "";
                        for (int off = 0xC0; off <= 0xCC; off += 4)
                        {
                            if (IsReadable(imgAddr + off, 4))
                            {
                                int v = Marshal.ReadInt32(imgAddr + off);
                                rawIconScan += $" +0x{off:X}={v}(0x{v:X})";
                            }
                        }

                        entries.Add((i, (int)n->NodeId, (int)n->Type, ptrValue, partsListAddr, iconId, texInfo, rawIconScan));
                    }
                    catch { }
                }
            }
            catch { }
        }

        // Print all entries with texture diagnostics
        sb.AppendLine($"Found {entries.Count} tile face images with flags=0x80:");
        sb.AppendLine();
        foreach (var e in entries)
        {
            sb.AppendLine($"  NodeList[{e.nodeIdx,3}] id={e.nodeId,3} type={e.nodeType}");
            sb.AppendLine($"    raw+0xC0=0x{e.ptrValue:X}  PartsList=0x{e.partsListAddr:X}  same={e.ptrValue == e.partsListAddr}");
            sb.AppendLine($"    {e.texInfo}");
            if (e.iconId > 0)
                sb.AppendLine($"    >>> ICON ID = {e.iconId} <<<");
            sb.AppendLine($"    rawScan:{e.rawIconScan}");
        }
        sb.AppendLine();

        // Group by iconId (if any were found)
        sb.AppendLine("--- GROUPED BY ICON ID ---");
        var iconGroups = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<(int nodeIdx, int nodeId)>>();
        foreach (var e in entries)
        {
            if (!iconGroups.ContainsKey(e.iconId))
                iconGroups[e.iconId] = new System.Collections.Generic.List<(int, int)>();
            iconGroups[e.iconId].Add((e.nodeIdx, e.nodeId));
        }
        foreach (var (id, group) in iconGroups)
        {
            sb.Append($"  iconId={id} ({group.Count} tiles):");
            foreach (var (idx, nid) in group) sb.Append($" [{idx}]id={nid}");
            sb.AppendLine();
        }
        sb.AppendLine();

        // Scan AtkComponentBase memory of each tile component for icon-range int32 values
        sb.AppendLine("--- COMPONENT BASE ICON SCAN (scanning 0x00-0x200 for int32 in 76050-76200) ---");
        for (int i = 0; i < uld.NodeListCount; i++)
        {
            try
            {
                var n = uld.NodeList[i];
                if (n == null) continue;
                bool vis = false;
                try { vis = n->IsVisible(); } catch { }
                if (!vis) continue;
                if (n->Width != 34 || n->Height != 45) continue;
                if ((int)n->Type < 1000) continue;

                var comp = (AtkComponentNode*)n;
                if (comp->Component == null) continue;
                nint compAddr = (nint)comp->Component;

                // Scan ComponentBase 0x00-0x400
                var hits = new System.Collections.Generic.List<string>();
                for (int off = 0; off < 0x400; off += 4)
                {
                    if (!IsReadable(compAddr + off, 4)) continue;
                    int val = Marshal.ReadInt32(compAddr + off);
                    if (val >= 76050 && val <= 76200)
                        hits.Add($"comp+0x{off:X}={val}");
                }

                // Also scan the AtkComponentNode itself (parent node memory) 0x00-0xD0
                nint nodeAddr = (nint)n;
                for (int off = 0; off < 0xD0; off += 4)
                {
                    if (!IsReadable(nodeAddr + off, 4)) continue;
                    int val = Marshal.ReadInt32(nodeAddr + off);
                    if (val >= 76050 && val <= 76200)
                        hits.Add($"node+0x{off:X}={val}");
                }

                // Also scan the child image node (face image with flags=0x80) raw memory
                var childUld = comp->Component->UldManager;
                for (int j = 0; j < childUld.NodeListCount && j < 20; j++)
                {
                    try
                    {
                        var child = childUld.NodeList[j];
                        if (child == null || child->Type != NodeType.Image) continue;
                        var img = (AtkImageNode*)child;
                        if ((byte)img->Flags != 0x80) continue;

                        // Scan the UldAsset raw bytes
                        if (img->PartsList != null && img->PartId < img->PartsList->PartCount && img->PartsList->Parts != null)
                        {
                            var part = img->PartsList->Parts[img->PartId];
                            if (part.UldAsset != null)
                            {
                                nint assetAddr = (nint)part.UldAsset;
                                for (int off = 0; off < 0x20; off += 4)
                                {
                                    if (!IsReadable(assetAddr + off, 4)) continue;
                                    int val = Marshal.ReadInt32(assetAddr + off);
                                    if (val >= 76050 && val <= 76200)
                                        hits.Add($"asset+0x{off:X}={val}");
                                }
                            }
                        }
                    }
                    catch { }
                }

                sb.AppendLine($"  NodeList[{i}] id={n->NodeId}: {(hits.Count > 0 ? string.Join(", ", hits) : "(none)")}");
            }
            catch { }
        }
        sb.AppendLine();
    }

    // ─── Deep node inspection (type-safe, no raw pointer reads) ───

    private static void DumpNodeDeep(StringBuilder sb, AtkResNode* node, int indent)
    {
        if (node == null || indent > 40) return;
        var pad = new string(' ', indent);

        // Image node info
        if (node->Type == NodeType.Image)
        {
            DumpImageNode(sb, (AtkImageNode*)node, pad);
        }
        // Text node info
        else if (node->Type == NodeType.Text)
        {
            DumpTextNode(sb, (AtkTextNode*)node, pad);
        }
        // Component node — recurse into internal tree
        else if ((int)node->Type >= 1000)
        {
            DumpComponentNode(sb, node, pad, indent);
        }
        else
        {
            sb.AppendLine($"{pad}[Res] id={node->NodeId} size=({node->Width},{node->Height})");
        }

        // Walk children
        try
        {
            var child = node->ChildNode;
            int steps = 0;
            while (child != null && steps++ < 100)
            {
                try
                {
                    sb.AppendLine($"{pad}  child: id={child->NodeId} type={(int)child->Type} size=({child->Width},{child->Height})");
                    DumpNodeDeep(sb, child, indent + 4);
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"{pad}  child ERROR: {ex.Message}");
                }
                child = child->NextSiblingNode;
            }
        }
        catch { }
    }

    private static void DumpImageNode(StringBuilder sb, AtkImageNode* img, string pad)
    {
        try
        {
            sb.Append($"{pad}[Image] id={img->AtkResNode.NodeId} partId={img->PartId}");
            sb.Append($" flags=0x{img->Flags:X} wrapMode={img->WrapMode}");

            if (img->PartsList != null)
            {
                var pl = img->PartsList;
                sb.Append($" PL(id={pl->Id},count={pl->PartCount})");

                if (img->PartId < pl->PartCount && pl->Parts != null)
                {
                    var part = pl->Parts[img->PartId];
                    sb.Append($" UV({part.U},{part.V},{part.Width},{part.Height})");

                    if (part.UldAsset != null)
                    {
                        sb.Append($" Asset(id={part.UldAsset->Id})");

                        try
                        {
                            var tex = part.UldAsset->AtkTexture;
                            sb.Append($" Tex(ready={tex.IsTextureReady})");
                        }
                        catch { }
                    }
                }
            }

            sb.AppendLine();

            // For icon textures (flags=0x80), dump the +0xC0 pointer for manual analysis
            if ((byte)img->Flags == 0x80)
            {
                nint imgAddr = (nint)img;

                if (IsReadable(imgAddr + 0xC0, 8))
                {
                    nint texChainPtr = Marshal.ReadIntPtr(imgAddr + 0xC0);
                    sb.AppendLine($"{pad}  ImgNode+0xC0 ptr: 0x{texChainPtr:X}");

                    // Dump first 0x40 bytes of the +0xC0 target for manual analysis
                    if (texChainPtr != 0 && IsReadable(texChainPtr, 0x40))
                        SafeHexDump(sb, texChainPtr, 0x00, 0x40, 32, pad + "    ");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"{pad}[Image] ERROR: {ex.Message}");
        }
    }

    private static void DumpTextNode(StringBuilder sb, AtkTextNode* txt, string pad)
    {
        try
        {
            var text = Marshal.PtrToStringUTF8((nint)txt->NodeText.StringPtr.Value) ?? "(null)";
            sb.AppendLine($"{pad}[Text] id={txt->AtkResNode.NodeId} text=\"{text.Replace("\n", "\\n").Trim()}\"");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"{pad}[Text] ERROR: {ex.Message}");
        }
    }

    private static void DumpComponentNode(StringBuilder sb, AtkResNode* node, string pad, int indent)
    {
        try
        {
            var comp = (AtkComponentNode*)node;
            if (comp->Component == null)
            {
                sb.AppendLine($"{pad}[Component] id={node->NodeId} type={(int)node->Type} Component=NULL");
                return;
            }

            var component = comp->Component;
            var uldMgr = component->UldManager;

            sb.AppendLine($"{pad}[Component] id={node->NodeId} type={(int)node->Type} compPtr={(nint)component:X} childCount={uldMgr.NodeListCount}");

            // Enumerate component's internal node list
            for (int j = 0; j < uldMgr.NodeListCount && j < 50; j++)
            {
                try
                {
                    var cn = uldMgr.NodeList[j];
                    if (cn == null) { sb.AppendLine($"{pad}  compChild[{j}] null"); continue; }

                    bool cVis = false;
                    try { cVis = cn->IsVisible(); } catch { }

                    sb.AppendLine($"{pad}  compChild[{j}] id={cn->NodeId} type={(int)cn->Type} vis={cVis} pos=({cn->X},{cn->Y}) size=({cn->Width},{cn->Height})");
                    DumpNodeDeep(sb, cn, indent + 4);
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"{pad}  compChild[{j}] ERROR: {ex.Message}");
                }
            }

            // Safe memory read of AtkComponentBase
            sb.AppendLine($"{pad}  ComponentBase memory (0x00 to 0x200):");
            SafeHexDump(sb, (nint)component, 0x00, 0x200, 32, pad + "    ");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"{pad}[Component] ERROR: {ex.Message}");
        }
    }

    // ─── Deep pointer scan for icon texture file paths ───
    // REMOVED: FindIconPathDeep, ScanInlineForPath, ReadCStringAt
    // These caused fatal AccessViolationException crashes due to TOCTOU races —
    // memory validated by IsReadable could be freed by the game before Marshal.ReadByte runs.
    // The aggressive recursive pointer following (3 levels × 32 pointers each = ~32K dereferences)
    // made this unavoidable in a live process.

    // ─── Addon pointer array (follow 14 pointers at addon+0x288) ───

    private static void DumpAddonPointerArray(StringBuilder sb, nint addonAddr)
    {
        sb.AppendLine("--- ADDON POINTER ARRAY (addon+0x288, 14 entries) ---");
        try
        {
            for (int i = 0; i < 14; i++)
            {
                int off = 0x288 + i * 8;
                if (!IsReadable(addonAddr + off, 8)) { sb.AppendLine($"  [{i:D2}] +0x{off:X}: (not readable)"); continue; }

                nint ptr = Marshal.ReadIntPtr(addonAddr + off);
                sb.AppendLine($"  [{i:D2}] +0x{off:X}: ptr=0x{ptr:X}");

                if (ptr != 0 && IsReadable(ptr, 0x100))
                {
                    // Dump raw bytes and scan for values in icon ID range (76050-76130)
                    SafeHexDump(sb, ptr, 0x00, 0x100, 32, "       ");

                    // Scan for int32 values in icon ID range
                    for (int off2 = 0; off2 < 0x100; off2 += 4)
                    {
                        if (!IsReadable(ptr + off2, 4)) continue;
                        int val = Marshal.ReadInt32(ptr + off2);
                        if (val >= 76050 && val <= 76150)
                            sb.AppendLine($"       ** ICON ID CANDIDATE at +0x{off2:X}: {val} (0x{val:X})");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  ERROR: {ex.Message}");
        }
        sb.AppendLine();
    }

    // ─── Scan all hand tile components for icon ID values ───

    private static void DumpIconIdScan(StringBuilder sb, AtkUnitBase* addon)
    {
        sb.AppendLine("--- ICON ID SCAN (hand tiles type=1045, icon range 76050-76150) ---");
        var uld = addon->UldManager;

        for (int i = 0; i < uld.NodeListCount; i++)
        {
            try
            {
                var n = uld.NodeList[i];
                if (n == null) continue;
                if ((int)n->Type != 1045 && (int)n->Type != 1022) continue;

                bool vis = false;
                try { vis = n->IsVisible(); } catch { }
                if (!vis) continue;

                var comp = (AtkComponentNode*)n;
                if (comp->Component == null) continue;
                var component = comp->Component;
                nint compAddr = (nint)component;

                sb.AppendLine($"  NodeList[{i}] id={n->NodeId} type={(int)n->Type} compAddr=0x{compAddr:X}");

                // Scan component base data for icon ID range values
                for (int off = 0; off < 0x200; off += 4)
                {
                    if (!IsReadable(compAddr + off, 4)) continue;
                    int val = Marshal.ReadInt32(compAddr + off);
                    if (val >= 76050 && val <= 76150)
                        sb.AppendLine($"    ** ICON ID at comp+0x{off:X}: {val} (0x{val:X})");
                }

                // Also scan each child image node for icon ID range
                var uldMgr = component->UldManager;
                for (int j = 0; j < uldMgr.NodeListCount && j < 10; j++)
                {
                    var cn = uldMgr.NodeList[j];
                    if (cn == null || cn->Type != NodeType.Image) continue;

                    nint imgAddr = (nint)cn;
                    // Scan AtkImageNode from +0xA0 to end for icon ID values
                    for (int off = 0xA0; off < 0xE0; off += 4)
                    {
                        if (!IsReadable(imgAddr + off, 4)) continue;
                        int val = Marshal.ReadInt32(imgAddr + off);
                        if (val >= 76050 && val <= 76150)
                            sb.AppendLine($"    ** ICON ID at imgNode[{j}]+0x{off:X}: {val} (0x{val:X})");
                    }

                    // Also read the +0xC0 target to scan deeper
                    if (IsReadable(imgAddr + 0xC0, 8))
                    {
                        nint texPtr = Marshal.ReadIntPtr(imgAddr + 0xC0);
                        if (texPtr != 0 && IsReadable(texPtr, 0x200))
                        {
                            for (int off = 0; off < 0x200; off += 4)
                            {
                                if (!IsReadable(texPtr + off, 4)) continue;
                                int val = Marshal.ReadInt32(texPtr + off);
                                if (val >= 76050 && val <= 76150)
                                    sb.AppendLine($"    ** ICON ID at imgNode[{j}]→0xC0→+0x{off:X}: {val} (0x{val:X})");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  NodeList[{i}] ERROR: {ex.Message}");
            }
        }
        sb.AppendLine();
    }

    // ─── Memory safety via VirtualQuery ───

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQuery(nint lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, nuint dwLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    private const uint MEM_COMMIT = 0x1000;

    /// <summary>
    /// Returns true if the entire range [addr, addr+size) is committed and readable.
    /// Walks all memory regions the range spans to handle page boundaries correctly.
    /// </summary>
    private static bool IsReadable(nint addr, int size)
    {
        if (addr == 0 || addr < 0x10000 || size <= 0) return false;

        nint end = addr + size;
        nint current = addr;

        while (current < end)
        {
            if (VirtualQuery(current, out var mbi, (nuint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == 0)
                return false;
            if (mbi.State != MEM_COMMIT) return false;
            uint p = mbi.Protect & 0xFF;
            // PAGE_READONLY=0x02, PAGE_READWRITE=0x04, PAGE_WRITECOPY=0x08,
            // PAGE_EXECUTE_READ=0x20, PAGE_EXECUTE_READWRITE=0x40, PAGE_EXECUTE_WRITECOPY=0x80
            if (!(p == 0x02 || p == 0x04 || p == 0x08 || p == 0x20 || p == 0x40 || p == 0x80))
                return false;

            // Advance past this committed region
            nint regionEnd = mbi.BaseAddress + (nint)mbi.RegionSize;
            if (regionEnd <= current) return false; // safety: avoid infinite loop
            current = regionEnd;
        }

        return true;
    }

    // ─── Safe hex dump using Marshal.ReadByte ───

    private static void SafeHexDump(StringBuilder sb, nint baseAddr, int startOffset, int endOffset, int bytesPerLine, string prefix = "  ")
    {
        for (int offset = startOffset; offset < endOffset; offset += bytesPerLine)
        {
            int lineLen = Math.Min(bytesPerLine, endOffset - offset);
            if (!IsReadable(baseAddr + offset, lineLen))
            {
                sb.AppendLine($"{prefix}+{offset:X4}: (not readable)");
                continue;
            }

            sb.Append($"{prefix}+{offset:X4}: ");
            var ascii = new StringBuilder(bytesPerLine);

            for (int b = 0; b < lineLen; b++)
            {
                byte val = Marshal.ReadByte(baseAddr, offset + b);
                if (b > 0 && b % 4 == 0) sb.Append(' ');
                sb.Append($"{val:X2}");
                ascii.Append(val >= 0x20 && val < 0x7F ? (char)val : '.');
            }

            sb.AppendLine($"  |{ascii}|");
        }
    }
}
