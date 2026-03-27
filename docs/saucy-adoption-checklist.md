# Saucy-Inspired Adoption Plan for MahjongHelper

This is the practical "what to borrow next" version of the Saucy research, tuned for MahjongHelper implementation order.

## Objective

Adopt Saucy's highest-value patterns for stable state reading with minimal patch pain:

1. Modular readers per UI/addon source.
2. Lifecycle-driven state updates (`shown/update/lost`).
3. Fast-path struct/offset reads where stable, plus fallback node traversal.
4. Built-in diagnostics and failure states instead of silent failures.

## Implementation Checklist (High ROI First)

## Phase A: Reader Architecture (Do First)

- [x] Create a reader interface similar to Saucy's `IUIReader`:
  - `GetAddonName()`
  - `OnAddonShown(IntPtr addonPtr)`
  - `OnAddonUpdate(IntPtr addonPtr)`
  - `OnAddonLost()`
- [x] Add a scheduler/manager that:
  - polls addon pointer + visibility,
  - dispatches lifecycle callbacks,
  - tracks active/inactive state transitions.
- [x] Add explicit reader status enums (`NoErrors`, `AddonNotFound`, `NodesNotReady`, etc.).

Why this first:
- Gives one stable control plane for all future extraction work.
- Makes debugging and patch recovery much faster.

## Phase B: Dual-Path Data Acquisition (Fast + Resilient)

- [x] Keep current probe path (memory/slot probe data).
- [x] Add a node-traversal path for fallback text/visibility signals.
- [x] Normalize both into one `MahjongGameState` model with per-field provenance:
  - `Source = Probe | Node | Cached`
  - confidence flags (`IsAuthoritative`, `IsFallback`).
- [x] Add merge policy:
  - prefer probe for stable numeric fields,
  - prefer node text for human-readable context,
  - retain last-known-good value if both paths fail.

Why this second:
- Prevents total breakage when one path regresses.

## Phase C: Patch-Survivability and Observability

- [x] Add a small diagnostics panel:
  - reader status,
  - active source path,
  - last successful update timestamp,
  - last N failure reasons.
- [x] Emit structured logs on state transitions only (already aligned with your passive logging direction).
- [x] Add quick export buttons for:
  - current normalized state,
  - recent transition history,
  - probe snippets.

Why this third:
- Reduces troubleshooting round-trips and guesswork after game patches.

## Minimal-Effort, High-Return Debugging Help You Can Provide

If you want to help debugging with least manual effort and best payoff, do these in order:

1. Passive play session captures (best ROI)
   - Play 10-15 minutes normally with plugin enabled.
   - No manual comparison needed.
   - Deliverables: latest `%APPDATA%/MahjongHelper/probe_history.log` and `%APPDATA%/MahjongHelper/state_comparison.log`.

2. Lightweight event tagging at key moments
   - Only click annotation buttons at major events:
     - tile draw,
     - tile discard,
     - call prompt appears,
     - round end/start.
   - No per-turn micromanagement.
   - High value: drastically improves signal correlation during analysis.

3. One "broken case" snapshot package
   - When suggestion/output looks wrong, capture once:
     - copy Mahjong UI state panel,
     - copy client probes,
     - timestamp + one-line note of what looked wrong.
   - High value: gives exact repro anchor.

4. Post-patch smoke check (2-3 minutes)
   - After FFXIV patch/plugin update, do one quick check:
     - open Mahjong UI,
     - confirm reader status,
     - export one probe sample.
   - High value: early detection of broken offsets/signatures.

## What Not Worth Your Time (Low ROI)

- Manually comparing long dumps line-by-line.
- Repeating the same capture many times without event tags.
- Hover-heavy/manual mapping loops unless specifically requested for a focused hypothesis.

## Suggested "Help Me Debug" Routine (Minimal Burden)

Use this default routine unless we ask for something special:

1. Start plugin.
2. Play normally for one short session.
3. Click event-tag buttons only at obvious moments.
4. Export/copy logs once at end.
5. Send one-line summary: "looked correct" or "wrong at <time/event>".

This gives the best return for your effort and keeps reverse-engineering mostly passive.
