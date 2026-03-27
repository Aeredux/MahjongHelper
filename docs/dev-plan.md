# Mahjong Helper — Development Plan

## Overview

A Dalamud plugin for FFXIV's Doman Mahjong (Gold Saucer) that reads in-game state from the "EmjL" addon, sends it to a local Riichi Mahjong solver server (`localhost:8080`), and displays/executes suggested moves.

## Build / Deploy Note

- For in-game testing and deployment, always update the Release x64 build output.
- Target artifact: `SamplePlugin/bin/x64/Release/SamplePlugin.dll`
- Do not assume the Debug build output is the correct DLL for user testing.

## Architecture

```
┌──────────────────────────────────────────────────────────┐
│                    FFXIV Game Client                     │
│  ┌────────────────────────────────────────────────────┐  │
│  │  "EmjL" Addon (Mahjong UI)                        │  │
│  │  - ATK node tree with tile image nodes             │  │
│  │  - Tile nodes: 34x45 px, image part IDs = tiles    │  │
│  │  - Discard pools, call indicators, game controls   │  │
│  └──────────────┬─────────────────────────────────────┘  │
│                 │ read via unsafe pointers                │
│  ┌──────────────▼─────────────────────────────────────┐  │
│  │  MahjongHelper Plugin (Dalamud)                    │  │
│  │                                                    │  │
│  │  ┌─────────────┐  ┌────────────────────────────┐   │  │
│  │  │ GameState   │  │ ServerClient               │   │  │
│  │  │ Reader      │──│ POST /api/suggest-move      │   │  │
│  │  │ (ATK parse) │  │ POST /api/evaluate-call     │   │  │
│  │  └─────────────┘  └────────────┬───────────────┘   │  │
│  │                                │                   │  │
│  │  ┌─────────────────────────────▼───────────────┐   │  │
│  │  │ UI Overlay                                  │   │  │
│  │  │ - Highlight suggested discard tile          │   │  │
│  │  │ - Show shanten, ukeire, reasoning           │   │  │
│  │  │ - Call decision prompts (chi/pon/kan/ron)    │   │  │
│  │  └─────────────────────────────────────────────┘   │  │
│  │                                                    │  │
│  │  ┌─────────────────────────────────────────────┐   │  │
│  │  │ AutoPlayer (optional)                       │   │  │
│  │  │ - Click tile to discard                     │   │  │
│  │  │ - Accept/decline calls                      │   │  │
│  │  └─────────────────────────────────────────────┘   │  │
│  └────────────────────────────────────────────────────┘  │
│                                                          │
└──────────────────────────────────────────────────────────┘
         │
         │ HTTP (JSON)
         ▼
┌──────────────────────┐
│  Mahjong Server      │
│  localhost:8080       │
│  - /api/suggest-move  │
│  - /api/evaluate-call │
│  - /api/validate-move │
│  - /api/health        │
│  - /api/history       │
└──────────────────────┘
```

## Server API Summary

| Endpoint | Method | Purpose |
|---|---|---|
| `/api/health` | GET | Health check |
| `/api/suggest-move` | POST | Given hand (13–14 tiles), returns ranked discard suggestions with shanten, ukeire, confidence, reasoning |
| `/api/evaluate-call` | POST | Evaluates whether to chi/pon/kan/ron/riichi/tsumo |
| `/api/validate-move` | POST | Checks if a discard is legal |
| `/api/history` | GET | Past suggestion history |
| `/api/game-state/{id}` | GET | Stored game record by ID |

Tile format: uppercase strings — `M1`–`M9`, `P1`–`P9`, `S1`–`S9`, `EAST`, `SOUTH`, `WEST`, `NORTH`, `WHITE`, `GREEN`, `RED`, `M0`, `P0`, `S0` (red fives).


## Development Phases

### Phase 1: Tile Mapping Discovery

**Goal:** Determine the mapping between EmjL addon image part IDs and actual Mahjong tile types.

- [x] **1.1** Add a dedicated file logger that periodically dumps ATK tree data to a known file (e.g., `%APPDATA%/MahjongHelper/dump.txt`) so the addon state can be inspected outside the game without needing live Dalamud log access.
- [x] **1.2** Refine `AtkTreeDumper` to focus on tile-relevant nodes — extract image part IDs, node IDs, positions, and visibility for all 34x45 nodes in a structured format (JSON or CSV).
- [x] **1.3** With a live game session: user reports their hand, compare against dumped image part IDs to build the tile ID mapping table.
- [x] **1.4** Validate the mapping across multiple hands/rounds to confirm it's consistent. Store the mapping in a config/data file.

### Phase 1.5: IDataManager-Based Icon ID Discovery

**Goal:** Auto-discover the complete icon-ID-to-tile-code mapping using FFXIV's own game data sheets, replacing manual hover learning and hardcoded guesses.

**Approach:**
- FFXIV stores Doman Mahjong tile definitions in Excel data sheets accessible via `IDataManager.GetExcelSheet<T>()`.
- The relevant sheet is likely `DomanMahjongTile` (or similar). Each row should contain:
  - A tile identifier or ordering index,
  - An icon ID (the same icon IDs we see loaded via `LoadIconTexture` at runtime: 76041–76077 range),
  - Possibly a tile name string.
- By iterating the sheet rows, we can build a complete, authoritative mapping of all 34 tile icon IDs to tile codes without any runtime observation, hover learning, or manual entry.

**Why this is better than previous approaches:**
1. **Hover-learning** was disabled as unreliable (stale tooltip values, conflicting mappings).
2. **Hardcoded baseline** mappings were wrong (`76069→P4`, `76070→S9` were incorrect).
3. **Manual breakpoint inspection** only reveals tiles currently in hand — needs many games to see all 34.
4. **Sequential icon ID guessing** (76041=M1, 76042=M2, ...) is plausible but unverified.
5. **IDataManager** reads the game's own source of truth — guaranteed correct, survives tile set changes, and auto-discovers all 34 tiles in one pass.

**Implementation steps:**
- [x] **1.5.1** Explore available Excel sheets via `IDataManager` to find the Doman Mahjong tile definition sheet. → **No tile sheet exists.** Emj sheets found: `EmjAddon`, `EmjCostume`, `EmjDani`, `EmjVoiceNpc`, `EmjCharaViewCamera` — none contain tile icon data.
- [x] **1.5.2** ~~Read all rows from the sheet and extract icon IDs + tile identity data.~~ → N/A, no sheet exists.
- [x] **1.5.3** Build the icon-ID-to-tile-code mapping from sequential icon range (76041–76074) and populate `MahjongIconMap.BuiltInMappings`. (Fallback path used.)
- [x] **1.5.4** Log the discovered mappings at plugin startup for verification.
- [x] **1.5.5** Populate `BuiltInMappings` with all 34 tile entries; `LockedBuiltInIconIds` locks all.
- [x] **1.5.6** Verify in-game that all 37 tiles now resolve correctly in the UI state display. (34 standard + 3 red fives confirmed.)

**Fallback:** If `IDataManager` does not expose a Mahjong tile sheet, fall back to the `EmjModule.TileSet` field + icon ID range enumeration (76041–76074 sequential mapping). The sequential range is strongly suggested by observed icon IDs in gameplay, but the data manager approach is preferred because it's self-documenting and self-correcting.

### Phase 2: Game State Reader

**Goal:** Reliably extract the full game state from the EmjL addon.

- [x] **2.1** Implement `GameStateReader` class that parses the ATK tree and returns a structured game state object (player hand tiles, drawn tile).
- [x] **2.2** Identify and read discard pool nodes for each player (opponent discards, tsumogiri detection if possible).
- [x] **2.2a** Identify dora indicator tiles (visible on the dead wall / game board UI).
- [x] **2.3** Identify seat wind indicators and riichi status for each player.
- [x] **2.4** Identify call decision UI elements (when the game prompts for chi/pon/kan/ron/tsumo).
- [x] **2.5** Detect game phase: waiting for discard, waiting for draw, call decision prompt, between rounds, etc.

### Phase 3: Server Communication

**Goal:** Send game state to the local server and receive suggestions.

- [x] **3.1** Implement `MahjongServerClient` with `HttpClient` — health check, suggest-move, evaluate-call, validate-move endpoints.
- [x] **3.2** Map internal game state to the server's JSON request format (tile type strings, opponent discard arrays).
- [x] **3.3** Parse server responses into internal suggestion/evaluation models.
- [x] **3.4** Add error handling: server unreachable, timeout, malformed response. Show user-friendly errors in the plugin UI.
- [x] **3.5** Add a health-check indicator in the UI (green/red dot showing server connectivity).

### Phase 4: Suggestion Display UI

**Goal:** Show the server's suggestions to the player in-game.

- [x] **4.1** Design the main overlay window (ImGui) showing: current shanten, best discard, ukeire count, reasoning text.
- [x] **4.2** Display the full ranked suggestion list (all discard options sorted by confidence).
- [ ] **4.3** Highlight the recommended discard tile in the player's hand (visual indicator on the actual game tile node, or an arrow/border in the overlay).
- [x] **4.4** Show call decision recommendations when a call prompt appears (should I chi/pon/kan? with confidence and reasoning).
- [x] **4.5** Add toggle to show/hide the overlay, and a compact mode (just the best discard tile name).

### Phase 5: Auto-Play Mode

**Goal:** Optionally automate tile discards and call decisions.

- [ ] **5.1** Implement tile click automation — simulate clicking the suggested discard tile node in the EmjL addon.
- [ ] **5.2** Implement call decision automation — click accept/decline on chi/pon/kan/ron prompts based on server evaluation.
- [x] **5.3** Add configurable delay before auto-actions (to look natural and allow user override).
- [x] **5.4** Add a master toggle for auto-play (off by default), separate toggles for auto-discard and auto-call.
- [x] **5.5** Add a "pause" keybind that temporarily disables auto-play for the current turn.

### Phase 6: Plugin Polish & Configuration

**Goal:** Clean up, rename, and make the plugin user-friendly.

- [ ] **6.1** Rename the plugin from "Sample Plugin" to "Mahjong Helper" — update `SamplePlugin.json`, namespaces, project name, and solution file.
- [ ] **6.2** Configuration window: server URL (default `localhost:8080`), auto-play toggles, overlay position/size, compact mode, delay settings.
- [ ] **6.3** Persist user settings via `Configuration.cs`.
- [ ] **6.4** Clean up unused sample code (goat image, random config bools, sample territory/job display).
- [ ] **6.5** Add a proper plugin icon and description for the Dalamud plugin list.
- [ ] **6.6** Separate debug/node-exploration code from functional code — move dump utilities (`TileDataDumper`, `StateComparisonLogger`, raw node scanning), debug UI buttons, and discovery helpers into a `Debug/` subfolder or namespace. Keep `Plugin.cs`, `MainWindow.cs`, and the `Mahjong/` folder focused on production functionality.

## Key Technical Decisions

| Decision | Rationale |
|---|---|
| File-based logging for tile discovery | Dalamud log files may be hard to access programmatically. A dedicated dump file at a known path enables AI-assisted iterative discovery. |
| Unsafe pointer ATK traversal | Required to read FFXIV UI state. Already implemented in `AtkTreeDumper`. |
| `HttpClient` for server communication | Standard .NET HTTP client, simple JSON serialization. |
| ImGui overlay for suggestions | Dalamud's native UI framework, already in use. |
| Separate highlight vs auto-play modes | Highlight-only is safe and always useful. Auto-play is opt-in for users who want full automation. |
| Configurable action delay | Makes auto-play less robotic and gives users time to override. |

## Data Models

### GameState (internal)

```csharp
class GameState
{
    List<TileType> Hand;          // 13 or 14 tiles
    TileType? DrawnTile;          // last drawn tile
    SeatWind PlayerWind;          // player's seat wind
    OpponentInfo[] Opponents;     // per-opponent discard/riichi info
    GamePhase Phase;              // discard, draw, call-prompt, etc.
}
```

### TileType (enum/mapping)

```csharp
enum TileType
{
    M1, M2, M3, M4, M5, M0, M6, M7, M8, M9,  // Manzu (M0 = red five)
    P1, P2, P3, P4, P5, P0, P6, P7, P8, P9,  // Pinzu (P0 = red five)
    S1, S2, S3, S4, S5, S0, S6, S7, S8, S9,  // Souzu (S0 = red five)
    EAST, SOUTH, WEST, NORTH,                  // Winds
    WHITE, GREEN, RED                           // Dragons
}
```

### ImagePartId → TileType mapping

```
// DISCOVERED — sequential icon IDs 76041–76077
// 76041–76049: M1–M9
// 76050–76058: P1–P9
// 76059–76067: S1–S9
// 76068–76071: EAST, SOUTH, WEST, NORTH
// 76072–76074: WHITE, GREEN, RED
// 76075–76077: M0, P0, S0 (red fives / aka dora)
```

## Open Questions

1. ~~**Tile-back detection**~~ — Face-down tiles use different node types/no icon. Player hand nodes 54–71 (type 1055, 42x55) hold the active hand; empty slots have no icon and pos=(0,0).
2. **Call prompt structure** — What does the EmjL addon look like when prompting for chi/pon/kan? Need to identify the relevant nodes.
3. ~~**Red fives**~~ — **Resolved.** Red fives use icon IDs 76075 (M0), 76076 (P0), 76077 (S0). The server tile format uses `M0`/`P0`/`S0` notation.
4. **Multiple rounds** — How does the addon transition between rounds? Need to detect round boundaries to reset state.
5. **Click simulation method** — Need to determine whether Dalamud provides a click/callback API for addon nodes, or if raw input simulation is needed.
6. **Discard pool layout** — Need to identify which node types/indices hold each player's discard pool, and how to distinguish the 4 player positions (self, shimocha, toimen, kamicha).
7. **Dora indicators** — Need to find the dead wall / dora indicator tiles in the node tree. These may be a separate set of tile component nodes.
