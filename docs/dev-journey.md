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
