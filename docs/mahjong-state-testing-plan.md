# Mahjong State Discovery Testing Plan

This plan is the operational checklist for collecting all remaining Mahjong state information with minimal user effort and high diagnostic value.

## Goal

Produce a stable, test-backed state reader that can reliably provide:

1. Player hand tile identities in order.
2. Drawn tile identity.
3. Key flow state signals (at minimum `AgentId.Emj+0x28/+0x08` transitions).
4. Reproducible transition logs and failure evidence when state is wrong.

## Current Instrumentation Available

The following are already implemented and should be used in this plan:

- Realtime + periodic `EmjL` UI reads.
- Probe extraction and probe history logging.
- Normalized merged state (`Probe` + `Node` + `Cached`) with provenance.
- Diagnostics panel with recent failures and source path.
- Transition-only normalized-state history logging.
- One-click export/copy actions in plugin UI.

## Required Artifacts

For each test session, collect these files from `%APPDATA%/MahjongHelper`:

1. `probe_history.log`
2. `mahjong_ui_state_history.log`
3. `normalized_state_history.log`
4. `state_comparison.log`
5. `probe_signals.log`

Optional when relevant:

6. `tile_candidates.log`
7. `normalized_state_export.txt`
8. `probe_snippet_export.txt`

## Test Execution Order

Run tests in this order. Do not skip ahead; each stage validates assumptions needed by the next stage.

## Stage 0: Environment Sanity (2-3 minutes)

Steps:

1. Open Mahjong UI (`EmjL`) in-game.
2. Verify `Reader Status` in plugin UI becomes `NoErrors`.
3. Confirm Diagnostics section updates timestamps for node and merge reads.
4. Click `Export Probe Snippet` once.

Pass criteria:

- Reader status reaches `NoErrors`.
- No repeated hard failures in Recent Failures.
- Probe snippet export file is created.

If fail:

- Stop and capture artifacts immediately.
- Note exact failure text from Diagnostics.

## Stage 1: Passive Baseline Session (10-15 minutes)

Purpose:

- Gather natural transitions with minimal user interaction.

Steps:

1. Play normally for 10-15 minutes.
2. No manual hover loops.
3. Only use annotation buttons at obvious events:
   - `Log: Tile Drawn`
   - `Log: Tile Discarded`

Pass criteria:

- `normalized_state_history.log` shows multiple transition entries.
- `probe_signals.log` shows meaningful `AgentId.Emj+0x28/+0x08` changes.
- Diagnostics show recent successful updates and no sustained error storm.

## Stage 2: Event Correlation Spot Checks (5-10 minutes)

Purpose:

- Confirm that major gameplay events map to expected state transitions.

Steps:

1. During active play, intentionally mark 3-5 draw events and 3-5 discard events.
2. After session, compare timestamps between:
   - annotation entries in `state_comparison.log`,
   - state changes in `normalized_state_history.log`,
   - signal changes in `probe_signals.log`.

Pass criteria:

- Draw/discard annotations align with nearby normalized/probe transitions.
- No long windows where gameplay changes but all sources stay static.

## Stage 3: Fallback Behavior Validation (5 minutes)

Purpose:

- Ensure cached fallback behaves as expected when one source is temporarily unavailable.

Steps:

1. Keep session running until at least one normalized state is present.
2. Trigger a temporary read disruption (e.g., UI transition, temporary hidden state).
3. Observe Normalized State and Diagnostics.

Pass criteria:

- Normalized state does not collapse to empty immediately.
- Field metadata shows `Cached` fallback where expected.
- Recovery returns to `Probe`/`Node` sources automatically.

## Stage 4: Cold Start / Reload Validation (3-5 minutes)

Purpose:

- Verify startup reliability and quick recovery.

Steps:

1. Reload plugin while Mahjong UI is already open.
2. Confirm startup dump path runs.
3. Confirm normalized state appears without long delay.

Pass criteria:

- Reader returns to `NoErrors` quickly.
- Startup entries appear in logs.
- No repeated startup failure in diagnostics.

## Stage 5: Post-Patch Smoke Procedure (2-3 minutes)

Run this after any game patch/plugin API change.

Steps:

1. Open Mahjong UI.
2. Check `Reader Status`.
3. Export probe snippet and normalized state.
4. Do 1-2 quick draw/discard actions and verify transitions.

Pass criteria:

- Basic transitions still emit.
- Exports still work.
- No catastrophic status/failure patterns.

## Failure Triage Rules

Use this decision tree to reduce wasted debugging effort.

1. `Reader Status != NoErrors` most of session
   - Treat as lifecycle/addon detection issue first.

2. Reader status OK, but normalized transitions missing
   - Treat as merge/signature/transition-logging issue.

3. Probe updates present, node updates missing
   - Treat as `EmjUiReader`/node traversal issue.

4. Node updates present, probe static/missing
   - Treat as probe extraction/parsing issue.

5. Frequent fallback to `Cached` without recovery
   - Treat as source reliability regression.

## Minimal-Effort Session Template (Default)

Use this unless we request deep investigation:

1. Play one 10-15 minute normal session.
2. Tag only obvious draw/discard events.
3. Export normalized state + probe snippet once at end.
4. Share logs and one-line summary:
   - `correct` or
   - `wrong around <time/event>`.

## Completion Definition

We consider discovery/testing coverage sufficient when all are true:

1. Stage 0-4 pass in one week of normal sessions.
2. No unresolved high-frequency failures in diagnostics.
3. Event correlation is consistently reproducible.
4. Post-patch smoke test succeeds on first attempt or has a clear, isolated failure signature.
