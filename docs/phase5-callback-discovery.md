# Phase 5: EmjL Callback Discovery Plan

## Current Status
**Auto-play infrastructure is complete but tile clicking is in dry-run mode.**

### ✅ Completed
- AutoPlayManager scheduling logic
- Configuration toggles (auto-play, auto-discard, auto-call, delay)
- `/mj auto` and `/mj pause` commands
- Overlay status display
- Action detection via `/mj mark discard` and `/mj mark call`

### ❌ Blocked
- Real tile discard automation (5.1)
- Real call accept/decline automation (5.2)

**Blocker:** Cannot determine the correct EmjL addon interaction mechanism.

---

## Discovery Attempts (All Failed)

### 1. Direct Node Click via `ReceiveEvent`
```csharp
var evt = stackalloc AtkEvent[1];
evt->Param = (uint)node->NodeId;
evt->Target = (AtkEventTarget*)node;
addon->ReceiveEvent(AtkEventType.MouseClick, (int)node->NodeId, evt, null);
```
**Result:** AtkValues unchanged, no tile discard occurred.

### 2. FireCallback with NodeId
```csharp
var values = stackalloc AtkValue[2];
values[0] = new AtkValue { Type = ValueType.Int, Int = 1 };
values[1] = new AtkValue { Type = ValueType.Int, Int = nodeId };
addon->FireCallback(2, values);
```
**Result:** No response from game.

### 3. FireCallback with Multiple Values
Tried various combinations including:
- `FireCallback(0, nodeIndex)`
- `FireCallback(1, 60)` (random probe)
- Multi-value arrays

**Result:** No response from game.

---

## Breakthrough: Saucy's Hook-Based Approach

### Source
[Saucy CuffACur Module](https://github.com/PunishXIV/Saucy/blob/master/Saucy/CuffACur/CufModule.cs)

### Key Pattern
```csharp
public delegate nint UnknownFunction(nint a1, ushort a2, int a3, void* a4);
public static Hook<UnknownFunction> FuncHook;

// Hook the event handler function via signature scan
FuncHook ??= Svc.Hook.HookFromAddress<UnknownFunction>(
    Svc.SigScanner.ScanText("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 30 0F B7 FA"), 
    FuncDetour);

// Call with event type 0x17 (23 decimal)
var evt = stackalloc AtkEvent[] { new() {
    Node = btn,
    Target = (AtkEventTarget*)btn,
    Param = 0,
    NextEvent = null
}};

FuncHook.Original((nint)addon, 0x17, 0, evt);
```

### Why This Works
- Directly calls the **internal event handler function** the game uses
- Bypasses `FireCallback` and `ReceiveEvent` wrappers
- Uses the **same code path** as manual player clicks

---

## Next Steps for EmjL

### 1. Find EmjL's Event Handler Signature
- Scan for similar pattern in FFXIV memory
- Signature might be different from Cuff-A-Cur (different addon type)
- Look for function that takes `(nint addon, ushort eventType, int param, void* eventData)`

### 2. Determine Correct Event Type
- Cuff-A-Cur uses `0x17` (MouseClick equivalent)
- EmjL might use different event type for tile interactions
- Test values: `0x03`, `0x09`, `0x17`, `0x19`, `0x1E`

### 3. Implement Hook in AddonClickHelper
```csharp
public delegate nint EmjEventHandler(nint addon, ushort eventType, int param, void* eventData);
private static Hook<EmjEventHandler>? _eventHook;

public static void Initialize(IGameInteropProvider interop, ISigScanner scanner)
{
    var address = scanner.ScanText("<SIGNATURE_TO_DISCOVER>");
    _eventHook = interop.HookFromAddress<EmjEventHandler>(address, EventDetour);
    _eventHook.Enable();
}

public static bool TryDiscardTileViaHook(AtkUnitBase* addon, int nodeIndex)
{
    var node = addon->UldManager.NodeList[nodeIndex];
    var evt = stackalloc AtkEvent[] { new() {
        Target = (AtkEventTarget*)node,
        Param = (uint)node->NodeId,
        NextEvent = null
    }};

    _eventHook.Original((nint)addon, 0x??, 0, evt);
    return true;
}
```

### 4. Discovery Commands
Add signature probing commands:
- `/mj scansig <pattern>` — test custom signatures
- `/mj testevent <nodeIndex> <eventType>` — test event types

---

## Alternative Approaches (If Hook Fails)

### Memory-Based Automation
- Directly write to AgentEmj memory structure
- Bypass UI interaction entirely
- **Risk:** Higher detection chance, game state desync

### Wait for Community
- Monitor for other EmjL automation plugins
- Check Dalamud Discord for signatures
- Request help from Saucy/PunishXIV developers

---

## References
- [Saucy CuffACur Module](https://github.com/PunishXIV/Saucy/blob/master/Saucy/CuffACur/CufModule.cs)
- [FFXIVClientStructs AtkEvent](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Component/GUI/AtkEvent.cs)
- [Dalamud Hooking Guide](https://dalamud.dev/api/Dalamud.Hooking.html)

---

## Timeline
- **2026-03-27:** Discovered Saucy's hook-based pattern
- **Next:** Implement signature scanning and event hook for EmjL
