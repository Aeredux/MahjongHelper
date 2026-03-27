using System;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace SamplePlugin.Mahjong;

/// <summary>
/// Provides methods for simulating user actions on EmjL (Doman Mahjong) addon.
///
/// Discovered callback IDs (via FireCallback):
///   7  = Discard specific tile (second value = 0-based hand position from left)
///   8  = Discard drawn tile (tsumogiri)
///   10 = Declare draw (ryuukyoku)
///   16 = Withdraw from match
///   19 = Close mahjong game
/// </summary>
public static unsafe class AddonClickHelper
{
    private static readonly string LogDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MahjongHelper");

    private const int CallbackIdDiscardTile = 7;
    private const int CallbackIdDiscardDrawn = 8;

    /// <summary>
    /// Discards a tile at the given hand position (0 = leftmost in sorted hand).
    /// Uses FireCallback(2, [7, handPos], true).
    /// </summary>
    public static bool TryDiscardTile(AtkUnitBase* addon, int handPos)
    {
        if (addon == null || handPos < 0 || handPos > 13) return false;

        try
        {
            LogAtkSnapshot(addon, $"pre-discard-pos{handPos}");

            var values = stackalloc AtkValue[2];
            values[0] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = CallbackIdDiscardTile };
            values[1] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = handPos };
            addon->FireCallback(2, values, true);

            Log($"[DISCARD] Fired callback 7 with handPos={handPos}");
            LogAtkSnapshot(addon, $"post-discard-pos{handPos}");
            return true;
        }
        catch (Exception ex)
        {
            Log($"ERROR discarding tile at handPos={handPos}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Discards the drawn tile (tsumogiri).
    /// Uses FireCallback(2, [8, 0], true).
    /// </summary>
    public static bool TryDiscardDrawnTile(AtkUnitBase* addon)
    {
        if (addon == null) return false;

        try
        {
            LogAtkSnapshot(addon, "pre-tsumogiri");

            var values = stackalloc AtkValue[2];
            values[0] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = CallbackIdDiscardDrawn };
            values[1] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 0 };
            addon->FireCallback(2, values, true);

            Log($"[DISCARD] Fired callback 8 (tsumogiri)");
            LogAtkSnapshot(addon, "post-tsumogiri");
            return true;
        }
        catch (Exception ex)
        {
            Log($"ERROR discarding drawn tile: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Attempts to click a hand tile node by dispatching an event through the component's listener.
    /// Tries multiple approaches: component ReceiveEvent, addon ReceiveEvent, and callback.
    /// nodeIndex is the position in the addon's NodeList.
    /// method: 1=component listener, 2=addon ReceiveEvent, 3=component button event, 4=FireCallback with index
    /// </summary>
    public static bool TryClickTileNode(AtkUnitBase* addon, int nodeIndex, bool execute, int method = 0)
    {
        if (addon == null || nodeIndex < 0) return false;

        try
        {
            var uld = addon->UldManager;
            if (nodeIndex >= uld.NodeListCount)
            {
                Log($"[CLICK-TEST] nodeIndex={nodeIndex} out of range (max={uld.NodeListCount})");
                return false;
            }

            var node = uld.NodeList[nodeIndex];
            if (node == null)
            {
                Log($"[CLICK-TEST] node at index {nodeIndex} is null");
                return false;
            }

            var isComponent = (int)node->Type >= 1000;
            AtkComponentNode* compNode = isComponent ? (AtkComponentNode*)node : null;
            AtkComponentBase* comp = compNode != null ? compNode->Component : null;

            Log($"[CLICK-TEST] Target: nodeIndex={nodeIndex} nodeId={node->NodeId} type={(int)node->Type} " +
                $"pos=({node->X},{node->Y}) size=({node->Width}x{node->Height}) visible={node->IsVisible()} " +
                $"isComp={isComponent} hasComp={comp != null} method={method}");
            LogAtkSnapshot(addon, $"pre-click-node{nodeIndex}-m{method}");

            // Calculate hand slot index (nodes 71=slot0, 70=slot1, ... 54=slot17)
            int slotIndex = nodeIndex <= 71 ? (71 - nodeIndex) : nodeIndex;

            if (!execute)
            {
                Log($"[DRY-RUN] Would click node {nodeIndex} method={method}");
                return true;
            }

            bool result = false;
            switch (method)
            {
                case 1:
                    // Method 1: ReceiveEvent on the component's own event listener
                    if (comp != null)
                    {
                        var evt1 = stackalloc AtkEvent[1];
                        evt1->Param = (uint)node->NodeId;
                        evt1->Target = (AtkEventTarget*)node;
                        evt1->Listener = (AtkEventListener*)comp;
                        evt1->NextEvent = null;
                        comp->ReceiveEvent(AtkEventType.MouseClick, (int)node->NodeId, evt1);
                        Log($"[EXECUTE] Method 1: component ReceiveEvent nodeId={node->NodeId}");
                        result = true;
                    }
                    else
                    {
                        Log($"[SKIP] Method 1: node is not a component");
                    }
                    break;

                case 2:
                    // Method 2: ReceiveEvent on addon with component as target
                    {
                        var evt2 = stackalloc AtkEvent[1];
                        evt2->Param = (uint)node->NodeId;
                        evt2->Target = (AtkEventTarget*)node;
                        evt2->Listener = (AtkEventListener*)addon;
                        evt2->NextEvent = null;
                        addon->ReceiveEvent(AtkEventType.MouseClick, (int)node->NodeId, evt2, null);
                        Log($"[EXECUTE] Method 2: addon ReceiveEvent nodeId={node->NodeId}");
                        result = true;
                    }
                    break;

                case 3:
                    // Method 3: ReceiveEvent with event type 0x17 (from Saucy CuffACur)
                    if (comp != null)
                    {
                        var evt3 = stackalloc AtkEvent[1];
                        evt3->Param = 0;
                        evt3->Target = (AtkEventTarget*)node;
                        evt3->Listener = (AtkEventListener*)comp;
                        evt3->NextEvent = null;
                        comp->ReceiveEvent((AtkEventType)0x17, 0, evt3);
                        Log($"[EXECUTE] Method 3: component event type 0x17 nodeId={node->NodeId}");
                        result = true;
                    }
                    else
                    {
                        Log($"[SKIP] Method 3: node is not a component");
                    }
                    break;

                case 4:
                    // FireCallback with [2, slotIndex, 0]
                    {
                        var values = stackalloc AtkValue[3];
                        values[0] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 2 };
                        values[1] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = slotIndex };
                        values[2] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 0 };
                        addon->FireCallback(3, values, true);
                        Log($"[EXECUTE] Method 4: FireCallback(3, [2, {slotIndex}, 0]) nodeIndex={nodeIndex}");
                        result = true;
                    }
                    break;

                case 5:
                    // Method 5: ReceiveEvent on addon with event type 0x09 (ButtonClick)
                    {
                        var evt5 = stackalloc AtkEvent[1];
                        evt5->Param = (uint)node->NodeId;
                        evt5->Target = (AtkEventTarget*)node;
                        evt5->Listener = (AtkEventListener*)addon;
                        evt5->NextEvent = null;
                        addon->ReceiveEvent((AtkEventType)0x09, (int)node->NodeId, evt5, null);
                        Log($"[EXECUTE] Method 5: addon event type 0x09 nodeId={node->NodeId}");
                        result = true;
                    }
                    break;

                case 6:
                    // FireCallback(1, [slotIndex])
                    {
                        var v = stackalloc AtkValue[1];
                        v[0] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = slotIndex };
                        addon->FireCallback(1, v, true);
                        Log($"[EXECUTE] Method 6: FireCallback(1, [{slotIndex}])");
                        result = true;
                    }
                    break;

                case 7:
                    // FireCallback(2, [0, slotIndex])
                    {
                        var v = stackalloc AtkValue[2];
                        v[0] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 0 };
                        v[1] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = slotIndex };
                        addon->FireCallback(2, v, true);
                        Log($"[EXECUTE] Method 7: FireCallback(2, [0, {slotIndex}])");
                        result = true;
                    }
                    break;

                case 8:
                    // FireCallback(2, [1, slotIndex])
                    {
                        var v = stackalloc AtkValue[2];
                        v[0] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 1 };
                        v[1] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = slotIndex };
                        addon->FireCallback(2, v, true);
                        Log($"[EXECUTE] Method 8: FireCallback(2, [1, {slotIndex}])");
                        result = true;
                    }
                    break;

                case 9:
                    // FireCallback(2, [11, slotIndex]) — Saucy TT pattern
                    {
                        var v = stackalloc AtkValue[2];
                        v[0] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 11 };
                        v[1] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = slotIndex };
                        addon->FireCallback(2, v, true);
                        Log($"[EXECUTE] Method 9: FireCallback(2, [11, {slotIndex}])");
                        result = true;
                    }
                    break;

                case 10:
                    // FireCallback(2, [14, slotIndex]) — another TT pattern
                    {
                        var v = stackalloc AtkValue[2];
                        v[0] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 14 };
                        v[1] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = slotIndex };
                        addon->FireCallback(2, v, true);
                        Log($"[EXECUTE] Method 10: FireCallback(2, [14, {slotIndex}])");
                        result = true;
                    }
                    break;

                default:
                    // Method 0: just log info, use specific method numbers for testing
                    Log($"[INFO] Use /mj clicktile <node> <method> run — methods 6-10 for callbacks, or /mj firecb <id> <slot> run");
                    result = false;
                    break;
            }

            LogAtkSnapshot(addon, $"post-click-node{nodeIndex}-m{method}");
            return result;
        }
        catch (Exception ex)
        {
            Log($"ERROR clicking node {nodeIndex} method={method}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Fires a callback with arbitrary int values for discovery.
    /// </summary>
    public static bool TryFireProbeCallbackEx(AtkUnitBase* addon, int callbackId, int[] values, bool execute)
    {
        if (addon == null) return false;

        try
        {
            var valStr = string.Join(",", values);
            if (!execute)
            {
                Log($"[DRY-RUN] Probe callback: values=[{valStr}]");
                LogAtkSnapshot(addon, "probe-dryrun");
                return true;
            }

            var atkVals = stackalloc AtkValue[values.Length];
            for (int i = 0; i < values.Length; i++)
                atkVals[i] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = values[i] };

            addon->FireCallback((uint)values.Length, atkVals, true);

            Log($"[EXECUTE] Probe callback: values=[{valStr}]");
            LogAtkSnapshot(addon, "probe-exec");
            return true;
        }
        catch (Exception ex)
        {
            Log($"ERROR probe callback: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Logs AtkValues snapshot for correlation.
    /// </summary>
    public static void LogAtkSnapshot(AtkUnitBase* addon, string context)
    {
        if (addon == null) return;

        try
        {
            var count = Math.Min((int)addon->AtkValuesCount, 20);
            var vals = new string[count];
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var v = addon->AtkValues[i];
                    vals[i] = $"[{i}]={v.Int}";
                }
                catch { vals[i] = $"[{i}]=?"; }
            }

            Log($"AtkSnapshot({context}): {string.Join(" ", vals)}");
        }
        catch { }
    }

    private static void Log(string message)
    {
        try
        {
            System.IO.Directory.CreateDirectory(LogDir);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(LogDir, "autoplay.log"),
                $"[{DateTime.UtcNow:O}] {message}\n");
        }
        catch { }
    }
}
