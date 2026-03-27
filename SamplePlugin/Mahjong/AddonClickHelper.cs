using System;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace SamplePlugin.Mahjong;

/// <summary>
/// Provides methods for simulating user clicks on EmjL (Doman Mahjong) addon nodes.
///
/// Two interaction patterns:
///   1. FireCallback — sends a callback event to the addon with a numeric command + value.
///      Used for game board actions (tile discard, call decisions).
///   2. ClickAddonButton — simulates clicking a button component.
///      Used for UI buttons (call accept/decline).
///
/// The callback IDs and value encoding are discovered empirically and logged.
/// </summary>
public static unsafe class AddonClickHelper
{
    private static readonly string LogDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MahjongHelper");

    /// <summary>
    /// Fires a callback on the EmjL addon to discard a tile at the given hand index.
    /// Currently in DISCOVERY MODE — logs the intended action but does NOT fire the callback
    /// until the correct callback ID and value encoding are verified.
    /// handIndex is the 0-based position of the tile in the player's hand (0-13).
    /// </summary>
    public static bool TryDiscardTile(AtkUnitBase* addon, int handIndex)
    {
        if (addon == null || handIndex < 0 || handIndex > 13) return false;

        // DISCOVERY MODE: log only, do not fire callback yet
        Log($"[DRY-RUN] Would discard tile at handIndex={handIndex}");
        LogAtkSnapshot(addon, $"pre-discard-{handIndex}");
        return false; // Return false to indicate no action was taken
    }

    /// <summary>
    /// Fires a callback on the EmjL addon to accept or decline a call.
    /// Currently in DISCOVERY MODE — logs the intended action but does NOT fire/click.
    /// callIndex: 0 = accept, 1 = pass/skip.
    /// </summary>
    public static bool TryRespondToCall(AtkUnitBase* addon, int callIndex)
    {
        if (addon == null || callIndex < 0) return false;

        var action = callIndex == 0 ? "accept" : "pass";
        Log($"[DRY-RUN] Would respond to call: {action} (callIndex={callIndex})");
        LogAtkSnapshot(addon, $"pre-call-{action}");
        return false;
    }

    /// <summary>
    /// Reads the visible text label from a call button component.
    /// </summary>
    private static string? FindButtonText(AtkComponentBase* comp)
    {
        if (comp == null) return null;

        try
        {
            var childUld = comp->UldManager;
            for (int i = 0; i < childUld.NodeListCount && i < 16; i++)
            {
                var cn = childUld.NodeList[i];
                if (cn == null || cn->Type != NodeType.Text)
                    continue;

                bool vis = false;
                try { vis = cn->IsVisible(); } catch { }
                if (!vis) continue;

                var txt = (AtkTextNode*)cn;
                var text = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)txt->NodeText.StringPtr.Value);
                if (!string.IsNullOrWhiteSpace(text))
                    return text.Trim();
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Logs a callback snoop entry — use this to discover what callback IDs the addon uses.
    /// Call this from OnMahjongDraw to record all AtkValues when the state changes,
    /// so we can correlate actions with callback values.
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

    /// <summary>
    /// Fires a raw callback probe to EmjL for discovery.
    /// Intended for manual controlled testing only.
    /// callbackId: the callback handler ID (similar to Saucy's 14)
    /// values: array of int values to pass
    /// </summary>
    public static bool TryFireProbeCallbackEx(AtkUnitBase* addon, int callbackId, int[] values, bool execute)
    {
        if (addon == null) return false;

        try
        {
            if (!execute)
            {
                var valStr = string.Join(",", values);
                Log($"[DRY-RUN] Probe callback would fire: callbackId={callbackId}, values=[{valStr}]");
                LogAtkSnapshot(addon, $"probe-dryrun-cb{callbackId}");
                return true;
            }

            var atkVals = stackalloc AtkValue[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                atkVals[i] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = values[i] };
            }

            addon->FireCallback((uint)values.Length, atkVals, true);

            var valStr2 = string.Join(",", values);
            Log($"[EXECUTE] Probe callback fired: callbackId={callbackId}, values=[{valStr2}]");
            LogAtkSnapshot(addon, $"probe-exec-cb{callbackId}");
            return true;
        }
        catch (Exception ex)
        {
            Log($"ERROR probe callback callbackId={callbackId}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Attempts to click a hand tile node directly using ReceiveEvent.
    /// Discovery mode to test different event types and parameters.
    /// nodeIndex is the position in the addon's NodeList.
    /// </summary>
    public static bool TryClickTileNode(AtkUnitBase* addon, int nodeIndex, bool execute)
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

            Log($"[CLICK-TEST] Target: nodeIndex={nodeIndex} nodeId={node->NodeId} type={node->Type} " +
                $"pos=({node->X},{node->Y}) size=({node->Width}x{node->Height}) visible={node->IsVisible()}");
            LogAtkSnapshot(addon, $"pre-click-node{nodeIndex}");

            if (!execute)
            {
                Log($"[DRY-RUN] Would click node {nodeIndex}");
                return true;
            }

            // Try method 1: ReceiveEvent on the node directly
            var evt = stackalloc AtkEvent[1];
            evt->Param = (uint)node->NodeId;
            evt->Target = (AtkEventTarget*)node;
            evt->Listener = null;
            evt->NextEvent = null;
            addon->ReceiveEvent(AtkEventType.MouseClick, (int)node->NodeId, evt, null);

            Log($"[EXECUTE] Clicked node {nodeIndex} via ReceiveEvent (NodeId={node->NodeId})");
            LogAtkSnapshot(addon, $"post-click-node{nodeIndex}");
            return true;
        }
        catch (Exception ex)
        {
            Log($"ERROR clicking node {nodeIndex}: {ex.Message}");
            return false;
        }
    }
}
