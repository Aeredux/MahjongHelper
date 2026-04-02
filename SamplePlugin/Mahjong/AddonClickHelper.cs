using System;
using System.Collections.Generic;
using System.Linq;
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
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    // SendInput structures
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type; // 0 = mouse
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public nint dwExtraInfo;
    }

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_MOVE = 0x0001;

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
    /// Advances past the score screen (atk0=29/32).
    /// Strategy: Scan for visible text button nodes containing "OK"/"Next"/"Continue",
    /// and click via ListItemClick. Falls back to trying various callbacks.
    /// Logs all attempts for diagnostics.
    /// </summary>
    public static bool TryAdvanceScoreScreen(AtkUnitBase* addon)
    {
        if (addon == null) return false;

        try
        {
            int rawAtk0 = -1;
            try
            {
                if (addon->AtkValues != null && addon->AtkValuesCount > 0)
                    rawAtk0 = addon->AtkValues[0].Int;
            }
            catch { }

            Log($"[ADVANCE] Attempting score screen advance (rawAtk0={rawAtk0})");
            LogAtkSnapshot(addon, "pre-advance");

            // Strategy 1: Scan all visible component nodes for clickable text buttons
            var uld = addon->UldManager;
            var foundButtons = new System.Collections.Generic.List<(string text, nint node, nint parent)>();

            for (int i = 0; i < uld.NodeListCount; i++)
            {
                try
                {
                    var n = uld.NodeList[i];
                    if (n == null || (int)n->Type < 1000) continue;
                    bool vis = false;
                    try { vis = n->IsVisible(); } catch { }
                    // Scan both visible and type=1032 (container) nodes
                    if (!vis && (int)n->Type != 1032) continue;

                    var comp = (AtkComponentNode*)n;
                    if (comp->Component == null) continue;

                    // Scan child nodes for text
                    var childUld = comp->Component->UldManager;
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
                            string text;
                            try { text = Marshal.PtrToStringUTF8((nint)txt->NodeText.StringPtr.Value) ?? ""; }
                            catch { continue; }

                            if (string.IsNullOrWhiteSpace(text)) continue;
                            var trimmed = text.Trim();

                            // Skip known call button text and suggestion labels
                            if (trimmed.EndsWith("!")) continue;
                            var lower = trimmed.ToLowerInvariant();
                            if (lower == "chi" || lower == "pon" || lower == "kan" ||
                                lower == "ron" || lower == "tsumo" || lower == "riichi" ||
                                lower == "skip" || lower == "pass" || lower == "cancel") continue;

                            // Log all text nodes found on the score screen for diagnostics
                            foundButtons.Add((trimmed, (nint)comp, (nint)n));
                        }
                        catch { }
                    }
                }
                catch { }
            }

            // Log just the recognized buttons
            if (foundButtons.Count > 0)
            {
                var btnNames = string.Join(", ", foundButtons.Select(b => $"\"{b.text}\""));
                Log($"[ADVANCE] Found {foundButtons.Count} text nodes: {btnNames}");
            }

            // Look for "OK", "Next", "Continue" or similar advance buttons
            foreach (var (text, node, parent) in foundButtons)
            {
                var lower = text.ToLowerInvariant();
                if (lower == "ok" || lower == "next" || lower.Contains("continue") ||
                    lower.Contains("proceed") || lower.Contains("confirm"))
                {
                    Log($"[ADVANCE] Clicking advance button: \"{text}\" node={node:X}");
                    var result = TryClickButton(addon, node, text);
                    LogAtkSnapshot(addon, "post-advance-button");

                    // Check if it worked
                    int postAtk0btn = -1;
                    try
                    {
                        if (addon->AtkValues != null && addon->AtkValuesCount > 0)
                            postAtk0btn = addon->AtkValues[0].Int;
                    }
                    catch { }

                    if (postAtk0btn != rawAtk0)
                    {
                        Log($"[ADVANCE] Button click worked: atk0 {rawAtk0} -> {postAtk0btn}");
                        return true;
                    }
                    Log($"[ADVANCE] Button click didn't change state (may need frame delay)");
                    break; // PostMessage is async, state change may come next frame
                }
            }

            // No recognized button found
            Log($"[ADVANCE] No advance button found on score screen");

            int postAtk0 = -1;
            try
            {
                if (addon->AtkValues != null && addon->AtkValuesCount > 0)
                    postAtk0 = addon->AtkValues[0].Int;
            }
            catch { }

            Log($"[ADVANCE] Result: atk0 {rawAtk0} -> {postAtk0}");
            return true;
        }
        catch (Exception ex)
        {
            Log($"ERROR advancing score screen: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Handles the chi/pon choice sub-menu (atk0=25) where multiple options exist
    /// for the same call type. Scans for the choice container (node id=52 type=1053
    /// or id=53 type=1054), finds visible option nodes (type=1014/1015), reads their
    /// tile icons, and clicks the best match.
    /// </summary>
    /// <param name="addon">The EmjL addon.</param>
    /// <param name="preferredTiles">
    /// Tile codes from the pre-chi suggestion to match against (e.g. ["M4","M5","M6"]).
    /// If null or empty, clicks the first visible option.
    /// </param>
    /// <param name="iconMap">Icon map for resolving icon IDs to tile codes.</param>
    /// <param name="iconCapture">Icon capture for reading dynamic icon IDs.</param>
    public static bool TrySelectCallChoice(AtkUnitBase* addon,
        IReadOnlyList<string>? preferredTiles, MahjongIconMap? iconMap, IconIdCapture? iconCapture)
    {
        if (addon == null) return false;

        try
        {
            int rawAtk0 = -1;
            try
            {
                if (addon->AtkValues != null && addon->AtkValuesCount > 0)
                    rawAtk0 = addon->AtkValues[0].Int;
            }
            catch { }

            Log($"[CHI-CHOICE] Attempting call choice selection (rawAtk0={rawAtk0})");
            LogAtkSnapshot(addon, "pre-chi-choice");

            // Find the choice container: id=52 (type=1053, chi) or id=53 (type=1054, pon)
            var uld = addon->UldManager;
            AtkComponentNode* choiceContainer = null;
            int choiceNodeIndex = -1;

            for (int i = 0; i < uld.NodeListCount; i++)
            {
                var n = uld.NodeList[i];
                if (n == null) continue;
                if ((n->NodeId == 52 || n->NodeId == 53) && (int)n->Type >= 1000)
                {
                    bool vis = false;
                    try { vis = n->IsVisible(); } catch { }
                    if (vis)
                    {
                        choiceContainer = (AtkComponentNode*)n;
                        choiceNodeIndex = i;
                        Log($"[CHI-CHOICE] Found choice container: nodeId={n->NodeId} type={(int)n->Type} index={i}");
                        break;
                    }
                }
            }

            if (choiceContainer == null || choiceContainer->Component == null)
            {
                Log($"[CHI-CHOICE] No visible choice container found");
                return false;
            }

            // Scan child nodes for visible option groups (type=1014 or 1015, with tile children)
            var comp = choiceContainer->Component;
            var childUld = comp->UldManager;
            var options = new System.Collections.Generic.List<(nint node, int childIdx, System.Collections.Generic.List<string> tiles)>();

            for (int j = 0; j < childUld.NodeListCount && j < 32; j++)
            {
                var cn = childUld.NodeList[j];
                if (cn == null || (int)cn->Type < 1000) continue;

                bool vis = false;
                try { vis = cn->IsVisible(); } catch { }
                if (!vis) continue;

                var childType = (int)cn->Type;
                // Option nodes are type 1014 or 1015 (tile groups)
                if (childType != 1014 && childType != 1015) continue;

                var optionComp = (AtkComponentNode*)cn;
                if (optionComp->Component == null) continue;

                // Read tile icons from the option's children (type=1009, size 34x45)
                var tileIcons = new System.Collections.Generic.List<string>();
                var optChildUld = optionComp->Component->UldManager;
                for (int k = 0; k < optChildUld.NodeListCount && k < 16; k++)
                {
                    var tn = optChildUld.NodeList[k];
                    if (tn == null || (int)tn->Type < 1000) continue;

                    uint iconId = 0;
                    if (EmjUiReader.TryFindIconPublic(tn, iconCapture, out iconId) && iconId > 0)
                    {
                        var tileName = iconMap?.Resolve(iconId);
                        if (!string.IsNullOrEmpty(tileName))
                            tileIcons.Add(tileName);
                    }
                }

                if (tileIcons.Count > 0)
                {
                    options.Add(((nint)optionComp, j, tileIcons));
                    Log($"[CHI-CHOICE] Option {options.Count - 1} (childIdx={j} id={cn->NodeId}): [{string.Join(",", tileIcons)}]");
                }
            }

            if (options.Count == 0)
            {
                Log($"[CHI-CHOICE] No visible options found in container");
                return false;
            }

            // Pick the best option: match against preferred tiles if available
            int bestIdx = 0;
            if (preferredTiles != null && preferredTiles.Count > 0)
            {
                int bestScore = -1;
                for (int i = 0; i < options.Count; i++)
                {
                    var optTiles = options[i].tiles;
                    int score = 0;
                    foreach (var pt in preferredTiles)
                    {
                        // Normalize red dora for matching: M0↔M5, P0↔P5, S0↔S5
                        if (optTiles.Any(t => TileMatchesForChi(t, pt)))
                            score++;
                    }
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestIdx = i;
                    }
                }
                Log($"[CHI-CHOICE] Best match: option {bestIdx} (score={bestScore}) preferred=[{string.Join(",", preferredTiles)}]");
            }
            else
            {
                Log($"[CHI-CHOICE] No preferred tiles, using first option");
            }

            // Click the selected option via ButtonClick dispatch
            var selectedNode = (AtkResNode*)(AtkComponentNode*)options[bestIdx].node;
            Log($"[CHI-CHOICE] Clicking option {bestIdx} id={selectedNode->NodeId}");

            var result = TryClickButton(addon, (nint)selectedNode, $"chi-option-{bestIdx}");
            LogAtkSnapshot(addon, "post-chi-choice");

            int postAtk0 = -1;
            try
            {
                if (addon->AtkValues != null && addon->AtkValuesCount > 0)
                    postAtk0 = addon->AtkValues[0].Int;
            }
            catch { }

            Log($"[CHI-CHOICE] Result: atk0 {rawAtk0} -> {postAtk0}");
            return result;
        }
        catch (Exception ex)
        {
            Log($"ERROR selecting call choice: {ex.Message}");
            return false;
        }
    }

    /// <summary>Matches tile codes for chi option comparison, normalizing red dora.</summary>
    private static bool TileMatchesForChi(string optionTile, string preferredTile)
    {
        if (optionTile.Equals(preferredTile, StringComparison.OrdinalIgnoreCase))
            return true;
        // M0↔M5, P0↔P5, S0↔S5
        var normOpt = optionTile switch { "M0" => "M5", "P0" => "P5", "S0" => "S5", _ => optionTile };
        var normPref = preferredTile switch { "M0" => "M5", "P0" => "P5", "S0" => "S5", _ => preferredTile };
        return normOpt.Equals(normPref, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Attempts to click a standalone button node (type >= 1000) using PostMessage
    /// to simulate a mouse click at the button's screen position.
    /// This is the most reliable approach for UI elements that don't respond to internal events.
    /// </summary>
    private static bool TryClickButton(AtkUnitBase* addon, nint buttonPtr, string label)
    {
        if (addon == null || buttonPtr == 0) return false;

        try
        {
            var compNode = (AtkComponentNode*)buttonPtr;
            var node = (AtkResNode*)compNode;

            Log($"[CLICK-BTN] {label}: nodeId={node->NodeId} type={(int)node->Type} visible={node->IsVisible()}");

            // Walk parent chain to compute game-client position
            float clientX = node->X;
            float clientY = node->Y;
            var parent = node->ParentNode;
            int depth = 0;
            while (parent != null && depth < 20)
            {
                clientX += parent->X;
                clientY += parent->Y;
                parent = parent->ParentNode;
                depth++;
            }

            // Parent walk gives client-area coordinates; no addon offset needed
            int centerX = (int)(clientX + node->Width / 2.0f);
            int centerY = (int)(clientY + node->Height / 2.0f);

            Log($"[CLICK-BTN] {label}: computed center=({centerX},{centerY}) addon=({addon->X},{addon->Y}) rawPos=({clientX},{clientY})");

            // Approach 1: Find the registered ButtonClick event on the node and dispatch
            // through the addon with the ORIGINAL event structure (preserving listener pointer)
            try
            {
                var evt = node->AtkEventManager.Event;
                int evtIdx = 0;
                while (evt != null && evtIdx < 32)
                {
                    evtIdx++;
                    if (evt->State.EventType == AtkEventType.ButtonClick && evt->Listener != null)
                    {
                        Log($"[CLICK-BTN] {label}: dispatching ButtonClick param={evt->Param} via addon using original event");
                        var eventData = stackalloc byte[0x28];
                        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt, (AtkEventData*)eventData);
                        Log($"[CLICK-BTN] {label}: addon ReceiveEvent done");
                        return true;
                    }
                    evt = evt->NextEvent;
                }
                Log($"[CLICK-BTN] {label}: no ButtonClick found in {evtIdx} events");
            }
            catch (Exception ex)
            {
                Log($"[CLICK-BTN] {label}: Approach 1 failed: {ex.Message}");
            }

            // Approach 2: SendInput simulation
            var hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            if (hwnd == 0)
            {
                Log($"[CLICK-BTN] {label}: no HWND found");
                return false;
            }

            var screenPt = new POINT { X = centerX, Y = centerY };
            ClientToScreen(hwnd, ref screenPt);
            Log($"[CLICK-BTN] {label}: screen coords=({screenPt.X},{screenPt.Y})");

            GetCursorPos(out var savedPos);
            SetCursorPos(screenPt.X, screenPt.Y);
            System.Threading.Thread.Sleep(30);

            var inputs = new INPUT[2];
            inputs[0].type = 0;
            inputs[0].mi.dwFlags = MOUSEEVENTF_LEFTDOWN;
            inputs[1].type = 0;
            inputs[1].mi.dwFlags = MOUSEEVENTF_LEFTUP;

            var sent = SendInput(2, inputs, Marshal.SizeOf<INPUT>());
            Log($"[CLICK-BTN] {label}: SendInput sent={sent}");

            System.Threading.Thread.Sleep(50);
            SetCursorPos(savedPos.X, savedPos.Y);

            Log($"[CLICK-BTN] {label}: cursor restored");
            return true;
        }
        catch (Exception ex)
        {
            Log($"[CLICK-BTN] ERROR clicking {label}: {ex.Message}");
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

    // --- Call-accept via list item click (WORKING APPROACH) ---

    /// <summary>
    /// Accepts a call by firing ListItemClick on the parent list's registered listener.
    /// This is the confirmed working approach: navigates from the button node to its
    /// parent list component, finds the ListItemClick event's registered listener,
    /// determines the button's index among visible siblings, and calls
    /// listener->ReceiveEvent(ListItemClick, index).
    ///
    /// buttonComponentPtr: the AtkComponentNode* of the call button (from ScanComponentForCalls)
    /// Returns true if the event was dispatched successfully.
    /// </summary>
    public static bool TryAcceptCallViaListClick(AtkUnitBase* addon, nint buttonComponentPtr, string callName)
    {
        if (addon == null || buttonComponentPtr == 0)
        {
            Log($"[CALL-ACCEPT] Cannot accept {callName}: addon={addon != null} ptr={buttonComponentPtr:X}");
            return false;
        }

        var compNode = (AtkComponentNode*)buttonComponentPtr;
        var node = (AtkResNode*)compNode;

        Log($"[CALL-ACCEPT] {callName}: ptr={buttonComponentPtr:X} nodeId={node->NodeId} " +
            $"type={(int)node->Type} visible={node->IsVisible()}");

        // Navigate to parent list node
        var parentNode = node->ParentNode;
        if (parentNode == null)
        {
            Log($"[CALL-ACCEPT] {callName}: no parent node");
            return false;
        }

        Log($"[CALL-ACCEPT] {callName}: parent id={parentNode->NodeId} type={(int)parentNode->Type}");

        // Find ListItemClick event on parent list
        var listEvt = parentNode->AtkEventManager.Event;
        AtkEvent* listItemClickEvt = null;
        while (listEvt != null)
        {
            if (listEvt->State.EventType == AtkEventType.ListItemClick)
            {
                listItemClickEvt = listEvt;
                break;
            }
            listEvt = listEvt->NextEvent;
        }

        if (listItemClickEvt == null || listItemClickEvt->Listener == null)
        {
            Log($"[CALL-ACCEPT] {callName}: no ListItemClick event or listener on parent");
            return false;
        }

        // Determine button's index among visible siblings in the list
        int buttonIndex = FindButtonIndexInList(parentNode, compNode);
        Log($"[CALL-ACCEPT] {callName}: buttonIndex={buttonIndex} listener={(nint)listItemClickEvt->Listener:X}");

        // Construct safe event with valid Node field
        var safeEvt = stackalloc AtkEvent[1];
        *safeEvt = *listItemClickEvt;
        safeEvt->Node = node;
        safeEvt->Param = (uint)buttonIndex;
        safeEvt->NextEvent = null;
        var eventData = stackalloc byte[0x28];

        LogAtkSnapshot(addon, $"pre-accept-{callName}");
        listItemClickEvt->Listener->ReceiveEvent(
            AtkEventType.ListItemClick, buttonIndex, safeEvt, (AtkEventData*)eventData);
        LogAtkSnapshot(addon, $"post-accept-{callName}");
        Log($"[CALL-ACCEPT] {callName}: dispatched ListItemClick index={buttonIndex}");
        return true;
    }

    /// <summary>
    /// Finds the button's index among visible component siblings in the parent list.
    /// Iterates the parent's child nodes (via UldManager if component, or ChildNode chain)
    /// and counts visible component nodes to determine position.
    /// </summary>
    private static int FindButtonIndexInList(AtkResNode* parentNode, AtkComponentNode* targetButton)
    {
        // If parent is a component (type >= 1000), iterate its UldManager children
        if ((int)parentNode->Type >= 1000)
        {
            var parentCompNode = (AtkComponentNode*)parentNode;
            var parentComp = parentCompNode->Component;
            if (parentComp != null)
            {
                var uld = parentComp->UldManager;
                // Collect visible component children (the list items)
                var visibleItems = new System.Collections.Generic.List<nint>();
                for (int i = 0; i < uld.NodeListCount && i < 64; i++)
                {
                    var cn = uld.NodeList[i];
                    if (cn == null) continue;
                    if ((int)cn->Type < 1000) continue; // only components
                    bool vis = false;
                    try { vis = cn->IsVisible(); } catch { }
                    if (!vis) continue;
                    visibleItems.Add((nint)cn);
                }

                // Find target in the visible items list
                for (int i = 0; i < visibleItems.Count; i++)
                {
                    if (visibleItems[i] == (nint)targetButton)
                    {
                        Log($"[CALL-ACCEPT] Found button at list index {i} of {visibleItems.Count} visible items");
                        return i;
                    }
                }

                Log($"[CALL-ACCEPT] Button not found in {visibleItems.Count} visible items, defaulting to 0");
            }
        }

        // Fallback: use 0
        return 0;
    }

    // --- Call-accept via button node click (experimental methods) ---

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
                    // KEY INSIGHT: All previous ReceiveEvent calls went through addon->ReceiveEvent().
                    // But the ListItemClick event's registered LISTENER is a DIFFERENT object than the addon.
                    // In ATK dispatch, events go to listener->ReceiveEvent(), not addon->ReceiveEvent().
                    // This method calls the LISTENER's ReceiveEvent directly.
                    {
                        var resNode9 = (AtkResNode*)compNode;
                        // Get button's list item index from its registered events
                        uint buttonParam = 0;
                        var btnEvt9 = resNode9->AtkEventManager.Event;
                        if (btnEvt9 != null)
                            buttonParam = btnEvt9->Param;
                        Log($"[CALL-CLICK] Method 9: button item param={buttonParam}");

                        var parentNode9 = resNode9->ParentNode;
                        if (parentNode9 != null)
                        {
                            Log($"[CALL-CLICK] Method 9: parent id={parentNode9->NodeId} type={(int)parentNode9->Type}");
                            // Find ListItemClick event on the parent list node
                            var listEvt = parentNode9->AtkEventManager.Event;
                            AtkEvent* listItemClickEvt = null;
                            int idx9 = 0;
                            while (listEvt != null && idx9 < 20)
                            {
                                Log($"[CALL-CLICK] Method 9: parent event[{idx9}] type={listEvt->State.EventType} " +
                                    $"param={listEvt->Param} target={(nint)listEvt->Target:X} " +
                                    $"listener={(nint)listEvt->Listener:X} node={(nint)listEvt->Node:X}");
                                if (listEvt->State.EventType == AtkEventType.ListItemClick)
                                {
                                    listItemClickEvt = listEvt;
                                    break;
                                }
                                listEvt = listEvt->NextEvent;
                                idx9++;
                            }

                            if (listItemClickEvt != null && listItemClickEvt->Listener != null)
                            {
                                // Construct a safe event copy with valid Node
                                var safeEvt = stackalloc AtkEvent[1];
                                *safeEvt = *listItemClickEvt;
                                safeEvt->Node = resNode9;
                                safeEvt->Param = buttonParam;
                                safeEvt->NextEvent = null;

                                var eventData = stackalloc byte[0x28];

                                LogAtkSnapshot(addon, "pre-m9");
                                Log($"[CALL-CLICK] Method 9: Calling LISTENER->ReceiveEvent(ListItemClick, {buttonParam}) " +
                                    $"listener={(nint)listItemClickEvt->Listener:X} (NOT addon!)");
                                listItemClickEvt->Listener->ReceiveEvent(
                                    AtkEventType.ListItemClick, (int)buttonParam, safeEvt, (AtkEventData*)eventData);
                                LogAtkSnapshot(addon, "post-m9");
                                Log($"[CALL-CLICK] Method 9: Done");
                            }
                            else
                            {
                                Log($"[CALL-CLICK] Method 9: No ListItemClick event or listener is null");
                            }
                        }
                        else
                        {
                            Log($"[CALL-CLICK] Method 9: No parent node found");
                        }
                        result = true;
                    }
                    break;

                case 10:
                    // Same as M9 but with param=0 (alternate index)
                    {
                        var resNode10 = (AtkResNode*)compNode;
                        var parentNode10 = resNode10->ParentNode;
                        if (parentNode10 != null)
                        {
                            var listEvt10 = parentNode10->AtkEventManager.Event;
                            AtkEvent* lic10 = null;
                            while (listEvt10 != null)
                            {
                                if (listEvt10->State.EventType == AtkEventType.ListItemClick)
                                { lic10 = listEvt10; break; }
                                listEvt10 = listEvt10->NextEvent;
                            }
                            if (lic10 != null && lic10->Listener != null)
                            {
                                var safeEvt10 = stackalloc AtkEvent[1];
                                *safeEvt10 = *lic10;
                                safeEvt10->Node = (AtkResNode*)compNode;
                                safeEvt10->Param = 0;
                                safeEvt10->NextEvent = null;
                                var ed10 = stackalloc byte[0x28];

                                LogAtkSnapshot(addon, "pre-m10");
                                Log($"[CALL-CLICK] Method 10: LISTENER->ReceiveEvent(ListItemClick, 0)");
                                lic10->Listener->ReceiveEvent(
                                    AtkEventType.ListItemClick, 0, safeEvt10, (AtkEventData*)ed10);
                                LogAtkSnapshot(addon, "post-m10");
                                Log($"[CALL-CLICK] Method 10: Done");
                            }
                            else
                            {
                                Log($"[CALL-CLICK] Method 10: No ListItemClick/listener on parent");
                            }
                        }
                        result = true;
                    }
                    break;

                case 11:
                    // Call ButtonClick's own LISTENER->ReceiveEvent (the component handler, not addon)
                    {
                        var resNode11 = (AtkResNode*)compNode;
                        var evt11 = resNode11->AtkEventManager.Event;
                        AtkEvent* buttonClickEvt = null;
                        while (evt11 != null)
                        {
                            if (evt11->State.EventType == AtkEventType.ButtonClick)
                            { buttonClickEvt = evt11; break; }
                            evt11 = evt11->NextEvent;
                        }
                        if (buttonClickEvt != null && buttonClickEvt->Listener != null)
                        {
                            var ed11 = stackalloc byte[0x28];
                            LogAtkSnapshot(addon, "pre-m11");
                            Log($"[CALL-CLICK] Method 11: LISTENER->ReceiveEvent(ButtonClick, {buttonClickEvt->Param}) " +
                                $"listener={(nint)buttonClickEvt->Listener:X}");
                            buttonClickEvt->Listener->ReceiveEvent(
                                AtkEventType.ButtonClick, (int)buttonClickEvt->Param, buttonClickEvt, (AtkEventData*)ed11);
                            LogAtkSnapshot(addon, "post-m11");
                            Log($"[CALL-CLICK] Method 11: Done");
                        }
                        else
                        {
                            Log($"[CALL-CLICK] Method 11: No ButtonClick/listener on button");
                        }
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

                    Log($"[CALL-CLICK]   Use '/mj clickcall {callName} <1-17> run' to test. M9/10 = listener->ReceiveEvent (KEY TEST).");
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
