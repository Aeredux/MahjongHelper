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
| `[1]` | String8 | `"Bamboo (9)"` | **Tile name (likely the recommended discard tile or hovered tile)** |
| `[6]` | String8 | `"Discard"` | **Current action label / game prompt** |
| `[22]` | String8 | `""` | Unknown (empty in dump, may change) |
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

### Phase A: Investigation / Instrumentation (no gameplay changes)

- [ ] **A1: Log AtkValues string fields every state update**
  - Read AtkValues indices `[1]`, `[6]`, `[22]`, `[45]` as String8
  - Log them alongside rawAtk0, agent state, and current phase to a new `suggestion_probe.log`
  - Capture values during: normal draw turn, after-call discard, chi/pon/kan prompt, skip/pass, opponent turns, between rounds
  
- [ ] **A2: Log visible suggestion text nodes (the "!" labels)**
  - In `ScanComponentForCalls`, instead of skipping `EndsWith("!")` nodes, log them with their parent component visibility, position, and alpha
  - This tells us which suggestion labels are visible at any moment
  
- [ ] **A3: Collect data during a full game**
  - Play a game with instrumentation active
  - Compile a mapping of AtkValues string values → actual game state

### Phase B: Game State from Suggestions

- [ ] **B1: Implement `ReadInGameSuggestion()` in EmjUiReader**
  - Read AtkValues string fields to determine:
    - **Discard turn**: `[6]` = "Discard" and `[1]` = tile name
    - **Call prompt**: `[6]` = "Chi"/"Pon"/"Kan"/"Ron"/"Tsumo"/"Riichi" (TBD from data)
    - **Waiting**: `[45]` = non-empty = opponent turn / nothing to do
    - **No suggestion**: empty strings = no action needed
  - Return a structured `InGameSuggestion` with type + tile name

- [ ] **B2: Use suggestion for phase detection (parallel to AtkVal[0])**
  - If in-game suggestion says "Discard" → WaitingForDiscard
  - If in-game suggestion says "Chi"/"Pon" → CallDecisionPrompt
  - If no suggestion → OpponentTurn or transition
  - Use this as a **cross-check** alongside AtkVal[0]: only act when both agree, or trust the suggestion when AtkVal[0] is ambiguous

### Phase C: Act on In-Game Suggestion

- [ ] **C1: Map tile names to tile codes**
  - Build a dictionary: `"Bamboo (9)"` → `S9`, `"Dots (3)"` → `P3`, `"Characters (7)"` → `M7`, etc.
  - Handle honor tiles: `"East"` → `EAST`, `"Red Dragon"` → `RED`, etc.

- [ ] **C2: Use in-game suggestion for discard (replaces server suggestion)**
  - When game says "Discard" + tile name → find that tile in hand → fire callback 7
  - If tile is the drawn tile → fire callback 8 (tsumogiri)
  - This completely sidesteps the server suggestion matching bugs

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

## Key Questions to Answer in Phase A

1. What does `AtkValues[6]` show during a chi prompt? A pon prompt? A normal draw?
2. What does `AtkValues[1]` show — is it always the recommended discard tile, or the last hovered tile?
3. Are AtkValues[30-37] always the same static strings, or do they change?
4. Is there an AtkValues index that shows the game's recommended discard tile icon ID?
5. During a call prompt, which `!`-suffixed text nodes are visible on the player pane?

## Priority

Start with **Phase A** (pure instrumentation) so we can collect data during a real game without breaking anything. The current auto-play mechanism continues running while we gather data.
