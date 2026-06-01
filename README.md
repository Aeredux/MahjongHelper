# Mahjong Helper

Mahjong Helper is a Dalamud plugin for FFXIV Doman Mahjong (EmjL).

It reads live Mahjong UI state, shows recommendations, can query a local Mahjong solver server, and can optionally execute discard/call actions automatically with configurable delays.
Can be used with https://github.com/Aeredux/MahjongSolver

## What It Currently Does

- Reads the EmjL addon state continuously (hand, drawn tile, discards, dora indicators, winds, scores, call prompts, turn/phase).
- Merges probe and node data into a normalized game state model used by UI, logging, and automation.
- Shows a suggestion overlay in-game:
  - compact mode (single best discard)
  - full mode (ranked suggestions, shanten, ukeire, confidence, reasoning)
  - server health status and call recommendation text
- Supports two suggestion providers for auto-play:
  - In-Game provider (uses in-game suggestion signals)
  - Server provider (uses local HTTP API responses)
- Talks to a local Mahjong server at `http://localhost:8080`:
  - `GET /api/health`
  - `POST /api/suggest-move`
  - `POST /api/evaluate-call`
  - `POST /api/validate-move`
- Optional auto-play behavior:
  - auto-discard when it is your discard phase
  - auto call accept/pass decisions during call prompts
  - pause/resume and randomized action delays
  - safety fallbacks for stuck phases (for example, delayed tsumogiri fallback)
- Provides a debug window with live diagnostics, normalized state, mapping/status text, transition history, and export/copy helpers.

## Slash Commands

Main command: `/mj`

- `/mj` toggles the debug window.
- `/mj overlay` toggles the suggestion overlay.
- `/mj compact` toggles compact/full overlay mode.
- `/mj auto` toggles auto-play.
- `/mj pause` pauses/resumes pending auto-play actions.
- `/mj mark discard` records a manual discard marker in diagnostics.
- `/mj mark call` records a manual call marker in diagnostics.

Advanced debug/probing commands are also available (for callback and click-path reverse engineering), including `probecallback`, `firecb`, `callsweep`, `acceptcall`, `clickcall`, and `clicktile`.

## Settings

The settings window currently includes:

- strategy provider selection (In-Game or Server)
- auto-play enable/disable
- auto-discard toggle
- auto-call toggle
- min/max delay controls for discard and call actions

## Prerequisites

- XIVLauncher, FFXIV, and Dalamud installed and working.
- .NET 8 SDK.
- Optional but recommended for server mode: local Mahjong solver server on `localhost:8080` implementing the endpoints above.

## Build

Use the solution build command (Release | x64):

```powershell
dotnet build MahjongHelper.sln -c Release
```

Expected plugin output:

`MahjongHelper/bin/x64/Release/MahjongHelper.dll`

Do not build the csproj with a runtime flag like `-r win-x64` for normal plugin deployment, because that writes to a different output path.

## Activating In-Game (Dev Plugin)

1. Open Dalamud settings (`/xlsettings`) and add the full path to `MahjongHelper.dll` under Dev Plugin Locations.
2. Open plugin installer (`/xlplugins`), go to Dev Tools -> Installed Dev Plugins, and enable Mahjong Helper.
3. Use `/mj` in chat to open/toggle the debug window.

## Logging And Diagnostics

Mahjong Helper writes debug artifacts to:

`%APPDATA%/MahjongHelper/`

Common files include:

- `server_log.txt` (HTTP request/response logs)
- `autoplay.log` (auto-play action traces)
- `mahjong_ui_state_history.log` (deduped UI state history)
- `normalized_state_history.log` (deduped normalized state history)
- `probe_history.log`, `probe_signals.log`, `tile_candidates.log` (reverse-engineering logs)
- `mapping_progress_report.txt` (mapping/report export)

## Notes

- This plugin automates in-game decisions and click actions when auto-play is enabled.
- Server suggestions are only used when server mode is selected and the local server is reachable.
- If the overlay is enabled but EmjL is not open, the overlay will remain hidden until a readable Mahjong state is available.
