# Saucy Repo Research: How It Gets FFXIV Minigame State Data

Source reviewed: https://github.com/PunishXIV/Saucy

This document summarizes the technical patterns Saucy uses to read game state for different Gold Saucer minigames. Focus is on state acquisition (where the data comes from), not solver quality or automation behavior.

## High-Level Pattern

Saucy uses a mixed strategy:

1. UI addon polling (`IGameGui.GetAddonByName`) with visibility checks.
2. Unsafe struct overlays (`StructLayout` + `FieldOffset`) for known addon memory layouts.
3. ATK node traversal for text/visibility extraction where raw fields are insufficient.
4. Agent interface access (`FindAgentInterface`) for state not present directly on addon structs.
5. Signature scanning (`SigScanner.ScanText` / `GetStaticAddressFromSig`) and hooks when no stable public path exists.
6. Event-driven addon lifecycle listeners for some minigames (instead of pure polling).

## Cross-Cutting Infrastructure

### 1) Polling scheduler for addon readers

- `UIReaderScheduler` keeps a list of readers implementing `IUIReader` and periodically:
  - discovers addon pointers,
  - tracks addon creation/destruction,
  - calls `OnAddonShown`, `OnAddonUpdate`, and `OnAddonLost`.
- It validates addon visibility via root node checks before treating an addon as active.

Primary files:
- `Saucy/TripleTriad/TriadBuddy/utils/UIReaderScheduler.cs`

### 2) ATK node utility traversal

- Uses helper functions to walk child/sibling node trees and extract text, texture paths, size, position, and visibility.
- This is used where direct struct offsets are unstable or incomplete.

Primary files:
- `Saucy/TripleTriad/TriadBuddy/utils/GUINodeUtils.cs`

### 3) Generic ATK value reader abstraction

- `AtkReader` wraps indexed reads from `AtkUnitBase->AtkValues` with type checking (`ReadInt`, `ReadUInt`, `ReadBool`, `ReadString`).
- This abstraction is reused in Out on a Limb and Mini Cactpot readers.

Primary files:
- `Saucy/OutOnALimb/ECEmbedded/AtkReader.cs`

## Minigame-by-Minigame State Acquisition

## Triple Triad

### A) In-match board/decks/rules/turn state

Method:
- Reads `TripleTriad` addon directly with unsafe struct overlays.
- `UIReaderTriadGame` defines an explicit `AddonTripleTriad` layout and per-card sub-struct (`AddonTripleTriadCard`).
- Pulls:
  - turn state,
  - both decks,
  - board cards,
  - card owner/rarity/type/sides,
  - rule and NPC descriptors via node traversal.

Key files:
- `Saucy/TripleTriad/TriadBuddy/plugin/UIReaderTriadGame.cs`
- `Saucy/TripleTriad/TriadBuddy/plugin/UIStateTriadGame.cs`

### B) Pre-game match request and deck selection

Method:
- Separate readers for request UI (`TripleTriadRequest`) and deck-select UI (`TripleTriadSelDeck`).
- Parses rule/NPC text and deck card texture paths from ATK tree nodes.

Key files:
- `Saucy/TripleTriad/TriadBuddy/plugin/UIReaderTriadPrep.cs`

### C) Card list / deck edit state

Method:
- Reads `GSInfoCardList` and `GSInfoEditDeck` addon state.
- Uses addon fields + agent pointers (`FindAgentInterface`) and fallback agent lookup through `UIModule->AgentModule`.

Key files:
- `Saucy/TripleTriad/TriadBuddy/plugin/UIReaderTriadCardList.cs`
- `Saucy/TripleTriad/TriadBuddy/plugin/UIReaderTriadDeckEdit.cs`

### D) Match results (MGP + win/lose/draw + card reward)

Method:
- Reads `TripleTriadResult` addon node tree for outcome + MGP.
- Reads reward item id from an agent overlay (`AgentTripleTriad` struct with `rewardItemId` field).

Key files:
- `Saucy/TripleTriad/TriadBuddy/plugin/UIReaderTriadResults.cs`

### E) Ownership/progression data from UIState + signatures

Method:
- `UnsafeReaderTriadCards` uses signature scanning to:
  - locate an internal function (`IsNpcBeaten`),
  - resolve `UIState` static pointer.
- Reads card-owned bits and NPC-completion status from client memory.

Key files:
- `Saucy/TripleTriad/TriadBuddy/plugin/UnsafeReaderTriadCards.cs`

### F) Additional deck manipulation hooks

Method:
- `UnsafeReaderTriadDeck` scans signatures for internal UI functions and invokes delegates for selected card / refresh behavior.

Key files:
- `Saucy/TripleTriad/TriadBuddy/plugin/UnsafeReaderTriadDeck.cs`

## Mini Cactpot

### A) Event-driven addon updates

Method:
- Registers addon lifecycle listener on `LotteryDaily` (`AddonEvent.PostUpdate`).
- Every update reads board values directly from addon (`GameNumbers[0..8]`).

Key files:
- `Saucy/MiniCactpot/MiniCactpot.cs`

### B) Reader over `AtkValues`

Method:
- Nested `Reader : AtkReader` extracts stage and number fields from indexed ATK values.

Key files:
- `Saucy/MiniCactpot/MiniCactpot.cs`
- `Saucy/OutOnALimb/ECEmbedded/AtkReader.cs`

Note:
- Solver logic (`MiniCactpot/Solver.cs`) is decision logic, not state acquisition, but input state comes from the addon event/update path above.

## Out on a Limb

### A) Addon-based state via `AtkValues`

Method:
- Polls minigame addons (`MiniGameBotanist`, `MiniGameAimg`) and reads state using `ReaderMiniGameBotanist : AtkReader`.
- Extracts fields like state, swings left, health, and remaining time.

Key files:
- `Saucy/OutOnALimb/ReaderMiniGameBotanist.cs`
- `Saucy/OutOnALimb/LimbManager.cs`

### B) Supplemental state from dialog text

Method:
- Reads `SelectString` / `SelectYesno` addon text nodes for prompts and payout extraction.
- Uses language-specific regex patterns for payout parsing.

Key files:
- `Saucy/OutOnALimb/LimbManager.cs`

## Cuff-a-cur (Punching Machine)

### A) UI node inspection + addon callbacks

Method:
- Reads `PunchingMachine` addon nodes (e.g., sliding/indicator nodes) to infer timing/state.
- Interacts through callbacks/button presses.

### B) Signature-based hook for event injection

Method:
- Creates a hook from scanned signature and calls original with crafted event args.
- Uses this to transition to results handling and synchronize state with result reader.

Key files:
- `Saucy/CuffACur/CufModule.cs`

## Shared Results UI for Cuff-a-cur / Out on a Limb

Method:
- `UIReaderGamesResults` tracks `GoldSaucerReward` addon.
- Parses nested node tree for MGP result and classifies result buckets.

Key files:
- `Saucy/TripleTriad/TriadBuddy/plugin/UIReaderGamesResults.cs`

## Other Gold Saucer Modules

These modules mostly rely on world/object state overlays rather than deep addon memory layouts:

- `AnyWayTheWindBlows`: checks player position + object presence (`Svc.Objects`) to determine safe spot conditions.
- `SliceIsRight`: renders guidance based on game objects/geometry cues.

Key files:
- `Saucy/OtherGames/AnyWayTheWindBlows.cs`
- `Saucy/OtherGames/SliceIsRight.cs`

## Practical Takeaways for Reverse Engineering

Patterns Saucy demonstrates that are useful for your own plugin work:

1. Start with addon name + visibility polling; only move to signatures when necessary.
2. Keep readers modular per addon and route lifecycle through a scheduler (`shown/update/lost`).
3. Use explicit struct overlays for stable/high-frequency fields (fast path).
4. Use ATK tree traversal for text-heavy or patch-fragile UI data (flexible path).
5. Maintain fallback paths for agent pointers (`FindAgentInterface` first, `UIModule->AgentModule` fallback).
6. Expect patch breakage in hardcoded offsets/signatures; include failsafes and error states.

## Reliability / Risk Notes

- Highest patch risk:
  - hardcoded field offsets in `StructLayout` overlays,
  - signature patterns,
  - assumptions about node tree indices/shape.
- More resilient mechanisms:
  - addon lifecycle events,
  - visibility-gated polling with null checks,
  - modular readers with explicit error states.
