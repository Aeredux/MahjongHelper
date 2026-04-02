# In-Game Suggestion Reading Plan

## Problem

Our current auto-play approach has reliability issues:
- **AtkVal[0] state machine** is unreliable for phase detection (stale call buttons persist, real call prompts appear at unexpected AtkVal values)
- **Callback 8** has dual meaning (tsumogiri at AtkVal[0]=30, skip/pass at AtkVal[0]=6) causing accidental drawn-tile discards
- **Server suggestion → hand slot matching** introduces complex tile-code matching and position mapping bugs

## Proposed Approach

Read the **game's own in-game suggestion system** from EmjL AtkValues strings. The game already knows the correct phase and recommends actions — we just need to read and act on them.

### Key AtkValues Identified

From `tile_dump_latest.txt` (50 AtkValues on EmjL addon):

| Index | Type | Example Value | Meaning |
|-------|------|---------------|---------|
| `[0]` | Int | `30` | Raw game phase (unreliable for our purposes) |
| `[1]` | String8 | `"Bamboo (9)"` | **Hover tooltip** — shows tile name under mouse cursor. Only populated at atk0=30 (draw turn). Changes rapidly with mouse movement. NOT a recommendation. |
| `[6]` | String8 | `"Discard"` | **Game action recommendation** — "Discard" during draw turn, "Pass" when game recommends passing a call, "Chi!" when game recommends accepting chi, "30 Fu N Han" at scoring |
| `[22]` | String8 | `""` | Always empty in all observed states |
| `[23]` | String8 | `"Calls Off"` | Call enable/disable label |
| `[24]` | String8 | `"Calls On"` | Call enable/disable label |
| `[30]` | String8 | `"Pon!"` | Static suggestion label for Pon prompt |
| `[31]` | String8 | `"Chi!"` | Static suggestion label for Chi prompt |
| `[32]` | String8 | `"Kan!"` | Static suggestion label for Kan prompt |
| `[33]` | String8 | `"Riichi!"` | Static suggestion label for Riichi prompt |
| `[34]` | String8 | `"Tsumo!"` | Static suggestion label for Tsumo prompt |
| `[35]` | String8 | `"Ron!"` | Static suggestion label for Ron prompt |
| `[36]` | String8 | `"Tenpai!"` | Tenpai notice label |
| `[37]` | String8 | `"Noten..."` | No-ten notice label |
| `[45]` | String8 | `"Please wait..."` | Waiting-for-opponents message |

### Text Nodes on Player Panes

The game also displays suggestion bubbles on the player pane (e.g., "Pon!", "Chi!") as text nodes inside component containers. These are the `trimmed.EndsWith("!")` nodes currently filtered in `ScanComponentForCalls`.

## Implementation Plan

### Phase A: Investigation / Instrumentation (no gameplay changes) ✅ COMPLETE

- [x] **A1: Log AtkValues string fields every state update**
  - `LogSuggestionProbe()` reads String8 AtkValues at [1],[6],[22],[23],[24],[45],[30-37]
  - Logs to `suggestion_probe.log` with dedup by signature
  
- [x] **A2: Log visible suggestion text nodes (the "!" labels)**
  - `LogSuggestionNode()` + `FlushSuggestionNodes()` captures "!" text nodes with visibility/owner info
  - Logged alongside probe data in `suggestion_probe.log`
  
- [x] **A3: Collect data during a full game**
  - 109 log entries captured across a full game
  - See findings below

### Phase B: Game State from Suggestions

- [x] **B1: Implement `ReadInGameSuggestion()` in EmjUiReader**
  - Read `AtkValues[6]` as the primary signal:
    - `"Discard"` → player's discard turn
    - `"Pass"` → game recommends passing on a call
    - `"Chi!"` / `"Pon!"` / `"Kan!"` / `"Ron!"` / `"Tsumo!"` → game recommends accepting
    - `"N Fu N Han"` → scoring/round end
  - Also read SuggestionNodes as secondary signal for call type detection
  - Return a structured `InGameSuggestion` with type enum

- [x] **B2: Use `[6]` for phase detection (cross-check with AtkVal[0])**
  - `[6]="Discard"` at atk0=30/2 → WaitingForDiscard (high confidence)
  - `[6]="Pass"` or `[6]="Chi!"` at atk0=15 → call prompt incoming (act immediately or wait for atk0=6)
  - `[6]` contains "Fu" and "Han" → BetweenRounds
  - Trust `[6]` over stale call buttons for phase decisions

### Phase C: Act on In-Game Suggestion

- [ ] **C1: Map tile names to tile codes**
  - Build a dictionary: `"Bamboo (9)"` → `S9`, `"Dots (3)"` → `P3`, `"Characters (7)"` → `M7`, etc.
  - Handle honor tiles: `"East"` → `EAST`, `"Red Dragon"` → `RED`, etc.

- [ ] **C2: Use in-game suggestion for discard**
  - `[1]` is a hover tooltip, NOT a recommendation — cannot use it for tile selection
  - For now, continue using server suggestion for which tile to discard
  - Future: investigate if there's a highlighted tile node or other visual indicator

- [ ] **C3: Use in-game suggestion for call decisions**
  - When game suggests "Chi!"/"Pon!" → the game recommends accepting
  - When game doesn't suggest anything during a call prompt → game recommends passing
  - Can use this as the auto-play signal instead of server eval

### Phase D: Hybrid Mode (Optional)

- [ ] **D1: Compare server suggestion vs in-game suggestion**
  - Log when they disagree for analysis
  - Let user configure which to trust (server vs game AI)
  
- [ ] **D2: Server provides tile selection, game provides phase detection**
  - Use in-game suggestion only for detecting "we need to act now"
  - Use server suggestion for choosing which tile to discard
  - This combines best of both: reliable phase detection + stronger AI

## Phase A Findings

### AtkValues[6] — Game Action Recommendation (THE key signal)

| `[6]` value | Meaning | When observed |
|---|---|---|
| `"Discard"` | Player's turn — discard a tile | atk0=30 (draw turn), atk0=6 (after draw animation), atk0=2 (after-call discard). Also persists stale into atk0=15/22 transitions. |
| `"Pass"` | Game recommends **passing** on a call | atk0=15 during a call prompt |
| `"Chi!"` | Game recommends **accepting chi** | atk0=15 during chi prompt (appeared once at 09:22:28) |
| `"30 Fu N Han"` | Scoring display (round result) | atk0=29, 32, 43 at round end |

### AtkValues[1] — Hover Tooltip (NOT a recommendation)

- Only populated when atk0=30 (player's draw turn)
- Changes rapidly with mouse movement: "Bamboo (9)" → "Bamboo (8)" → "Dots (4)" within 1 second
- Shows tile names: "West Wind", "White Dragon", "South Wind", "Characters (1)", etc.
- **Cannot be used as the recommended discard tile** — it's just a hover tooltip
- Open question: how to determine which tile to discard during draw turns

### AtkValues[22], [23], [24], [45] — Unchanged

- `[22]` always empty
- `[23]` = "Calls Off", `[24]` = "Calls On" — static labels, never changed
- `[45]` never appeared in the log (always empty or unchanged from initial state)

### AtkValues[30-37] — Static Labels

- Never changed during the entire game — they are fixed UI label strings

### SuggestionNodes — Reliable Call Indicators

| Nodes visible | Meaning | Timing |
|---|---|---|
| `"Chi!"` (ownerType=1031) | Chi available | Appears ~1s before atk0=6 call prompt |
| `"Pon!"` + `"Chi!"` | Both Pon and Chi available | Appeared together |
| `"Tsumo!"` | Self-draw win available | Appeared once near end of game |
| `"Ron!"` | Ron win available | Appeared at start of log (round scoring) |
| `"Chi!"` (ownerType=1027) | Chi on a different component | Appeared once alongside 1031 nodes |
| `(none)` | No suggestions | Most of the game |

### Key Insight: `[6]` Transition Timeline During a Call Prompt

```
atk0=22 [6]="Discard"     → draw animation
atk0=15 [6]="Discard"     → opponent discards (stale [6])
atk0=15 [6]="Pass"        → game updates [6] to recommend Pass
atk0=6  [6]="Discard"     → call prompt appears, [6] resets
```

The `[6]="Pass"` or `[6]="Chi!"` appears at atk0=15 BEFORE the actual call prompt (atk0=6). This means we can detect the game's recommendation before the prompt even shows.

### Remaining Questions

1. **How to find recommended discard tile?** `[1]` is just hover tooltip. Need to find another AtkValue, or use the game's highlighted tile visual, or fall back to server suggestion for tile selection.
2. **Is `[6]="Chi!"` reliable?** Only observed once. Need more chi prompt data.
3. **What does `[6]` show during Pon/Kan/Riichi prompts?** Not observed in this game.

## Priority

**Phase B next**: Implement `ReadInGameSuggestion()` using `[6]` for call/pass decisions. For discard tile selection, continue using server suggestion (Phase D hybrid approach) since `[1]` is just a tooltip.
