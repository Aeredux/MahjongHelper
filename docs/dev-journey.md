# Dev Journey

## 2026-03-25: Initial Design Plan

**What:** Created the development plan in `docs/dev-plan.md`.

**Why:** Needed a structured roadmap before starting implementation. The plan covers the full architecture — from tile mapping discovery through game state reading, server communication, suggestion UI, auto-play, and polish.

**Key decisions:**
- Fetched the Mahjong server API docs (`localhost:8080/docs`) to understand the exact request/response formats for `suggest-move`, `evaluate-call`, and `validate-move` endpoints.
- Planned 6 phases: tile discovery → game state reader → server client → suggestion UI → auto-play → polish.
- Will use a dedicated file logger (not just Dalamud logs) for tile mapping discovery, to enable AI-assisted iterative debugging.
- Highlight mode (display-only) and auto-play mode (click automation) will be separate toggleable features.
- Created `.github/copilot-instructions.md` for workspace-wide development guidelines (no assumptions, git commits per task, dev journey documentation).

## 2026-03-25: Phase 1 — Tile Mapping Discovery

**What:** Created `TileDataDumper.cs` for comprehensive offline analysis of EmjL addon data.

**Why:** Need to discover how FFXIV's Doman Mahjong represents tile types in memory to build a tile reader.

**Key findings so far:**
- EmjL addon has 330 nodes. Hand tiles are type 1045 components at NodeList indices 256-268 (13 hand tiles). Drawn tile is type 1022 at index 107.
- All hand tile AtkImageNodes share identical partId=18, UV(97,0,34,45) — this does NOT distinguish tile types.
- Tile face icons use AtkImageNode with flags=0x80 (icon texture mode). The icon is loaded dynamically via `LoadIconTexture(iconId)`.
- AtkValues[1] shows hovered tile name (e.g., "Bamboo (9)"), [2] shows its icon ID — but only for one tile at a time.
- Confirmed two icon mappings from hover data: P4=76069, S9=76071.
- Raw pointer byte reads caused AccessViolationException crash — rewrote entire dumper to use `Marshal.ReadByte()` for all exploratory reads.
- Added icon texture path extraction: follows PartsList→Parts→UldAsset→AtkTexture→Resource→FileName chain to get texture file paths (e.g., `ui/icon/076000/076071_hr1.tex`), which encode the icon ID directly.

**Technical decisions:**
- Using `Marshal.ReadIntPtr`/`Marshal.ReadByte` everywhere instead of raw `byte*` for crash safety.
- Reading MSVC `std::string` layout (SSO vs heap) to extract texture paths.
- Dumping to `%APPDATA%/MahjongHelper/tile_dump.txt` for offline analysis.

## 2026-03-25: Crash #3 — IsReadable page boundary bug

**What:** Game crashed again with `AccessViolationException` in `ScanInlineForPath` → `FindIconPathDeep`. Root cause: `IsReadable()` only called `VirtualQuery` once for the starting address, so reads spanning a page boundary (e.g., 0x100 bytes starting near the end of a committed region) would pass validation but crash when Marshal.ReadByte hit the next unmitted page.

**Fix:** Rewrote `IsReadable()` to loop `VirtualQuery` across all memory regions the range `[addr, addr+size)` touches, validating each one is committed and readable.

**Crash log location noted:** `C:\Users\alvin\AppData\Roaming\XIVLauncher\dalamud_appcrash_*.log` — newest file = latest crash.

## 2026-03-25: Crash #4 — TOCTOU race in FindIconPathDeep

**What:** Game crashed again, this time in `ReadCStringAt` called from `FindIconPathDeep`. Same root cause class: `AccessViolationException` from reading memory that was freed between the `IsReadable()` check and the actual `Marshal.ReadByte` call (TOCTOU race). The aggressive 3-level recursive pointer scan follows ~32K arbitrary addresses per tile in a live process — a race is inevitable.

**Fix:** Removed `FindIconPathDeep`, `ScanInlineForPath`, and `ReadCStringAt` entirely. These never found any icon paths anyway (all dead ends), and the approach is fundamentally unsafe for a live process. The dump still captures the +0xC0 pointer and 0x40 bytes of its target for manual analysis.

**Key insight:** Recursive pointer-following in live game memory is not viable. Need to pivot to a different strategy for tile identity discovery — likely game agents/state managers rather than scanning UI node memory.

## 2026-03-25: Breakthrough — live icon capture and real hand nodes

**What:** Hooked `AtkImageNode.LoadIconTexture`, captured live icon IDs as Mahjong tiles are loaded, and identified the real player hand nodes as visible `type=1055` components sized `42x55` with nested icon images.

**Why:** The old `1045`/`1022` surface-level node inspection was incomplete. Tile icon IDs are not persisted in the plain ATK node fields after load, so dump-only inspection could not reliably recover the hand.

**Key findings:**
- `TextureType=0` / `Resource=NULL` confirmed the tile icons are not stored through the static `AtkTextureResource` path.
- The icon IDs must be captured at load time; the hook now sees Mahjong tile icons in the `76041-76077` range.
- The visible full-size hand uses `type=1055` nodes at `NodeList[54..71]`, not the earlier `1045` nodes.
- Those `1055` nodes contain the icon-bearing `AtkImageNode` inside nested child components, so recursive traversal is required.
- Added a first-pass `MahjongHandReader` that extracts the current hand from the visible `1055` nodes and a `MahjongIconMap` that learns iconId-to-tile mappings from hover tooltips over time.

## 2026-03-25: Cache persistence for autonomous refresh

**What:** Persisted the live image-address-to-icon cache and the learned iconId-to-tile-name map under `%APPDATA%/MahjongHelper`.

**Why:** The plugin already auto-refreshes every few seconds, but rebuilds were wiping the in-memory hook state. That made the user re-trigger tile loads or hover again after each rebuild, which defeats the point of automatic refresh.

**Result:** Future rebuilds can recover the previous live capture state and learned tile names automatically, so the reader can continue from cached data instead of waiting for user interaction.

## 2026-03-25: Phase 1 stabilization — last hand fallback + baseline icon seeds

**What:** Added two stabilization improvements: persisted last resolved hand snapshot fallback, and a small built-in baseline icon map seed.

**Why:** Right after plugin reload, live icon capture can be cold for a few frames/seconds. This caused temporary empty hand snapshots and reduced trust in the reader output. The fallback keeps output usable until fresh captures arrive.

**Changes made:**
- `MahjongHandReader` now saves the latest non-empty resolved hand snapshot to `%APPDATA%/MahjongHelper/last_hand_snapshot.json`.
- When live read returns empty (or addon/capture is unavailable), `MahjongHandReader` now tries to return the persisted last snapshot with `IsFromCache=true`.
- Snapshot display text now indicates cache source when fallback is active.
- `MahjongIconMap` now seeds a conservative built-in mapping baseline (`76069->P4`, `76070->S9`, `76071->S9`) before loading learned cache.

**Result:** Phase 1 reader behavior is more stable during cold start and rebuild windows, while still converging to live captured data once icon loads occur.

## 2026-03-26: Phase 1 UX acceleration — mapping progress panel + export

**What:** Added a dedicated mapping-progress report in the main plugin window, with copy and export actions.

**Why:** Hover-based learning works but was inefficient because users had no clear view of what was still missing. The new report makes progress explicit and reduces unnecessary hovering.

**Changes made:**
- `MahjongIconMap` now builds a progress report including:
	- known mapping count,
	- discovered tile-code coverage out of 34 expected tile codes,
	- missing tile codes,
	- unknown observed icon IDs in likely Mahjong icon range,
	- full known `iconId -> tileCode` table.
- `Plugin` now refreshes this report during normal EmjL draw updates and startup dump path.
- Added `Plugin.ExportMappingReport()` to write `%APPDATA%/MahjongHelper/mapping_progress_report.txt`.
- `MainWindow` now includes:
	- `Copy Mapping Report` button,
	- `Export Mapping Report` button,
	- a read-only mapping progress panel.

**Result:** The mapping workflow is now checklist-driven. Export uses current cached state directly and does not require unhovering before save.

## 2026-03-26: Hover-learning regression fix — context-gated mapping updates

**What:** Fixed unstable icon learning where the same icon ID could appear to map to unrelated tile names depending on hover context.

**Why:** User reports showed conflicts like `76071 -> P2` with observations such as `76071 observed NORTH`, indicating stale/unrelated tooltip values were being mixed into learning.

**Changes made:**
- `MahjongIconMap.ObserveHover` now accepts eligible icon IDs and only learns when hovered icon ID is currently visible in live hand/drawn snapshot.
- `Plugin` now computes eligible icon IDs from `MahjongHandSnapshot` and passes them into hover learning.
- Increased confirmation requirement from 2 to 3 consecutive observations for a pair before learning.
- Restricted auto-learning to suit tile codes (`M1-9`, `P1-9`, `S1-9`) to avoid honor-tile tooltip noise for now.
- Locked built-in icon IDs so cached values cannot override baseline mappings on load.

**Result:** Mapping flips from unrelated hover contexts are blocked, and noisy observations are filtered before they can mutate learned map state.

## 2026-03-26: Mapping recovery controls — reset learned cache from UI

**What:** Added UI controls to clear corrupted learned icon mappings without manually editing cache files.

**Why:** Earlier hover-learning bugs could leave bad persisted mappings in `%APPDATA%/MahjongHelper/icon_name_cache.json`. Users needed a safe recovery path before resuming mapping work.

**Changes made:**
- `MahjongIconMap.ResetLearnedMappings()` clears learned mappings and conflicts, then restores locked built-in baseline mappings.
- `MahjongIconMap.ResetLearnedMapping(iconId)` removes one non-locked icon mapping.
- `Plugin` now exposes reset actions for the main window.
- `MainWindow` now includes:
	- `Reset Learned Mappings` button,
	- text input + `Reset Icon ID` button for targeted cleanup.

**Result:** Bad learned cache entries can now be removed in-game before continuing Phase 1 mapping verification.

## 2026-03-26: Hover-learning cadence fix + full data reset

**What:** Fixed hover learning so it runs every draw instead of only on the 3-second dump throttle, and added a full data reset path.

**Why:** Requiring 3 consecutive hover confirmations was ineffective when hover observation only happened on throttled refresh. Brief but valid hovers were never recorded. Also, users needed a one-click way to clear all cached state, not only learned mappings.

**Changes made:**
- `Plugin.OnMahjongDraw` now calls hover learning every draw using the most recent eligible icon-ID set from the last live hand snapshot.
- `Plugin` now stores `_lastEligibleIconIds` from the latest resolved hand for frame-to-frame hover validation.
- Added `IconIdCapture.ResetCapturedIcons()` to clear persisted image-node icon capture cache.
- Added `MahjongHandReader.ResetCachedSnapshot()` to clear persisted last-hand snapshot fallback.
- Added `Plugin.ResetAllData()` and `Reset All Data` UI button to clear:
	- learned icon mappings,
	- conflict history,
	- captured icon cache,
	- cached hand snapshot,
	- exported mapping report file.

**Result:** Hover learning should respond during normal hovering again, and there is now a clean recovery path back to zero cached MahjongHelper state.

## 2026-03-26: Pivot to authoritative client Mahjong state probes

**What:** Added safe dump sections for the actual client Mahjong state holders exposed by FFXIVClientStructs: `UIState->Emj`, `AgentId.Emj`, and `EmjModule`.

**Why:** Hover-derived mapping and baseline seeds were not reliable enough to treat as the source of truth. Public client structs confirm Doman Mahjong has dedicated state objects, so the next step is to inspect those directly instead of continuing to infer tile identity from tooltip behavior.

**Changes made:**
- Extended `TileDataDumper` with a new `CLIENT MAHJONG STATE PROBES` section.
- Added safe raw dumps for:
	- `Client::Game::UI::UIState->Emj` (`0x38` bytes),
	- `Client::UI::Misc::EmjModule` (`0xD0` bytes),
	- `Client::UI::Agent::AgentId.Emj` (first `0xA0` bytes).
- Added aligned dword and pointer-field reporting for those opaque structs, plus bounded hex dumps of readable pointer targets.
- Reused the existing `VirtualQuery`-backed readability checks so the new probes stay within the repo's established crash-safety constraints.

**Result:** Future in-game dumps can now be compared against live hand changes to determine whether the real Mahjong state is present in `UIState->Emj` or the Mahjong agent, which is a stronger path than continued hover inference.

## 2026-03-26: UI quality-of-life — copy client probes button

**What:** Added a dedicated `Copy Client Probes` button to the main window.

**Why:** The raw dump is large, but current analysis focuses on the `CLIENT MAHJONG STATE PROBES` section. A targeted copy action avoids manual selection and accidental omission when sharing probe data.

**Changes made:**
- `MainWindow` now includes a `Copy Client Probes` button next to existing copy actions.
- Added section extraction helper that copies text from:
	- `--- CLIENT MAHJONG STATE PROBES ---`
	- up to `--- FULL NODE LIST ---`
- If probe markers are not present, the button falls back to copying the full raw dump text.

**Result:** Probe-only logs can be copied in one click for faster iteration and comparison.

## 2026-03-26: Deep AgentId.Emj pointer probing

**What:** Expanded Mahjong client-state probes to include deeper dumps of `AgentId.Emj` pointer targets.

**Why:** Before/after-turn captures still showed no differences in top-level `UIState->Emj` and first `0xA0` bytes of `AgentId.Emj`. That indicates the live tile/turn state is likely in nested heap structures reached via agent pointer fields.

**Changes made:**
- Added `DumpAgentEmjDeepPointers` in `TileDataDumper`.
- For candidate offsets (`+0x28`, `+0x30`, `+0x40`, `+0x48`, `+0x58`, `+0x60`, `+0x70`), dumper now:
	- resolves pointer targets safely,
	- dumps up to `0x200` readable bytes,
	- emits dword/pointer summaries,
	- emits a small nested-pointer preview (`0x40` bytes each, bounded count).
- Reused existing `VirtualQuery` readability guards to stay aligned with established crash-safety constraints.

**Result:** Future probe captures should expose deeper state deltas even when the top-level Mahjong structs remain stable across turns.

## 2026-03-26: Dump identity markers for capture validation

**What:** Added explicit unique markers to each dump header.

**Why:** Multiple captures were occasionally compared as if they were different game moments but ended up byte-identical. The header now makes it obvious whether two dumps are truly different captures before analysis.

**Changes made:**
- `TileDataDumper` now writes these header fields for every dump:
	- `DumpSequence` (monotonic in-process counter),
	- `UtcTimestamp` (ISO-8601),
	- `TickCount64`.

**Result:** Users can quickly confirm capture uniqueness and timing context without relying on filenames alone.

## 2026-03-26: Probe-section identity markers

**What:** Added unique capture markers directly inside `CLIENT MAHJONG STATE PROBES`.

**Why:** Probe-only copies can exclude the top-level dump header, which made two probe files appear identical without an obvious identity marker.

**Changes made:**
- `TileDataDumper.DumpClientMahjongState` now emits:
	- `ProbeSequence`,
	- `ProbeUtcTimestamp`,
	- `ProbeTickCount64`.

**Result:** Even probe-only dumps now carry built-in uniqueness markers for reliable comparison.

## 2026-03-26: Automatic probe change logging

**What:** Added background probe-state tracking that writes entries only when the Mahjong probe state meaningfully changes.

**Why:** Manual copy/compare loops were slowing investigation. This allows passive data collection while simply playing, without repeated user export steps.

**Changes made:**
- `Plugin` now extracts `CLIENT MAHJONG STATE PROBES` from each periodic dump.
- Added normalization that ignores volatile identity-only lines:
	- `ProbeSequence`,
	- `ProbeUtcTimestamp`,
	- `ProbeTickCount64`.
- On normalized state change, plugin appends full probe section to:
	- `%APPDATA%/MahjongHelper/probe_history.log`.

**Result:** Investigation can continue with minimal user action; probe transitions are captured automatically in a persistent timeline.

## 2026-03-26: Extracted live AgentId.Emj state signal

**What:** Added automatic extraction of the moving `AgentId.Emj+0x28` state value and logged state transitions to a dedicated signal file.

**Why:** Probe history analysis showed that `AgentId.Emj+0x28` dword `+0x08` changes over time (observed sequence included `6 -> 5 -> 6 -> 1`), indicating this field likely tracks Mahjong flow state.

**Changes made:**
- `Plugin.TrackProbeState` now parses the probe section for:
	- `AgentId.Emj+0x28` block,
	- dword line `+0x08: i32=...`.
- On change, plugin appends concise transition lines to:
	- `%APPDATA%/MahjongHelper/probe_signals.log`
	- format: `timestamp AgentId.Emj+0x28/+0x08 i32 changed: old -> new`.

**Result:** Phase investigation now has a compact, high-signal timeline of probable Mahjong state transitions without manual diffing.

## 2026-03-26: Direct tile-candidate delta logging (phase-independent)

**What:** Added automatic byte-delta extraction from `AgentId.Emj+0x28` to log likely tile/count fields directly, without requiring phase mapping.

**Why:** User captures were already consistently taken at the same decision moment (their turn), so phase identification is not the immediate bottleneck. The faster path is to isolate the specific offsets that change with hand updates.

**Changes made:**
- `Plugin` now parses the full `AgentId.Emj+0x28` hex block into a `0x200` byte snapshot.
- Added cross-dump delta tracking against the previous snapshot.
- Added focused candidate logging to `%APPDATA%/MahjongHelper/tile_candidates.log`:
	- byte changes in data-heavy ranges (`0x70..0x1EF`) filtered to compact value range (`0x01..0x40`),
	- u16 changes in `0x70..0x11F` filtered to `<= 0x0200`.
- Pointer-heavy header region is intentionally de-emphasized to reduce noise.

**Result:** Investigation now records a concise stream of direct candidate field changes that should correlate to draw/discard hand evolution, independent of phase labeling.

## 2026-03-26: Emj UIReader scaffold (Saucy-inspired slot model)

**What:** Added a first-pass Mahjong UI reader that produces a normalized slot-based state object from `EmjL` addon nodes, and surfaced it in the main window.

**Why:** To follow the same architecture pattern used by Saucy's Triple Triad reader: extract explicit slot state every frame first, then solve on a normalized game-state model.

**Changes made:**
- Added `SamplePlugin/Mahjong/EmjUiReader.cs` with:
	- normalized `UiState` / `UiSlot` model,
	- explicit slot reads for current observed player hand indices (`54..66`) and draw index (`107`),
	- visible tile candidate enumeration from `type=1055` tile components,
	- icon extraction through captured `LoadIconTexture` mapping for each slot.
- Wired reader into `Plugin` update flow (`TryImmediateDump` and periodic `OnMahjongDraw`) so state updates continuously.
- Added new read-only panel in `MainWindow`:
	- `Mahjong UI State (slot-based scaffold)`,
	- `Copy Mahjong UI State` button.

**Result:** Plugin now outputs a stable, solver-ready UI state scaffold separate from raw memory dumps, enabling offset/slot validation in a structured format.

## 2026-03-26: Passive UI-state logging and reverse-engineering ergonomics

**What:** Added explicit repo guidance and implementation to minimize required user actions during reverse-engineering.

**Why:** Manual copy/compare loops are unnecessary friction during live gameplay, especially when state can be captured automatically on plugin reload and periodic updates.

**Changes made:**
- Updated `.github/copilot-instructions.md` with a reverse-engineering ergonomics rule:
	- default to passive instrumentation and automatic logging,
	- avoid repeated manual user copy/compare actions unless unavoidable.
- Added automatic Mahjong UI-state history logging in `Plugin`:
	- new file `%APPDATA%/MahjongHelper/mahjong_ui_state_history.log`,
	- logs on startup/reload and periodic updates,
	- writes only on meaningful state change (signature-based dedupe, timestamp ignored in comparison).

**Result:** UI-state captures are now persisted automatically without requiring user button clicks, reducing test friction while playing.

## 2026-03-26: Canonical slot filtering for Mahjong UI state

**What:** Added canonical hand/draw selection in `EmjUiReader` to reduce placeholder/duplicate noise while preserving raw slot diagnostics.

**Why:** Uploaded UI-state sample showed valid data mixed with placeholder hand entries (`x=0`, no icon) and duplicated candidate rows, which still required manual interpretation.

**Changes made:**
- Added `CanonicalPlayerHand` and `CanonicalPlayerDraw` slot kinds.
- `EmjUiReader` now computes canonical hand from raw fixed hand slots by filtering for visible, positioned/icon-backed entries and ordering by X.
- Draw slot now uses raw draw when valid, with a fallback heuristic for shifted draw representation.
- Kept existing raw `PlayerHand` / `PlayerDraw` / `VisibleTileCandidate` output for offset-debug safety.

**Result:** Logged and on-screen UI state now starts with a cleaner, solver-oriented canonical view while still retaining raw evidence for reverse-engineering.

## 2026-03-26: Increased UI-state capture cadence for fast turns

**What:** Added a lightweight high-frequency UI-state sampler in `OnMahjongDraw` while keeping full dump cadence throttled.

**Why:** Normal play can advance turns quickly; 3-second dump cadence may miss intermediate slot transitions even though automatic logging is enabled.

**Changes made:**
- Added separate UI-state sampling interval (`250ms`) independent of full dump interval (`3s`).
- Fast path updates the UI-state panel and logs state changes via existing signature dedupe.
- Full dump, probe extraction, and heavier processing remain on the prior throttle to avoid performance regression.

**Result:** Reverse-engineering captures now track rapid in-game slot updates more reliably without requiring extra user actions.

## 2026-03-27: Phase 1.5 — IDataManager exploration + sequential icon mapping

**What:** Explored `IDataManager` Excel sheets for Mahjong tile data, found none, then implemented sequential icon ID mapping as the fallback.

**Why:** The two hardcoded baseline mappings (`76069→P4`, `76070→S9`) were confirmed incorrect by the user. Hover-learning was already disabled as unreliable. Needed to find the authoritative tile-to-icon mapping.

**IDataManager exploration results:**
- Searched `Lumina.Excel.dll` for all Emj-related sheet types. Found:
  - `EmjAddon` — addon layout settings, no tile data
  - `EmjCostume` / `EmjCostumeData` — tile back cosmetics
  - `EmjDani` / `EmjDaniR` — rank/dani progression
  - `EmjVoiceNpc` — NPC voice settings
  - `EmjCharaViewCamera` — camera settings
- Searched for "tile", "mahjong", "doman" — no tile definition sheet exists.
- **Conclusion: FFXIV does not expose a Mahjong tile Excel sheet.** Tile icons are hardcoded into the addon UI, not data-driven from sheets.

**Sequential mapping implementation:**
- Live gameplay data confirmed icon IDs in the `76041–76077` range appearing consistently for all tile types.
- The icon texture paths seen in earlier probes (e.g., `ui/icon/076000/076071_hr1.tex`) encode the icon ID directly, confirming the range is real.
- FFXIV icon numbering convention for Doman Mahjong: sequential starting at 76041, ordered as M1–M9, P1–P9, S1–S9, then honor tiles (EAST, SOUTH, WEST, NORTH, WHITE, GREEN, RED).
- Populated `MahjongIconMap.BuiltInMappings` with all 34 entries (76041–76074).
- Removed `LockedBuiltInIconIds` / `SeedBuiltInMappings` complexity since all mappings are now built-in.
- Logged discovered mappings at plugin startup for verification.

**Verification needed:** User should confirm in-game that tile codes now display correctly in the UI state panel instead of `ICON_XXXXX`.

## 2026-03-27: Fix — direct AtkImageNode icon ID fallback for mid-game reload

**What:** Added a direct struct-read fallback in `EmjUiReader` to read icon IDs from `AtkImageNode` when the `LoadIconTexture` hook cache is empty.

**Why:** Startup diagnostics (`startup_diag.txt`) revealed the root cause of all-`(missing)` normalized state: after a mid-game plugin reload, `IconIdCapture` has zero Mahjong entries because the `LoadIconTexture` hook only fires when icons are first loaded. Since tiles are already on screen, the hook never fires, every node returns `iconId=0`, canonical hand is empty, and the entire pipeline produces `(missing)`.

**Fix:**
- Added `TryReadIconIdFromStruct(AtkImageNode*)` in `EmjUiReader` that reads the icon ID via the texture resource chain: `PartsList→Parts[PartId]→UldAsset→AtkTexture.Resource→IconId`.
- This path was already proven by `TileDataDumper.DumpPointerFingerprints()` which successfully read icon IDs this way.
- Renamed `TryFindCapturedIcon`/`TryFindCapturedIconRecursive` → `TryFindIcon`/`TryFindIconRecursive` to reflect that icon discovery is no longer hook-only.
- The recursive icon finder now: (1) tries hook cache first (fastest), (2) falls back to direct struct read if hook returns 0.
- Both `TryReadNodeSlot` and the visible-candidates loop now use the unified `TryFindIcon` which includes both paths.

**Result:** Icon IDs should now be readable immediately after plugin reload without waiting for new `LoadIconTexture` calls. Combined with the 34-entry sequential mapping, the normalized state should show tile codes like `M8 P6 S4` instead of `(missing)`.

## 2026-03-27: Red five (aka dora) icon mappings confirmed

**What:** Added mappings for the three red five tiles (76075→M0, 76076→P0, 76077→S0) based on live gameplay correlation.

**Why:** User reported hand `1 1 2 5r 7 8 m | 5r 6 p | 4 4 9 s | SOUTH WEST` drawn `2p`. UI state showed two unmapped icons: `ICON_76075` between M2 and M7 (confirmed red five of characters) and `ICON_76076` between M8 and P6 (confirmed red five of circles). By pattern, 76077 is the red five of bamboo.

**Key insight:** FFXIV Doman Mahjong uses icons 76041–76074 for the 34 standard tiles (M1–M9, P1–P9, S1–S9, winds, dragons), then 76075–76077 for the three red fives (aka dora). This brings the total to 37 mapped icons.

**Changes made:**
- Added `[76075] = "M0"`, `[76076] = "P0"`, `[76077] = "S0"` to `BuiltInMappings` in `MahjongIconMap`.
- Added `M0`, `P0`, `S0` to `ExpectedTileCodes` (now 37 entries).
- Using "0" notation (standard Mahjong convention for red fives, e.g., 0m = red five of characters).

**Result:** All 37 Doman Mahjong tile icons are now mapped. The mapping progress report should show `37/37` known tile codes with no missing entries.

## 2026-03-27: Fix hand node range and drawn tile detection

**What:** Expanded player hand node indices from 54–66 to 54–71 and replaced the incorrect node 107 draw slot with position-gap-based drawn tile detection.

**Why:** User confirmed that node 107 (type 1022, 34x45) shows **another player's discard**, not the local player's drawn tile. Meanwhile, the actual drawn tile (P2) appeared at node 54 (pos=556), separated from the sorted hand by a ~52px gap vs normal ~42px tile spacing. Additionally, only 9 of 14 tiles were being read because nodes 67–71 (containing M1, M1, M2, M0, M7) fell outside the old Range(54, 13) hand slot range.

**Changes made:**
- `PlayerHandNodeIndices` expanded from `Range(54, 13)` to `Range(54, 18)` to cover all player hand nodes 54–71.
- Removed `PlayerDrawNodeIndex = 107` — this node is not the player's draw.
- Rewrote `BuildCanonicalDraw` to detect the drawn tile by finding the largest X-position gap between consecutive tiles in the sorted canonical hand. Normal tile spacing is ~42px; the drawn tile gap is ~52px+. Threshold set at 50px.
- After detecting the draw tile, it's removed from the canonical hand list and emitted as `CanonicalPlayerDraw` instead.
- Raised `BuildCanonicalHand` cap from 13 to 14 to allow the drawn tile through before gap detection runs.

**Result:** The canonical hand should now correctly show all 13 hand tiles plus the drawn tile identified separately, matching the actual in-game hand layout.

## 2026-03-27: Phase 2 completion — full game state reader

**What:** Implemented all remaining Phase 2 features: discard pool classification, dora indicator reading, seat wind/round wind detection, score reading, riichi status detection, call decision prompt detection, and game phase inference.

**Why:** Phase 2.1 (player hand + draw) was confirmed working. The remaining Phase 2 tasks were needed to provide a complete game state for the server communication phase.

**Changes made:**

1. **EmjUiReader — new types and reading methods:**
   - Added `GamePhase` enum: `Unknown`, `WaitingForDiscard`, `WaitingForDraw`, `CallDecisionPrompt`, `RiichiDecisionPrompt`, `TsumoDecisionPrompt`, `RonDecisionPrompt`, `BetweenRounds`, `GameOver`.
   - Added `CallOptions` flags enum: `Chi`, `Pon`, `Kan`, `Ron`, `Tsumo`, `Riichi`, `Skip`.
   - Added `UiGameInfo` record carrying all non-tile game state: seat wind, round wind, round number, honba, riichi sticks, 4 player scores, riichi status per player, available call options, inferred game phase, raw AtkValue ints.
   - Extended `UiState` record to include `UiGameInfo GameInfo`.
   - Added `ReadGameInfo()` — orchestrates all game info reading.
   - Added `ReadWindAndScoresFromTextNodes()` — scans visible text nodes for wind kanji/English and score values, classifies by spatial position.
   - Added `ReadCallPrompts()` — scans visible non-tile component nodes for text children containing call keywords (chi/pon/kan/ron/tsumo/riichi/skip in English and Japanese).
   - Added `ReadRiichiStatus()` — scans for narrow rectangular image nodes (riichi stick proportions) and classifies by Y/X position.
   - Added `InferGamePhase()` — infers phase from available call prompts (Ron → RonDecisionPrompt, Tsumo → TsumoDecisionPrompt, Riichi → RiichiDecisionPrompt, other calls → CallDecisionPrompt).

2. **MahjongGameState — new fields:**
   - Added: `SeatWind`, `RoundWind`, `RoundNumber`, `RiichiStatus` (bool[4]), `PlayerScore`/`RightScore`/`OppositeScore`/`LeftScore`, `AvailableCalls`, `GamePhase`.
   - Updated `ToDisplayText()` to show all new fields.
   - Added `IReadOnlyList<bool>` formatting support in `FormatValue`.

3. **MahjongGameStateBuilder — merge logic for new fields:**
   - Added `MergeNullableInt()` helper for wind/score fields with cached fallback.
   - Merge now populates all new fields from `UiGameInfo`.
   - `BuildNormalizedStateSignature()` and `BuildActiveSourcePath()` updated to include new fields.

4. **TileDataDumper — UI element discovery section:**
   - Added `DumpUiElementDiscovery()` section that scans:
	 - Visible text nodes (wind labels, scores, status text)
	 - Component-hosted text nodes (buttons, labels inside containers)
	 - Non-tile component nodes (potential buttons/indicators with child type counts)
	 - Non-tile image/NineGrid nodes (potential riichi sticks, indicators)
	 - AtkValues game state candidates (small ints, scores, bools, strings)
   - This section enables passive discovery of exact node IDs for future refinement.

**What's known vs needs live validation:**
- ✅ Discard pool classification (parent-node grouping + Y-sorting) — scaffolded, needs live data to confirm group assignment
- ✅ Dora indicator detection (small groups with narrow spatial spread) — scaffolded, needs live data
- ⚠️ Wind/score text node reading — heuristic spatial thresholds need live calibration
- ⚠️ Riichi stick detection — proportional image node scan, needs live validation of stick dimensions
- ⚠️ Call prompt detection — text-based keyword matching, needs live validation of button text
- ⚠️ Game phase inference — currently call-prompt-driven only, hand-count-based detection pending

**Result:** Phase 2 is code-complete. All game state reader features are implemented and compile cleanly. The UI state panel and normalized state panel now show the full game state including wind, scores, riichi status, available calls, and game phase. Live validation during gameplay will determine which heuristic thresholds need adjustment.

## 2026-03-27: Fix discard classification + add child-tree navigation for game state

**What:** Fixed discard tile classification (was showing all discards as dora indicators) and replaced heuristic text/image scanning with proper RootNode child-tree navigation based on DomanMahjongStatus reference code.

**Why:** User reported that discards were all being classified as DoraIndicator and were not separated by player. The spatial/parent-grouping heuristic was wrong. The user also pointed to the DomanMahjongStatus plugin which reads game state via specific NodeID paths in the ATK tree.

**Key discoveries from live dump data:**
- Discard tiles have **distinct NodeType values per player**:
  - `1021` = local player discards
  - `1022` = left player (kamicha) discards
  - `1023` = right player (shimocha) discards
  - `1024` = opposite player (toimen) discards
- Dora indicators during gameplay are NOT rendered as 34x45 tile nodes — they use a different mechanism. The DomanMahjongStatus reference only reads dora from the end-of-round score screen.
- The DomanMahjongStatus plugin navigates the ATK tree by NodeID chains (`GetChild(id, id, ...)`) from the RootNode, NOT by NodeList index.

**Changes made:**
1. **Rewrote `ClassifySmallTiles`** — replaced entire spatial/parent-grouping heuristic with simple NodeType-based mapping. ~100 lines of heuristic code replaced with ~15 lines of direct type checking.

2. **Added ATK child-tree navigation helpers:**
   - `FindChildById(AtkResNode* root, params int[] ids)` — walks a chain of NodeIDs through the tree
   - `FindDirectChildById(AtkResNode* node, uint id)` — finds a direct child by NodeID, handling both regular ChildNode lists and component UldManager trees
   - `ReadNodeText(AtkResNode* node)` — reads text content from a Text node
   - `ReadTextNodeInt(AtkResNode* root, params int[] ids)` — navigates to a text node and parses int (strips ×-prefix for honba/riichi sticks)

3. **Rewrote `ReadGameInfo`** using child-tree navigation:
   - Round indicator: root → 16 → 19 (image IconID 121451-121458 maps to East/South 1-4)
   - Honba count: root → 21 → 23 (text "×N")
   - Riichi stick count: root → 21 → 22 (text "×N")
   - Player panes: root → 36 → {37,39,41,43} → {38,40,42,44}
   - Scores: pane → {10,11} → {12,13} → 2 (text)
   - Seat winds: pane → {7,8} → {9,10} (text "East"/"South"/"West"/"North")

4. **Removed heuristic methods:**
   - `ReadWindAndScoresFromTextNodes` — replaced by child-tree navigation
   - `ReadRiichiStatus` — was producing false positives (matching arbitrary narrow image nodes)

5. **Added `ReadPlayerPane` helper** — reads score and seat wind from a player's info pane component.

**What's still pending:**
- Dora indicator reading during gameplay (need to investigate what node structure holds the dora tiles on the board)
- Riichi status per player (need to find the correct node path — DomanMahjongStatus doesn't expose this directly)
- Call prompt detection validation (text keyword matching still needs live testing)

## 2026-03-27: Phase 3 — Server Communication

**What:** Implemented the full server communication layer for sending game state to the local Mahjong solver server and receiving discard suggestions.

**New files:**
1. **`ServerModels.cs`** — Request/response DTOs:
   - `SuggestMoveRequest` — hand tiles, drawn tile, discard pools, dora, wind/round context
   - `EvaluateCallRequest` — hand tiles, call tile/type, discard context
   - `ValidateMoveRequest` — hand tiles + proposed discard
   - `DiscardPools` — per-player discard arrays
   - Response models: `SuggestMoveResponse` (with `DiscardSuggestion` list), `EvaluateCallResponse`, `ValidateMoveResponse`, `HealthResponse`
   - All use `System.Text.Json` attributes for snake_case JSON serialization

2. **`MahjongServerClient.cs`** — HTTP client wrapper:
   - Targets `http://localhost:8080` by default
   - Async methods: `CheckHealthAsync`, `SuggestMoveAsync`, `EvaluateCallAsync`, `ValidateMoveAsync`
   - Tracks health state, server version, last error
   - 5-second timeout, never throws (returns null on failure)
   - `GetStatusText()` for compact UI display

3. **`GameStateMapper.cs`** — Converts `MahjongGameState` → server request format:
   - Resolves icon IDs to tile code strings via `MahjongIconMap`
   - Handles 14-tile hand splitting (last tile as drawn)
   - Builds `DiscardPools` from state fields
   - Filters out unresolved/placeholder tile codes

**Plugin integration:**
- Server client created on plugin init, disposed on teardown
- Periodic health check every 30 seconds via `OnFrameworkUpdate`

## 2026-03-28: Phase 5B — Score Screen Auto-Advance, Riichi Diagnostics, UI Status

**What:** Implemented automatic score screen advancement, added riichi diagnostic logging, and added auto-play status display in the main window.

**Score screen auto-advance:**
- Added `BetweenRounds` phase detection for atk0=29 (stable score screen) and atk0=32 (animation/transition) in `EmjUiReader.InferGamePhase`
- `AutoPlayManager` schedules a score advance action with 3000ms delay when `BetweenRounds` detected
- `AddonClickHelper.TryAdvanceScoreScreen()` scans ULD nodes for text buttons, finds "Next"/"OK"/"Continue"
- `TryClickButton()` dispatches via `addon->ReceiveEvent` with the button's original registered `AtkEventType.ButtonClick` event

**Click approach discovery (extensive exploration):**
- FireCallback 7/8: No effect at atk0=29
- TryAcceptCallViaListClick: "Next" button parent has no ListItemClick event
- Direct `listener->ReceiveEvent`: Crashes the game (unsafe pointer dispatch)
- PostMessage with coordinates: FFXIV doesn't process WM_LBUTTONDOWN for addon UI
- SendInput mouse simulation: No state change
- **Working approach:** `addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt, eventData)` with the ORIGINAL registered event structure. Key insight: must preserve the original listener pointer; must use `AtkEventType.ButtonClick` enum name (NOT `(AtkEventType)0x09` which has a different numeric value).

**Riichi diagnostics:**
- Added `[RIICHI-DIAG]` logging when riichi suggestion detected, `RiichiDecisionPrompt` phase entered/left, and provider decision for riichi moves

**Auto-play UI status:**
- Added `autoPlayStatusText` field to `MainWindow` displaying ON/OFF state, provider name, current phase, pending action, and paused status
- Status text generated in `Plugin.HandleMergedStateUpdate`

**ISuggestionProvider interface:**
- New file `ISuggestionProvider.cs` with interface + `InGameSuggestionProvider` (reads EmjL addon suggestions) and `ServerSuggestionProvider` (calls Mahjong server API)
- Supports discard suggestions, call evaluations, and riichi decisions from either source
- Auto-suggest on state change: when hand changes, builds `SuggestMoveRequest` and fires async request
- Throttled to 1 request/second, skips duplicate hand signatures
- Server status shown in diagnostics and as a status line in MainWindow
- Suggestion text displayed in a new "Server Suggestion" panel in MainWindow

## 2026-03-27: Phase 4 — Suggestion Display UI (overlay)

**What:** Created a dedicated in-game overlay window for displaying AI discard suggestions during Mahjong games.

**New file: `SuggestionOverlayWindow.cs`**

Two display modes:
- **Compact mode:** Single line showing best discard tile + shanten + ukeire. Click `[+]` to expand.
- **Full mode:** Header with shanten count + server status, ranked suggestion table (tile, shanten-after-discard, ukeire, confidence), hover tooltips for reasoning text, call recommendation area. Click `[-]` to collapse.

Features:
- Semi-transparent dark background for readability over game UI
- Color-coded: gold for best pick, green/cyan/white for ukeire ranges, red for errors
- Shows up to 8 suggestions with "... +N more" for overflow
- Auto-hides when not in a Mahjong game (tied to reader status)
- Call recommendation area (placeholder for Phase 4.4)

**Configuration:**
- `OverlayVisible` — persisted toggle for overlay visibility
- `OverlayCompactMode` — persisted toggle for compact vs full mode

**Commands:**
- `/mj overlay` — toggle overlay visibility
- `/mj compact` — toggle compact/full mode
- `/mj` — toggle debug window (unchanged)

**Integration:**
- Overlay fed live data from Plugin.cs: suggestion response, server status, hand description, current turn
- Auto-opens when in a Mahjong game, auto-closes when not

## 2026-03-27: Phase 5 callback discovery instrumentation (EmjL)

**What:** Added a dedicated discovery workflow to identify real EmjL callback bindings for discard/call automation without asking the user to read in-game UI values.

**Changes made:**
- `Plugin.cs`
  - Fixed initialization order bug: `Configuration` is now loaded before constructing `AutoPlayManager`.
  - Added passive `action_probe.log` snapshots on normalized-state transitions:
    - source, phase, turn, available calls
    - hand description and draw icon
    - all discard pools (P/R/O/L)
  - Added Atk snapshot logging via `AddonClickHelper.LogAtkSnapshot(...)` whenever action-probe state changes.
  - Added command-driven discovery helpers:
    - `/mj mark discard` — annotate a manual discard action
    - `/mj mark call` — annotate a manual call accept/decline action
    - `/mj probecallback <a> <b> [run]` — dry-run by default, optional guarded execution for callback probes
- `AddonClickHelper.cs`
  - Added `TryFireProbeCallback(...)` helper for controlled callback probing and logging.
  - Kept auto-discard/auto-call execution in dry-run mode pending callback confirmation.
- `Configuration.cs`
  - Cleanup/fix: removed duplicate `using` directive encountered during build.

**Result:**
- Discovery is now file-driven and reproducible (`action_probe.log`, `autoplay.log`, `server_log.txt`) and supports controlled callback probing from commands.
- No production behavior changed for players (automation remains safe dry-run until callback IDs are confirmed).

## 2026-03-27: Fix Stale Icon ID Reading in Hand Tiles

**What:** Fixed incorrect hand tile readings caused by stale hook-captured icon IDs.

**Why:** Users reported hand tiles showing wrong tile codes (e.g., SOUTH instead of M2). Root cause: `TryFindIconRecursive` in `EmjUiReader.cs` prioritized the `IconIdCapture` hook cache over the direct struct read. When the game reuses image node addresses for different tiles without re-calling `LoadIconTexture`, the hook cache retains stale icon IDs from previous tile states. The struct-based read (`TryReadIconIdFromStruct`) reads the ground truth from the texture resource chain and always reflects the currently loaded texture.

**Changes:**
- `EmjUiReader.cs`
  - Swapped icon read priority in `TryFindIconRecursive`: struct-based read (`TryReadIconIdFromStruct`) now runs first as ground truth; hook-based `IconIdCapture` is used as fallback only (useful after mid-game plugin reload when struct chain may not yet be populated).
  - Added `TryReadIconIdFromStructPublic` public wrapper so `MahjongHandReader` can also use the struct-based read.
- `MahjongHandReader.cs`
  - Applied same fix to `TryFindCapturedIconRecursive`: struct read first, hook capture as fallback.
- `MahjongIconMap.cs`
  - Added diagnostic logging in `Resolve`: when an icon ID in the mahjong range (76041-76150) has no mapping, logs it to `%APPDATA%/MahjongHelper/unmapped_icons.log` (deduplicated per session).
  - This enables self-serve discovery of alternate tile set icon IDs (e.g., icon 76127 observed for what should be NORTH).

## 2026-03-27: Call-accept via button node clicking (Phase 5.2)

**What:** Implemented a new approach for accepting call prompts (Pon/Chi/Kan/Ron/Tsumo/Riichi) by clicking the actual UI button component nodes, after confirming FireCallback doesn't work for call acceptance.

**Why:** The callsweep instrumentation (FireCallback probing with IDs 0-6, 9, 11-15 and values 0-2) was executed during a live Pon prompt but had zero effect — confirming FireCallback is a dead end for accepting calls. The EmjL addon uses real button components (type 1029) inside container/list hierarchies (1032→1030→1029) that need ReceiveEvent-based clicking.

**Changes:**
- `EmjUiReader.cs`
  - Added `CallButtonNodes` optional parameter to `UiGameInfo` record to carry captured button pointers
  - Modified `ReadCallPrompts()` to output `Dictionary<CallOptions, nint>` mapping each detected call to its button component pointer
  - Modified `ScanComponentForCalls()` to store `(nint)comp` when a call text match is found, using `buttonNodes.TryAdd(matched.Value, (nint)comp)`
  - Updated `ReadGameInfo()` call site to pass `callButtonNodes` into `UiGameInfo`
- `AddonClickHelper.cs`
  - Added `TryClickCallButton()` method with 4 click strategies: (1) component MouseClick, (2) addon MouseClick, (3) event 0x17 (Saucy pattern), (4) event 0x09 (ButtonClick)
  - Method 0 (auto) tries all 4 in sequence for discovery
- `AutoPlayManager.cs`
  - Added `_lastCallButtonNodes` field, updated `OnGameStateUpdate()` to accept button nodes
  - Replaced dry-run stub in `ExecuteCallResponse()` with actual button clicking using priority order: Ron > Tsumo > Kan > Pon > Chi > Riichi
- `Plugin.cs`
  - Added `/mj clickcall [type] [method] run` command for manual testing of individual call button clicks
  - Passes `CallButtonNodes` from `_lastUiState` to `AutoPlayManager.OnGameStateUpdate()`

## 2026-03-31: ECommons-Style Button Click (Call Accept Breakthrough)

**What:** Researched ECommons/ClickLib Dalamud community patterns for clicking addon buttons. Discovered that all previous click methods (1-5) were fundamentally wrong — they fabricated events from scratch with arbitrary parameters. The correct approach reads pre-registered events from `AtkResNode.AtkEventManager.Event` and replays them.

**Why:** Methods 1-5 (ButtonClick 25, MouseClick 9, Press+Release+Click, ListItemClick 35) all dispatched cleanly but had zero effect on call prompt buttons. The ECommons `ClickAddonButton` extension method revealed the correct pattern: buttons store their own registered events (type, param, target, listener) in a linked list, and the correct click simulation replays those registered events through the addon.

**Key discovery:**
```csharp
// ECommons pattern (what works):
var evt = resNode->AtkEventManager.Event;  // read registered event
addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt);

// Our old pattern (what didn't work):
var evt = new AtkEvent { Param = 0, Target = ..., Listener = ... };
addon->ReceiveEvent((AtkEventType)25, 0, evt, eventData);  // fabricated params
```

**Changes:**
- `AddonClickHelper.cs`
  - Method 6: ECommons-style — reads registered events from AtkEventManager, replays first through addon
  - Method 7: Replays ALL registered events individually with AtkSnapshots between each
  - Method 8: Walks parent chain (button → list → container) and replays events from each ancestor
  - Method 0 diagnostic: now dumps all registered events on button node and parent chain (event type, param, target, listener, flags)
- `Plugin.cs`
  - clickcall command: method 0 (diagnostic) now auto-executes without requiring 'run' keyword
  - clickcall command: all output now also written to autoplay.log for reliable debugging
- `AutoPlayManager.cs`
  - `ExecuteCallResponse()` now uses method 6 (ECommons-style) instead of method 1 (ButtonClick 25)
- `PluginDebugHelpers.cs`
  - Added `LogToFile(filename, message)` helper for writing to specific log files

## 2026-03-31: ListItemClick From Parent List Node (Call Accept Fix)

**What:** Discovered why methods 6-8 failed during user testing: method 6 replayed MouseOver (first event on button), causing a tile hover. Method 7 replayed all button events including MouseOut, cancelling the hover. Method 8 walked parent chain but only replayed the FIRST event at each depth, missing the critical ListItemClick at event[4] on the list node.

**Root cause:** The captured Chi button node (type=1029, nodeId=2) is a list item inside a type=1030 List component. Its registered events (MouseOver, ButtonClick, DragDrop*, etc.) are standard list-item events with param=1 (the item's index). The actual call-acceptance event is **ListItemClick** on the PARENT list node (depth=1), which has a different listener (the addon handler at 2C6BAAC3810) than the button's events (listener 2C726009F60).

**Key insight:** In FFXIV's ATK system, when a user clicks a list item, the game fires `ListItemClick` through the list component's registered event to the addon handler. The param argument to `ReceiveEvent` specifies WHICH item was clicked. The button node and list node have DIFFERENT listeners — the button's listener handles visual state, the list's ListItemClick listener handles the actual selection logic.

**Changes:**
- `AddonClickHelper.cs`
  - Method 9: Navigate to parent list node, find its ListItemClick event, fire through addon with button's item param (e.g., 1 for Chi)
  - Method 10: Same as 9 but fires with param=0 (to test if indexing differs)
  - Method 11: Fire ButtonClick (not MouseOver) from button's own events through addon

## 2026-04-02: Phase A — In-Game Suggestion Probe Instrumentation

**What:** Added passive instrumentation to log the game's in-game suggestion system data for reverse-engineering. Created a plan document (`docs/in-game-suggestion-plan.md`) for pivoting auto-play to use in-game suggestions instead of the unreliable AtkVal[0] state machine.

**Why:** The previous AtkVal[0] + stale-button approach for phase detection has fundamental reliability issues:
- Stale call buttons persist in the ATK tree after prompts are dismissed, causing false CallDecisionPrompt
- Callback 8 has dual meaning (tsumogiri at AtkVal[0]=30, skip at AtkVal[0]=6)
- AtkVal[0] values overlap between real call prompts and normal gameplay

The game already has built-in suggestion strings in AtkValues (tile names, action labels like "Discard", call labels like "Pon!"/"Chi!"). Reading these is more reliable than reconstructing state from raw integers.

**Changes:**
- `EmjUiReader.cs`
  - Added `LogSuggestionProbe()`: reads String8 AtkValues at indices [1],[6],[22],[23],[24],[45] and [30-37], logs to `suggestion_probe.log` on change
  - Added `LogSuggestionNode()` + `FlushSuggestionNodes()`: logs visible "!" suggestion text nodes (previously silently skipped) with visibility and owner info
- `docs/in-game-suggestion-plan.md` — created 4-phase plan (A: instrument, B: state from suggestions, C: act on suggestions, D: hybrid mode)

## 2026-04-02: Phase B — ReadInGameSuggestion + Suggestion-First Phase Detection

**What:** Implemented `ReadInGameSuggestion()` reading AtkValues[6] for the game's action recommendation, and rewrote `InferGamePhase` to use the suggestion as the primary signal instead of the unreliable AtkVal[0] state machine.

**Why:** AtkValues[6] reliably indicates "Discard", "Pass", "Chi!", "Pon!", "Ron!", "Tsumo!", scoring strings, etc. Using this as the primary phase signal eliminates stale-button and AtkVal[0]-overlap issues.

**Changes:**
- `EmjUiReader.cs`
  - Added `SuggestionType` enum (None, Discard, Pass, Chi, Pon, Kan, Ron, Tsumo, Riichi, Scoring)
  - Added `InGameSuggestion` record (Type, RawText, TileName?, TileIconId?)
  - Implemented `ReadInGameSuggestion()` — parses [6] string to determine suggestion type
  - Rewrote `InferGamePhase`: suggestion-first logic with AtkVal fallback only when suggestion is None; call-button-based detection commented out
  - `ReadGameInfo` updated to pass `iconCapture`/`iconMap` through to `ReadInGameSuggestion`
- `MahjongGameState.cs`
  - Added `InGameSuggestion` StateField<string> to the record
  - `ToDisplayText()` shows InGameSuggestion
  - Merge method formats suggestion as `"{Type}:{RawText} tile={TileName} icon={TileIconId}"`
- `Plugin.cs`
  - Heartbeat includes `sug=` field

## 2026-04-02: Bug Fixes — Stale Discard & Callback 8 Safety Guard

**What:** Fixed two bugs caused by stale AtkValues and dual-meaning callbacks.

**Bug 1 — [6]="Discard" stale override:** `[6]="Discard"` persists into AtkVal[0]=6 call prompts, causing the plugin to stay stuck on WaitingForDiscard. Fixed by only trusting "Discard" when AtkVal[0]=30 or 2 (actual draw/after-call turn).

**Bug 2 — Callback 8 tsumogiri at non-6 states:** Stale call buttons at rawAtk0=15 triggered CallDecisionPrompt → call:pass → callback 8. But callback 8 at rawAtk0=15 means "discard drawn tile" (tsumogiri), not "skip call." Fixed by adding a rawAtk0=6 guard in `ExecuteCallResponse` — only fires callback 8 when rawAtk0==6.

**Changes:**
- `EmjUiReader.cs` — InferGamePhase only maps Discard→WaitingForDiscard when atk0=30/2
- `AutoPlayManager.cs` — `ExecuteCallResponse` skip/pass path reads rawAtk0 and only fires callback 8 when rawAtk0==6

## 2026-04-02: Suggestion Tile from Node 45 + Dora Indicator Reading

**What:** Fixed the suggested discard tile reading to use the actual visual tile icon from node 45 instead of the stale hover tooltip AtkValues[1]/[2]. Also identified and implemented dora indicator reading from the UI.

**Suggestion tile fix:** AtkValues[1] is a mouse hover tooltip (changes with cursor movement) and AtkValues[2] is a static icon (76070 = West, never changes). The actual suggested discard tile is rendered as a visible component child inside node 45 (the suggestion bar). Specifically: NodeList node with nodeId=45 (type=1027) → compChild id=2 (type=1021, size 34×45) is the visible tile image. Updated `ReadInGameSuggestion` to find this node via ULD NodeList scan and extract its icon using `TryFindIcon`, then resolve via `iconMap.Resolve()`.

**Dora indicator discovery:** Added `LogDoraProbe()` diagnostic that scans all non-discard/hand component nodes for mahjong tile icons. Found dora indicators at nodeIds 28-32, type=1006, size 50×60. Up to 5 slots exist (1 base dora + 4 kan dora); only slots with valid tile icons (76041-76077) are revealed. Node 28 (leftmost) is the initial dora; additional slots become visible after kan declarations.

**Changes:**
- `EmjUiReader.cs`
  - `ReadInGameSuggestion()`: signature updated to receive `iconCapture`/`iconMap`; body rewritten to find node 45 in addon's ULD NodeList, locate visible child id=2, extract icon via `TryFindIcon`, resolve tile code via `iconMap.Resolve()`
  - Added 50×60 type=1006 detection in the main node scan loop → produces `SlotKind.DoraIndicator` slots for nodes with valid tile icons
  - Re-indexes dora slots by X position (leftmost = index 0)
  - Added `LogDoraProbe()` diagnostic for instrumentation
- `MahjongGameState.cs` — `DoraIndicators` field (already existed) now populated from the new dora slots

## 2026-04-13: Bug Fix — Call Action Overwrite Race Condition

**What:** Fixed a bug where autoplay would suggest "pass" on a call prompt but then take the chi anyway.

**Root cause (corrected after log analysis):** The in-game suggestion text did NOT flip — it consistently said "Pass" throughout. The actual sequence was:
1. AutoPlay correctly scheduled `call:pass` and dispatched a Skip button ListItemClick
2. The Skip click had **zero effect** (pre/post AtkSnapshots identical) — likely a timing race with the game's internal event processing
3. The game's own call timer expired and auto-accepted Chi (the first available call)
4. Game entered `CallChoicePrompt` (atk0=25, chi sub-menu)
5. `TryScheduleCallChoice()` blindly auto-selected a chi combination, completing the unwanted call

The real bug was #5: `TryScheduleCallChoice()` ran on any `CallChoicePrompt` entry, even when the autoplay had intended to pass. It had no way to distinguish "we deliberately accepted this call" from "the game timer auto-accepted against our intent."

Initial misdiagnosis (suggestion text flipping between frames) was reverted.

**Fix:** Added `_lastCallIntentWasAccept` flag. Set to `true` only when `call:accept` executes. `TryScheduleCallChoice()` now only auto-selects a chi option if this flag is true. When the game timer auto-accepts a call we didn't want, the flag stays `false` and the chi choice sub-menu is left alone (user/game can handle it).

**Changes:**
- `AutoPlayManager.cs`
  - Added `_lastCallIntentWasAccept` field, reset on entering new call phase
  - Set to `true` in `Update()` when executing `call:accept`
  - `TryScheduleCallChoice()` gated on `_lastCallIntentWasAccept`
  - Reverted the incorrect `_pendingAction.StartsWith("call:")` overwrite guard from the earlier misdiagnosis

## 2026-04-13: Bug Fix — ListItemClick Index Wrong for Skip (Root Cause)

**What:** Discovered the deeper root cause: `TryAcceptCallViaListClick` was sending the wrong ListItemClick param for the Skip button, causing it to accept calls instead of skipping.

**Root cause:** `FindButtonIndexInList` computed the button's index by iterating ALL visible component children in the parent list's ULD NodeList. It found Skip at index 1 of 7 visible components. But the game's ListItemClick handler uses its own item numbering — index 1 corresponds to the first call option (Chi/Pon), not Skip.

Evidence from logs:
- Every "Skip" click caused an immediate phase transition (~70-145ms) to WaitingForDiscard or CallChoicePrompt
- A true skip would return to OpponentTurn (opponents continue playing)
- CallDecisionPrompt → WaitingForDiscard after "Skip" on Pon prompts = Pon was accepted (post-pon discard needed)
- CallDecisionPrompt → CallChoicePrompt after "Skip" on Chi prompts = Chi was accepted (chi sub-menu shown)
- **Our "Skip" button click has been accepting calls all along** — Pon accepts were invisible because the phase (WaitingForDiscard) looks the same as a skip→draw sequence

**Fix:** Replaced `FindButtonIndexInList` with `ReadButtonEventParam` — reads the button's own registered AtkEvent Param value. FFXIV list item buttons store their true list index as the Param on their events (MouseOver, ButtonClick, etc.). Using the button's own Param ensures the ListItemClick dispatch targets the correct item.

**Changes:**
- `AddonClickHelper.cs`
  - Added `ReadButtonEventParam()`: reads button node's AtkEventManager events, prefers ButtonClick Param, logs all events for diagnostics
  - `TryAcceptCallViaListClick` now uses `ReadButtonEventParam` instead of `FindButtonIndexInList`
  - `FindButtonIndexInList` kept for reference with a deprecation warning

## 2026-04-13: Bug Fix — Skip Uses Callback 8, Accept Uses Index 0, Retry Limits

**What:** Fixed three compounding bugs: (1) Skip via ListItemClick never worked, (2) accept used wrong index, (3) infinite retry on stuck call prompts.

**Discovery:** After deploying ReadButtonEventParam fix, Riichi prompt caused infinite Skip spam (~2 clicks/second for minutes with no effect). Investigation revealed:
- ALL call list buttons have `param=1` on their registered events — it's a constant, not a unique item index
- `ReadButtonEventParam` was therefore as wrong as `FindButtonIndexInList` — both produced the same value
- ListItemClick doesn't work for Skip AT ALL because Skip is not a list item in the game's UI. Dispatching ListItemClick for "Skip" always executes a call option instead.
- The correct skip mechanism is `FireCallback [8, 0]` (confirmed in `callbackNotes.txt`)
- For accepting calls, ListItemClick(0) selects the first call option (Chi/Pon/Kan/Ron/Riichi) — index 0, not the button's event param

**Fixes:**
1. **Skip/pass**: Removed ListItemClick path entirely for skip. Now always uses `TrySkipCall()` (callback [8, 0])
2. **Accept**: Hardcoded `buttonIndex = 0` in `TryAcceptCallViaListClick` instead of reading from button events
3. **Retry limit**: Added `_consecutiveCallAttempts` counter (resets on phase change, caps at 5) — prevents infinite spam when a call click has no effect

**Changes:**
- `AddonClickHelper.cs` — `TryAcceptCallViaListClick` uses fixed index=0
- `AutoPlayManager.cs`
  - `ExecuteCallResponse` skip path uses callback 8 directly (no ListItemClick attempt)
  - Added `_consecutiveCallAttempts` with 5-attempt limit, reset on phase change
  - Both accept and pass paths increment counter and log attempt number

## 2026-04-13: Performance Fix — Stale addon pointer + per-frame string allocations

**What:** Fixed framerate drop that occurred when NOT in the mahjong game.

**Root cause:** Two issues compounding:
1. `_lastAddonAddress` was set in `OnMahjongDraw` but **never cleared** when the EmjL addon was destroyed. After leaving mahjong, `_autoPlayManager.Update()` continued running every frame with a stale/freed `AtkUnitBase*` pointer — unsafe memory access on garbage data plus wasted CPU.
2. `BuildDiagnosticsText()` and `BuildRecentTransitionsText()` allocated `StringBuilder` + strings every frame via `OnFrameworkUpdate`, even when the debug MainWindow was closed.

**Fixes:**
1. Registered `AddonEvent.PreFinalize` listener for EmjL (`OnMahjongFinalize`) — sets `_lastAddonAddress = 0` and calls `_autoPlayManager.ClearPending()` when addon is destroyed.
2. Wrapped diagnostics/transitions/server-status string builds in `if (MainWindow.IsOpen)` guard.

**Changes:**
- `Plugin.cs`
  - Added `OnMahjongFinalize` handler (clears `_lastAddonAddress`, calls `ClearPending`)
  - Registered PreFinalize listener in constructor, unregistered in Dispose
  - Diagnostics string builds now gated on `MainWindow.IsOpen`

## 2026-04-13: Performance Fix — Throttle IconIdCapture disk writes + reduce per-frame allocations

**What:** Fixed the remaining framerate drop that persisted after the stale-pointer fix.

**Root cause:** `IconIdCapture` hooks `AtkImageNode.LoadIconTexture` which fires for **every** icon load in the entire game (hotbars, inventory, status effects, etc.). Each invocation serialized the full `_iconMap` dictionary (~200KB JSON) to disk via `SaveCache()`. Outside of mahjong, FFXIV loads icons constantly, causing continuous disk I/O + JSON serialization.

Additionally, `OnFrameworkUpdate` was building status/diagnostics strings every frame even when the debug MainWindow was closed — unnecessary GC pressure from `ToString()`, string interpolation, and `StringBuilder` allocations.

**Fixes:**
1. **IconIdCapture**: Replaced per-invocation `SaveCache()` with a dirty-flag + 5-second throttle. `MarkDirty()` sets a flag on each capture; `FlushIfNeeded()` (called once per frame from `OnFrameworkUpdate`) only writes to disk if dirty AND 5+ seconds since last save.
2. **OnFrameworkUpdate**: Gated `readerStatus.ToString()`, auto-play status string building, and `_serverClient.GetStatusText()` behind `MainWindow.IsOpen` check.

**Changes:**
- `IconIdCapture.cs`
  - Added `_cacheDirty` flag and `_lastSaveUtc` timer
  - `OnLoadIconTexture` now calls `MarkDirty()` instead of `SaveCache()`
  - Added `FlushIfNeeded()` public method with 5s throttle
  - `ResetCapturedIcons` and `Dispose` use `SaveCacheNow()` for immediate write
- `Plugin.cs`
  - Added `_iconCapture.FlushIfNeeded()` call in `OnFrameworkUpdate`
  - `readerStatus.ToString()` and auto-play status strings gated on `MainWindow.IsOpen`

## 2026-04-13: Bug Fix — Riichi button not found due to stale button text labels

**What:** Fixed intermittent failure where riichi was not being called even though the suggestion said to accept.

**Root cause:** When a riichi prompt appears, the phase transitions to `RiichiDecisionPrompt` (based on `AtkValues[6]` text) before the button component text nodes update. The button scan (`ScanComponentForCalls`) finds stale text labels from a previous call prompt — e.g. "Chi" and "Skip" from a prior chi prompt — instead of "Riichi". Since `ExecuteCallResponse` requires an exact `CallOptions.Riichi` match in the phase-specific priority list, it fails with `"no matching button for phase=RiichiDecisionPrompt in captured nodes [Chi,Skip]"`.

Confirmed by autoplay logs showing repeated failures: the button scan consistently found `[Chi,Skip]` during `RiichiDecisionPrompt` prompts. The "Riichi" text node existed deeper in the DOM (visible in deep_text_scan.log) but the stale Chi button text at the same tree position was being matched first.

**Fix:** Added a fallback path in `ExecuteCallResponse`: when in `RiichiDecisionPrompt`, `TsumoDecisionPrompt`, or `RonDecisionPrompt` and no phase-matching button label is found, click the first non-Skip button via `ListItemClick(0)`. In these self-action prompts, the first button is always the accept option regardless of stale label text.

**Changes:**
- `AutoPlayManager.cs`
  - Added fallback after priority-based button search for Riichi/Tsumo/Ron phases
  - Fallback clicks the first non-Skip captured button node as a proxy for accept
  - Logs indicate fallback usage for diagnostics

## 2026-04-13: Bug Fix — Tsumo/Ron not detected because AtkValues[6] empty for win prompts

**What:** Fixed tsumo (and ron) prompts being missed by autoplay even when buttons were visible.

**Root cause:** Two compounding issues:
1. **Phase detection**: `AtkValues[6]` doesn't populate "Tsumo" or "Ron" text for win prompts — it stays empty/stale. The only signal is `atk0=6` (call prompt) with visible Tsumo/Ron buttons. `InferGamePhase` mapped `atk0=6` generically to `CallDecisionPrompt`, never reaching `TsumoDecisionPrompt` or `RonDecisionPrompt`.
2. **Provider returns null**: `InGameSuggestionProvider.GetCallAction()` returns `null` when suggestion type is `None`. Even if the phase were correct, `TryScheduleCallResponse` would bail on `decision == null`. After 3 seconds the fallback auto-pass fires, skipping the win.

Confirmed by heartbeat logs: `phase=CallDecisionPrompt calls=Tsumo,Skip sug=None: pending=` — Tsumo button visible, phase misidentified, no action scheduled, eventually auto-passed.

**Fixes:**
1. **InferGamePhase**: When `atk0=6` and suggestion is None, check `availableCalls` flags: Tsumo → `TsumoDecisionPrompt`, Ron → `RonDecisionPrompt`, Riichi → `RiichiDecisionPrompt`, else `CallDecisionPrompt`.
2. **TryScheduleCallResponse**: Auto-accept for `TsumoDecisionPrompt` and `RonDecisionPrompt` when provider returns null — you never decline a winning hand.

**Changes:**
- `EmjUiReader.cs` — `InferGamePhase`: atk0=6 fallback now checks call buttons for Tsumo/Ron/Riichi
- `AutoPlayManager.cs` — `TryScheduleCallResponse`: force `decision = "accept"` for Tsumo/Ron when provider is null
