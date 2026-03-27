using System;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace SamplePlugin.Mahjong;

/// <summary>
/// Provides methods for simulating user clicks on EmjL (Doman Mahjong) addon nodes.
///
/// Interaction approach (from Saucy reference):
///   The game dispatches UI events through AtkEventListener.ReceiveEvent on component nodes.
///   For minigame addons, the component node's own event listener handles tile interactions.
///   We call ReceiveEvent on the component with the correct event type and target node.
/// </summary>
public static unsafe class AddonClickHelper
{
    private static readonly string LogDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MahjongHelper");

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
                    // Method 4: FireCallback with slot index (0-based hand position)
                    {
                        // Hand tiles are nodes 71(slot0) down to 54(slot17), nodeId pattern: 134, 1340001-1340016
                        // Try passing the hand slot index as callback value
                        int slotIndex = nodeIndex <= 71 ? (71 - nodeIndex) : nodeIndex;
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

                default:
                    // Method 0: try all methods sequentially with logging
                    for (int m = 1; m <= 5; m++)
                    {
                        Log($"--- Trying method {m} ---");
                        TryClickTileNode(addon, nodeIndex, true, m);
                    }
                    result = true;
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
