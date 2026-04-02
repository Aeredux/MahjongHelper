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
    // Callback 8 also works as skip/pass on call prompts
    private const int CallbackIdSkipCall = 8;

    // P/Invoke for PostMessage mouse click (works tabbed out, no cursor movement)
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hWnd, uint Msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hWnd, ref POINT lpPoint);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint hWnd, ref POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const nint MK_LBUTTON = 0x0001;

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
    /// Skips/passes on a call prompt (Pon, Chi, Kan, Ron, etc.).
    /// Uses FireCallback(2, [9, 0], true).
    /// </summary>
    public static bool TrySkipCall(AtkUnitBase* addon)
    {
        if (addon == null) return false;

        try
        {
            LogAtkSnapshot(addon, "pre-skip-call");

            var values = stackalloc AtkValue[2];
            values[0] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = CallbackIdSkipCall };
            values[1] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 0 };
            addon->FireCallback(2, values, true);

            Log($"[CALL] Fired callback 8 (skip/pass)");
            LogAtkSnapshot(addon, "post-skip-call");
            return true;
        }
        catch (Exception ex)
        {
            Log($"ERROR skipping call: {ex.Message}");
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

    // --- Call-accept via button node click ---

    /// <summary>
    /// Attempts to accept a call by clicking its button component node.
    /// Tries multiple ReceiveEvent approaches in sequence and logs which (if any) succeeds.
    /// buttonComponentPtr must be the AtkComponentButton* captured from ScanComponentForCalls.
    /// Returns true if the click was dispatched (doesn't guarantee game accepted it).
    /// </summary>
    public static bool TryClickCallButton(AtkUnitBase* addon, nint buttonComponentPtr, string callName, int method = 0)
    {
        if (addon == null || buttonComponentPtr == 0)
        {
            Log($"[CALL-CLICK] Cannot click {callName}: addon={addon != null} ptr={buttonComponentPtr:X}");
            return false;
        }

        var compNode = (AtkComponentNode*)buttonComponentPtr;
        var comp = compNode->Component;
        var node = (AtkResNode*)compNode;

        if (comp == null)
        {
            Log($"[CALL-CLICK] {callName}: component is null at ptr {buttonComponentPtr:X}");
            return false;
        }

        var nodeType = (int)node->Type;
        Log($"[CALL-CLICK] {callName}: ptr={buttonComponentPtr:X} nodeId={node->NodeId} " +
            $"type={nodeType} visible={node->IsVisible()} pos=({node->X},{node->Y}) " +
            $"size=({node->Width}x{node->Height}) method={method}");

        // Log the component's event list for diagnostics
        try
        {
            var ownerNode = comp->OwnerNode;
            Log($"[CALL-CLICK] comp->OwnerNode={(nint)ownerNode:X} " +
                $"compNode={(nint)compNode:X} match={((nint)ownerNode == (nint)compNode)}");
        }
        catch (Exception ex)
        {
            Log($"[CALL-CLICK] Error reading OwnerNode: {ex.Message}");
        }

        LogAtkSnapshot(addon, $"pre-callclick-{callName}-m{method}");

        try
        {
            bool result = false;
            switch (method)
            {
                case 1:
                    // ButtonClick (type 25) on the component — the correct event type for AtkComponentButton
                    {
                        var eventData = stackalloc byte[0x28]; // AtkEventData zeroed
                        var evt = stackalloc AtkEvent[1];
                        *evt = default;
                        evt->Param = 0;
                        evt->Target = (AtkEventTarget*)node;
                        evt->Listener = (AtkEventListener*)comp;
                        evt->NextEvent = null;
                        comp->ReceiveEvent((AtkEventType)25, 0, evt, (AtkEventData*)eventData);
                        Log($"[CALL-CLICK] Method 1: component ButtonClick(25) nodeId={node->NodeId}");
                        result = true;
                    }
                    break;

                case 2:
                    // ButtonClick (type 25) dispatched through the addon
                    {
                        var eventData = stackalloc byte[0x28];
                        var evt = stackalloc AtkEvent[1];
                        *evt = default;
                        evt->Param = (uint)node->NodeId;
                        evt->Target = (AtkEventTarget*)node;
                        evt->Listener = (AtkEventListener*)addon;
                        evt->NextEvent = null;
                        addon->ReceiveEvent((AtkEventType)25, (int)node->NodeId, evt, (AtkEventData*)eventData);
                        Log($"[CALL-CLICK] Method 2: addon ButtonClick(25) nodeId={node->NodeId}");
                        result = true;
                    }
                    break;

                case 3:
                    // MouseClick (type 9) on addon with mouse data at button center
                    {
                        var eventData = stackalloc byte[0x28];
                        // Set mouse position to center of the button
                        var mouseData = (short*)eventData;
                        mouseData[0] = (short)(node->X + node->Width / 2);  // PosX
                        mouseData[1] = (short)(node->Y + node->Height / 2); // PosY
                        var evt = stackalloc AtkEvent[1];
                        *evt = default;
                        evt->Param = (uint)node->NodeId;
                        evt->Target = (AtkEventTarget*)node;
                        evt->Listener = (AtkEventListener*)addon;
                        evt->NextEvent = null;
                        addon->ReceiveEvent(AtkEventType.MouseClick, (int)node->NodeId, evt, (AtkEventData*)eventData);
                        Log($"[CALL-CLICK] Method 3: addon MouseClick(9) at center nodeId={node->NodeId}");
                        result = true;
                    }
                    break;

                case 4:
                    // Full button press sequence: ButtonPress(23) → ButtonRelease(24) → ButtonClick(25)
                    {
                        var eventData4 = stackalloc byte[0x28];
                        var evts4 = stackalloc AtkEvent[3];
                        for (int i = 0; i < 3; i++)
                        {
                            evts4[i] = default;
                            evts4[i].Param = 0;
                            evts4[i].Target = (AtkEventTarget*)node;
                            evts4[i].Listener = (AtkEventListener*)comp;
                            evts4[i].NextEvent = null;
                        }
                        comp->ReceiveEvent((AtkEventType)23, 0, &evts4[0], (AtkEventData*)eventData4);
                        comp->ReceiveEvent((AtkEventType)24, 0, &evts4[1], (AtkEventData*)eventData4);
                        comp->ReceiveEvent((AtkEventType)25, 0, &evts4[2], (AtkEventData*)eventData4);
                        Log($"[CALL-CLICK] Method 4: component Press(23)+Release(24)+Click(25)");
                        result = true;
                    }
                    break;

                case 5:
                    // ListItemClick (type 35) — call buttons may be list items inside a list component
                    {
                        var eventData = stackalloc byte[0x28];
                        var evt = stackalloc AtkEvent[1];
                        *evt = default;
                        evt->Param = 0;
                        evt->Target = (AtkEventTarget*)node;
                        evt->Listener = (AtkEventListener*)comp;
                        evt->NextEvent = null;
                        comp->ReceiveEvent((AtkEventType)35, 0, evt, (AtkEventData*)eventData);
                        Log($"[CALL-CLICK] Method 5: component ListItemClick(35)");
                        result = true;
                    }
                    break;

                case 6:
                    // ECommons-style ClickAddonButton: read the button's registered events and replay through addon
                    {
                        var resNode = (AtkResNode*)compNode;
                        var regEvt = resNode->AtkEventManager.Event;
                        
                        // Log all registered events
                        var e = regEvt;
                        int evtIdx = 0;
                        while (e != null && evtIdx < 20)
                        {
                            Log($"[CALL-CLICK] RegisteredEvent[{evtIdx}]: type={e->State.EventType} param={e->Param} " +
                                $"target={(nint)e->Target:X} listener={(nint)e->Listener:X} node={(nint)e->Node:X}");
                            e = e->NextEvent;
                            evtIdx++;
                        }

                        if (regEvt == null)
                        {
                            Log($"[CALL-CLICK] Method 6: No events registered on button node! Trying parent nodes...");
                            // Try the parent node (list component)
                            var parentNode = node->ParentNode;
                            if (parentNode != null)
                            {
                                regEvt = parentNode->AtkEventManager.Event;
                                e = regEvt;
                                evtIdx = 0;
                                while (e != null && evtIdx < 20)
                                {
                                    Log($"[CALL-CLICK] ParentEvent[{evtIdx}]: type={e->State.EventType} param={e->Param} " +
                                        $"target={(nint)e->Target:X} listener={(nint)e->Listener:X}");
                                    e = e->NextEvent;
                                    evtIdx++;
                                }
                            }
                        }

                        if (regEvt != null)
                        {
                            Log($"[CALL-CLICK] Method 6: Replaying first registered event type={regEvt->State.EventType} param={regEvt->Param}");
                            addon->ReceiveEvent(regEvt->State.EventType, (int)regEvt->Param, regEvt);
                            Log($"[CALL-CLICK] Method 6: Done");
                        }
                        else
                        {
                            Log($"[CALL-CLICK] Method 6: No registered events found on button or parent!");
                        }
                        result = true;
                    }
                    break;

                case 7:
                    // Replay each registered event individually with AtkSnapshot between them
                    {
                        var resNode7 = (AtkResNode*)compNode;
                        var evt7 = resNode7->AtkEventManager.Event;
                        int idx7 = 0;
                        while (evt7 != null && idx7 < 20)
                        {
                            LogAtkSnapshot(addon, $"pre-regevt-{idx7}");
                            Log($"[CALL-CLICK] Method 7: Firing event[{idx7}] type={evt7->State.EventType} param={evt7->Param}");
                            addon->ReceiveEvent(evt7->State.EventType, (int)evt7->Param, evt7);
                            LogAtkSnapshot(addon, $"post-regevt-{idx7}");
                            evt7 = evt7->NextEvent;
                            idx7++;
                        }
                        if (idx7 == 0)
                            Log($"[CALL-CLICK] Method 7: No registered events on button node");
                        result = true;
                    }
                    break;

                case 8:
                    // Walk parent chain and replay registered events from each ancestor (list → container → addon root)
                    {
                        var walkNode = node;
                        int depth8 = 0;
                        while (walkNode != null && depth8 < 5)
                        {
                            var walkEvt = walkNode->AtkEventManager.Event;
                            int idx8 = 0;
                            while (walkEvt != null && idx8 < 10)
                            {
                                Log($"[CALL-CLICK] Method 8: depth={depth8} event[{idx8}] type={walkEvt->State.EventType} " +
                                    $"param={walkEvt->Param} target={(nint)walkEvt->Target:X} listener={(nint)walkEvt->Listener:X}");
                                idx8++;
                                walkEvt = walkEvt->NextEvent;
                            }
                            if (idx8 > 0)
                            {
                                // Replay the first event from this node
                                var firstEvt = walkNode->AtkEventManager.Event;
                                LogAtkSnapshot(addon, $"pre-parent{depth8}");
                                Log($"[CALL-CLICK] Method 8: Replaying from depth={depth8} type={firstEvt->State.EventType} param={firstEvt->Param}");
                                addon->ReceiveEvent(firstEvt->State.EventType, (int)firstEvt->Param, firstEvt);
                                LogAtkSnapshot(addon, $"post-parent{depth8}");
                            }
                            else
                            {
                                Log($"[CALL-CLICK] Method 8: depth={depth8} — no events registered");
                            }
                            walkNode = walkNode->ParentNode;
                            depth8++;
                        }
                        result = true;
                    }
                    break;

                case 9:
                    // SelectString-style: FireCallback(1, [0]) — select first list item (index 0)
                    // This common pattern was NEVER tested in callsweep (sweep started at value 1, not 0)
                    {
                        LogAtkSnapshot(addon, "pre-m9");
                        var v9 = stackalloc AtkValue[1];
                        v9[0] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 0 };
                        addon->FireCallback(1, v9, true);
                        LogAtkSnapshot(addon, "post-m9");
                        Log($"[CALL-CLICK] Method 9: FireCallback(1, [0]) — SelectString pattern index=0");
                        result = true;
                    }
                    break;

                case 10:
                    // SelectString-style: FireCallback(1, [1]) — select second list item (index 1)
                    {
                        LogAtkSnapshot(addon, "pre-m10");
                        var v10 = stackalloc AtkValue[1];
                        v10[0] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 1 };
                        addon->FireCallback(1, v10, true);
                        LogAtkSnapshot(addon, "post-m10");
                        Log($"[CALL-CLICK] Method 10: FireCallback(1, [1]) — SelectString pattern index=1");
                        result = true;
                    }
                    break;

                case 11:
                    // SelectString-style with button's event param minus 1 (in case it's 1-indexed)
                    {
                        var resNode11 = (AtkResNode*)compNode;
                        uint btnParam11 = 0;
                        var be11 = resNode11->AtkEventManager.Event;
                        if (be11 != null) btnParam11 = be11->Param;
                        int idx11 = (int)btnParam11 > 0 ? (int)btnParam11 - 1 : (int)btnParam11;

                        LogAtkSnapshot(addon, "pre-m11");
                        var v11 = stackalloc AtkValue[1];
                        v11[0] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = idx11 };
                        addon->FireCallback(1, v11, true);
                        LogAtkSnapshot(addon, "post-m11");
                        Log($"[CALL-CLICK] Method 11: FireCallback(1, [{idx11}]) — adjusted from button param={btnParam11}");
                        result = true;
                    }
                    break;

                case 12:
                    // PostMessage mouse click at button position (works tabbed out, no cursor movement)
                    {
                        // Walk parent chain to compute game-client position
                        float clientX = node->X;
                        float clientY = node->Y;
                        var p12 = node->ParentNode;
                        int d12 = 0;
                        while (p12 != null)
                        {
                            Log($"[CALL-CLICK] Method 12: parent[{d12}] id={p12->NodeId} offset=({p12->X},{p12->Y})");
                            clientX += p12->X;
                            clientY += p12->Y;
                            p12 = p12->ParentNode;
                            d12++;
                        }

                        int centerX = (int)(clientX + node->Width / 2.0f);
                        int centerY = (int)(clientY + node->Height / 2.0f);

                        Log($"[CALL-CLICK] Method 12: client pos=({clientX},{clientY}) center=({centerX},{centerY}) " +
                            $"addon=({addon->X},{addon->Y}) scale={addon->Scale}");

                        var hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                        // lParam = MAKELPARAM(x, y) = (y << 16) | (x & 0xFFFF)
                        nint lParam = (centerY << 16) | (centerX & 0xFFFF);

                        Log($"[CALL-CLICK] Method 12: PostMessage click at client ({centerX},{centerY}) hwnd={hwnd:X}");

                        PostMessage(hwnd, WM_LBUTTONDOWN, MK_LBUTTON, lParam);
                        System.Threading.Thread.Sleep(30);
                        PostMessage(hwnd, WM_LBUTTONUP, 0, lParam);

                        LogAtkSnapshot(addon, "post-m12");
                        Log($"[CALL-CLICK] Method 12: PostMessage click sent");
                        result = true;
                    }
                    break;

                case 13:
                    // FireCallback(2, [9, itemIndex]) — callback 9 with item index
                    {
                        var resNode13 = (AtkResNode*)compNode;
                        uint btnParam13 = 0;
                        var be13 = resNode13->AtkEventManager.Event;
                        if (be13 != null) btnParam13 = be13->Param;

                        LogAtkSnapshot(addon, "pre-m13");
                        var v13 = stackalloc AtkValue[2];
                        v13[0] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 9 };
                        v13[1] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = (int)btnParam13 };
                        addon->FireCallback(2, v13, true);
                        LogAtkSnapshot(addon, "post-m13");
                        Log($"[CALL-CLICK] Method 13: FireCallback(2, [9, {btnParam13}])");
                        result = true;
                    }
                    break;

                case 14:
                    // FireCallback(2, [9, 0]) — accept first call option
                    {
                        LogAtkSnapshot(addon, "pre-m14");
                        var v14 = stackalloc AtkValue[2];
                        v14[0] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 9 };
                        v14[1] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 0 };
                        addon->FireCallback(2, v14, true);
                        LogAtkSnapshot(addon, "post-m14");
                        Log($"[CALL-CLICK] Method 14: FireCallback(2, [9, 0])");
                        result = true;
                    }
                    break;

                case 15:
                    // FireCallback(2, [8, 1]) — callback 8 was EXCLUDED from sweeps!
                    // [8, 0] = skip. Maybe [8, 1] = accept first call option?
                    {
                        LogAtkSnapshot(addon, "pre-m15");
                        var v15 = stackalloc AtkValue[2];
                        v15[0] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 8 };
                        v15[1] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 1 };
                        addon->FireCallback(2, v15, true);
                        LogAtkSnapshot(addon, "post-m15");
                        Log($"[CALL-CLICK] Method 15: FireCallback(2, [8, 1]) — callback 8 value 1");
                        result = true;
                    }
                    break;

                case 16:
                    // FireCallback(2, [8, 2]) — accept second call option?
                    {
                        LogAtkSnapshot(addon, "pre-m16");
                        var v16 = stackalloc AtkValue[2];
                        v16[0] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 8 };
                        v16[1] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 2 };
                        addon->FireCallback(2, v16, true);
                        LogAtkSnapshot(addon, "post-m16");
                        Log($"[CALL-CLICK] Method 16: FireCallback(2, [8, 2]) — callback 8 value 2");
                        result = true;
                    }
                    break;

                case 17:
                    // FireCallback(2, [7, 0]) during call prompt — callback 7 also excluded from sweeps
                    // [7, N] normally discards, but semantics may differ during call prompt
                    {
                        LogAtkSnapshot(addon, "pre-m17");
                        var v17 = stackalloc AtkValue[2];
                        v17[0] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 7 };
                        v17[1] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 0 };
                        addon->FireCallback(2, v17, true);
                        LogAtkSnapshot(addon, "post-m17");
                        Log($"[CALL-CLICK] Method 17: FireCallback(2, [7, 0]) — callback 7 during call prompt");
                        result = true;
                    }
                    break;

                default:
                    // Method 0: deep diagnostic — dump button info, parent chain, event registrations
                    Log($"[CALL-CLICK] Method 0: DIAGNOSTIC for {callName}");
                    Log($"[CALL-CLICK]   node: id={node->NodeId} type={nodeType} flags={(uint)node->NodeFlags:X} " +
                        $"drawFlags={(uint)node->DrawFlags:X}");
                    
                    // Walk parent chain
                    try
                    {
                        var parent = node->ParentNode;
                        int parentDepth = 0;
                        while (parent != null && parentDepth < 10)
                        {
                            var pType = (int)parent->Type;
                            var pVis = false;
                            try { pVis = parent->IsVisible(); } catch { }
                            Log($"[CALL-CLICK]   parent[{parentDepth}]: id={parent->NodeId} type={pType} " +
                                $"visible={pVis} pos=({parent->X},{parent->Y}) size=({parent->Width}x{parent->Height})");
                            parent = parent->ParentNode;
                            parentDepth++;
                        }
                    }
                    catch (Exception pex) { Log($"[CALL-CLICK]   parent walk error: {pex.Message}"); }

                    // Dump registered events on button node AND its parents
                    try
                    {
                        Log($"[CALL-CLICK]   --- Registered Events ---");
                        var diagNode = node;
                        int diagDepth = 0;
                        while (diagNode != null && diagDepth < 5)
                        {
                            var diagEvt = diagNode->AtkEventManager.Event;
                            int diagIdx = 0;
                            while (diagEvt != null && diagIdx < 15)
                            {
                                Log($"[CALL-CLICK]   events[depth={diagDepth}][{diagIdx}]: type={diagEvt->State.EventType} " +
                                    $"param={diagEvt->Param} target={(nint)diagEvt->Target:X} listener={(nint)diagEvt->Listener:X} " +
                                    $"flags={diagEvt->State.StateFlags}");
                                diagEvt = diagEvt->NextEvent;
                                diagIdx++;
                            }
                            if (diagIdx == 0)
                                Log($"[CALL-CLICK]   events[depth={diagDepth}]: (none)");
                            diagNode = diagNode->ParentNode;
                            diagDepth++;
                        }
                    }
                    catch (Exception eex) { Log($"[CALL-CLICK]   event dump error: {eex.Message}"); }

                    // Dump ALL AtkValues (not just first 20)
                    try
                    {
                        var totalVals = (int)addon->AtkValuesCount;
                        Log($"[CALL-CLICK]   AtkValues total count: {totalVals}");
                        var sb = new System.Text.StringBuilder();
                        for (int i = 0; i < totalVals && i < 200; i++)
                        {
                            try
                            {
                                var v = addon->AtkValues[i];
                                var typeStr = v.Type.ToString();
                                if (v.Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int)
                                    sb.Append($"[{i}]i={v.Int} ");
                                else if (v.Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt)
                                    sb.Append($"[{i}]u={v.UInt} ");
                                else if (v.Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String)
                                {
                                    string s;
                                    try { s = Marshal.PtrToStringUTF8((nint)v.String.Value) ?? "null"; }
                                    catch { s = "err"; }
                                    sb.Append($"[{i}]s=\"{s}\" ");
                                }
                                else
                                    sb.Append($"[{i}]{typeStr}={v.Int} ");

                                // Log in chunks to avoid line length issues
                                if (sb.Length > 300)
                                {
                                    Log($"[CALL-CLICK]   AtkVals: {sb}");
                                    sb.Clear();
                                }
                            }
                            catch { sb.Append($"[{i}]=? "); }
                        }
                        if (sb.Length > 0)
                            Log($"[CALL-CLICK]   AtkVals: {sb}");
                    }
                    catch (Exception vex) { Log($"[CALL-CLICK]   AtkValues dump error: {vex.Message}"); }

                    // Dump button's child nodes
                    try
                    {
                        var btnUld = comp->UldManager;
                        Log($"[CALL-CLICK]   button child nodes: {btnUld.NodeListCount}");
                        for (int ci = 0; ci < btnUld.NodeListCount && ci < 20; ci++)
                        {
                            var cn = btnUld.NodeList[ci];
                            if (cn == null) continue;
                            var cnVis = false;
                            try { cnVis = cn->IsVisible(); } catch { }
                            var cnText = "";
                            if (cn->Type == NodeType.Text)
                            {
                                try
                                {
                                    var tn = (AtkTextNode*)cn;
                                    cnText = $" text=\"{Marshal.PtrToStringUTF8((nint)tn->NodeText.StringPtr.Value)}\"";
                                }
                                catch { }
                            }
                            Log($"[CALL-CLICK]   child[{ci}]: id={cn->NodeId} type={(int)cn->Type} " +
                                $"visible={cnVis} pos=({cn->X},{cn->Y}) size=({cn->Width}x{cn->Height}){cnText}");
                        }
                    }
                    catch (Exception cex) { Log($"[CALL-CLICK]   child dump error: {cex.Message}"); }

                    Log($"[CALL-CLICK]   Use '/mj clickcall {callName} <1-17> run' to test click methods.");
                    result = false;
                    break;
            }

            LogAtkSnapshot(addon, $"post-callclick-{callName}-m{method}");
            return result;
        }
        catch (Exception ex)
        {
            Log($"[CALL-CLICK] ERROR clicking {callName} method={method}: {ex.Message}\n{ex.StackTrace}");
            return false;
        }
    }

    // --- Call-accept discovery helpers ---

    // Tracks which (callbackId, secondVal) pair to try next during callsweep
    private static int _sweepIndex = 0;

    // Candidate callback patterns to try during a call prompt.
    // Phase 1 (exhausted): IDs 0-6, 9, 11-15 with values 0-2
    // Phase 2: higher IDs, 3-value patterns, known working IDs with unusual second values
    private static readonly int[][] SweepCandidates = new[]
    {
        // Higher callback IDs not yet tested
        new[] { 17, 0 },
        new[] { 18, 0 },
        new[] { 21, 0 },
        new[] { 22, 0 },
        new[] { 23, 0 },
        new[] { 24, 0 },
        new[] { 25, 0 },
        new[] { 26, 0 },
        new[] { 27, 0 },
        new[] { 28, 0 },
        new[] { 29, 0 },
        new[] { 30, 0 },
        // Single-value patterns (some addons use 1-arg callbacks)
        new[] { 1 },
        new[] { 2 },
        new[] { 3 },
        new[] { 4 },
        new[] { 5 },
        new[] { 6 },
        new[] { 9 },
        new[] { 10 },
        new[] { 11 },
        new[] { 12 },
        // 3-value patterns: [callbackId, callType, detail]
        // callType might encode chi=0, pon=1, kan=2, ron=3 or similar
        new[] { 7, 0, 0 },
        new[] { 7, 0, 1 },
        new[] { 7, 1, 0 },
        new[] { 7, 1, 1 },
        new[] { 7, 2, 0 },
        new[] { 7, 3, 0 },
        new[] { 8, 1, 0 },
        new[] { 8, 1, 1 },
        new[] { 8, 2, 0 },
        new[] { 8, 3, 0 },
        new[] { 9, 0, 0 },
        new[] { 9, 1, 0 },
        new[] { 9, 2, 0 },
        new[] { 9, 3, 0 },
        new[] { 10, 1, 0 },
        new[] { 10, 2, 0 },
        // Try ID 7 with larger second values (hand position might correlate to call selection)
        new[] { 7, 13 },
        new[] { 7, 14 },
        new[] { 7, 15 },
        new[] { 7, 16 },
        new[] { 7, 17 },
        new[] { 7, 18 },
        new[] { 7, 19 },
        new[] { 7, 20 },
    };

    /// <summary>
    /// Dry-run: log all candidates and current sweep index.
    /// </summary>
    public static void LogCallSweepDryRun(AtkUnitBase* addon)
    {
        Log($"[CALLSWEEP] DRY-RUN — {SweepCandidates.Length} candidates, next index={_sweepIndex}");
        for (int i = 0; i < SweepCandidates.Length; i++)
        {
            var c = SweepCandidates[i];
            var marker = i == _sweepIndex ? " <-- NEXT" : "";
            Log($"[CALLSWEEP]   [{i}] values=[{string.Join(",", c)}]{marker}");
        }
    }

    /// <summary>
    /// Execute the next untried callback probe in the sweep sequence.
    /// Logs AtkValues before and after to detect game response.
    /// Advances the sweep index so the next call tries the next candidate.
    /// </summary>
    public static void ExecuteNextCallSweepProbe(AtkUnitBase* addon)
    {
        if (_sweepIndex >= SweepCandidates.Length)
        {
            Log($"[CALLSWEEP] All {SweepCandidates.Length} candidates exhausted. Use '/mj callsweep' to see results. Reset with next game.");
            _sweepIndex = 0;
            return;
        }

        var candidate = SweepCandidates[_sweepIndex];
        Log($"[CALLSWEEP] Executing candidate [{_sweepIndex}]: values=[{string.Join(",", candidate)}]");

        // Snapshot AtkValues BEFORE
        LogAtkSnapshot(addon, $"callsweep-pre-{_sweepIndex}");

        // Fire the callback
        var atkVals = stackalloc AtkValue[candidate.Length];
        for (int i = 0; i < candidate.Length; i++)
            atkVals[i] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = candidate[i] };

        addon->FireCallback((uint)candidate.Length, atkVals, true);

        // Snapshot AtkValues AFTER
        LogAtkSnapshot(addon, $"callsweep-post-{_sweepIndex}");

        Log($"[CALLSWEEP] Candidate [{_sweepIndex}] fired. Check game for visible effect.");
        _sweepIndex++;
    }

    /// <summary>
    /// Reset sweep index (for starting a new sweep session).
    /// </summary>
    public static void ResetCallSweep() => _sweepIndex = 0;
}
