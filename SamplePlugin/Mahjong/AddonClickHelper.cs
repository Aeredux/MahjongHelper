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
    /// </summary>
    public static bool TryFireProbeCallback(AtkUnitBase* addon, int a, int b, bool execute)
    {
        if (addon == null) return false;

        try
        {
            if (!execute)
            {
                Log($"[DRY-RUN] Probe callback would fire: a={a}, b={b}");
                LogAtkSnapshot(addon, $"probe-dryrun-a{a}-b{b}");
                return true;
            }

            var values = stackalloc AtkValue[2];
            values[0] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = a };
            values[1] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = b };
            addon->FireCallback(2, values);

            Log($"[EXECUTE] Probe callback fired: a={a}, b={b}");
            LogAtkSnapshot(addon, $"probe-exec-a{a}-b{b}");
            return true;
        }
        catch (Exception ex)
        {
            Log($"ERROR probe callback a={a}, b={b}: {ex.Message}");
            return false;
        }
    }
}
