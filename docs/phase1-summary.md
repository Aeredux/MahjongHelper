# Phase 1: Tile Identity Discovery — Current Status

## Outcome
Phase 1 is no longer in pure discovery mode. The important unknowns have been narrowed down enough that there is now a working implementation path:

- Tile identity is **not** reliably readable from the original `1045` hand nodes.
- Tile identity is **not** available through the normal `UldAsset -> AtkTextureResource -> IconID` path for the live hand tiles.
- The real visible player hand is represented by **`type=1055` nodes** sized **`42x55`** at `NodeList[54..71]`.
- The tile face icon inside those nodes is nested inside child components, so **recursive traversal** is required.
- The tile icon ID must be captured **at load time** by hooking `AtkImageNode.LoadIconTexture`.

That means the current path forward is:

1. Capture icon IDs live.
2. Read the visible `1055` hand nodes.
3. Resolve icon IDs to Mahjong tile codes.
4. Feed the resulting hand to the solver.

## What We Know Now

### Addon / UI structure

- `EmjL` is the active Mahjong game addon.
- The originally inspected `1045` nodes at `NodeList[256..268]` are **not** the stable source of truth for the live player hand.
- The drawn tile still appears on `type=1022` nodes.
- The real player hand is on visible `type=1055` nodes at `NodeList[54..71]`, each `42x55` in size.

### Texture / icon behavior

- For the live hand tiles, the static texture chain is a dead end:
	- `TextureType = 0`
	- `Resource = NULL`
- So the repo pattern from `DomanMahjongStatus` works for round indicators and similar static images, but **not** for hand tile faces.
- The correct approach is to hook `AtkImageNode.LoadIconTexture` and record the icon ID when the game applies it.

### Confirmed working live capture

- Live icon capture is working and sees Mahjong tile icon IDs in the `76041-76077` range.
- The hook also captures Dora / round UI icons, which helped validate that the hook path is correct.

### Hover-derived mapping

- `AtkValues[1]` = hovered tile name.
- `AtkValues[2]` = hovered tile icon ID.
- This is only available for the currently hovered tile, but it is enough to build a learned `iconId -> tileCode` map over time.

Known confirmed examples:

- `76069 -> P4`
- `76071 -> S9`

## Implemented In Code

### Live icon capture

Implemented in:

- `SamplePlugin/Mahjong/IconIdCapture.cs`

What it does:

- Hooks `AtkImageNode.LoadIconTexture`.
- Captures `image-node-address -> iconId` at runtime.
- Stores recent capture history for diagnostics.
- Persists the captured map across plugin reloads in `%APPDATA%/MahjongHelper/icon_capture_cache.json`.

### Learned icon-name mapping

Implemented in:

- `SamplePlugin/Mahjong/MahjongIconMap.cs`

What it does:

- Observes hover tooltip data from `AtkValues`.
- Converts names like `Bamboo (9)` into solver-style tile codes like `S9`.
- Persists learned `iconId -> tileCode` mappings in `%APPDATA%/MahjongHelper/icon_name_cache.json`.

### Hand extraction

Implemented in:

- `SamplePlugin/Mahjong/MahjongHandReader.cs`

What it does:

- Reads visible `type=1055` hand nodes.
- Recursively traverses nested child/component trees to find the icon-bearing `AtkImageNode`.
- Resolves `iconId` from the live capture cache.
- Sorts hand tiles by on-screen `X` position.
- Produces a `MahjongHandSnapshot` with hand tiles and drawn tile.

### Plugin integration

Integrated in:

- `SamplePlugin/Plugin.cs`

What it does:

- Learns hover mappings continuously during `EmjL` `PreDraw`.
- Auto-refreshes every 3 seconds.
- Writes diagnostic dump output.
- Shows the resolved hand snapshot in the secondary plugin UI text area.

### Diagnostics / dump output

Updated in:

- `SamplePlugin/Mahjong/TileDataDumper.cs`

Notable sections now include:

- captured icon IDs,
- resolved hand snapshot,
- learned icon map,
- raw `1055` node capture info.

## Dead Ends That Are Now Settled

These are no longer promising directions unless a future finding contradicts them:

### 1. `1045` node-only reading

The original `1045` nodes do not provide a stable, complete hand identity path.

### 2. Static texture resource chain

For hand tile faces:

- `TextureType != Resource`
- `Resource == NULL`

So `AtkTextureResource.IconID` is not directly available there.

### 3. Raw byte / pointer fingerprinting as the primary solution

This helped narrow the problem but is not the right production path.

### 4. Deep recursive blind pointer scans

These caused `AccessViolationException` crashes and were removed.

## Current Limitation

The hand reader path is implemented, but tile-code resolution is still only partially automatic.

What is solved:

- We can capture live icon IDs.
- We know which nodes represent the actual hand.
- We can persist capture state across rebuilds.

What is not fully solved yet:

- The `iconId -> tileCode` map is only as complete as what has been learned from hover data so far.
- Unknown tiles still appear as `ICON_<id>` until learned or hard-mapped.
- The current resolved snapshot may temporarily be empty immediately after a rebuild if no prior cache exists or if no relevant icon loads have happened since restart.

## Best Next Steps

### 1. Finish icon ID to tile code mapping

Build a complete hard mapping for Mahjong tile icon IDs in the `76041-76077` range.

This is the highest-value remaining task in Phase 1 because it removes dependence on hover learning.

### 2. Persist last resolved hand snapshot as a fallback

If the live icon cache is temporarily cold right after reload, use the last resolved hand until fresh captures arrive.

### 3. Promote the current hand reader into a production `GameStateReader`

Once icon mapping is complete, the current `MahjongHandReader` can become the tile-reading core of Phase 2.

### 4. Start solver payload generation

Once the reader emits stable solver tile codes (`M1`-`M9`, `P1`-`P9`, `S1`-`S9`, honors), it can be wired into the localhost Mahjong solver.

## Clean Handoff Summary

If continuing in another window, the important facts are:

1. Ignore the old assumption that the `1045` nodes are the real hand source.
2. The real player hand is on visible `type=1055`, `42x55` nodes at `NodeList[54..71]`.
3. The icon image is nested inside child components; recursive traversal is required.
4. The hand tile icon ID is not statically readable from `AtkTextureResource`.
5. The working solution is live icon capture via `AtkImageNode.LoadIconTexture`.
6. Current runtime components are already implemented:
	 - `IconIdCapture`
	 - `MahjongIconMap`
	 - `MahjongHandReader`
7. Remaining Phase 1 work is primarily completing the iconId-to-tile mapping and stabilizing fallback behavior.
# Phase 1: Tile Identity Discovery — Summary

## What We Know

- **EmjL addon** has 330 nodes. Hand tiles = type 1045 components at indices 256-268 (13 hand tiles). Drawn tile = type 1022 at index 107.
- Each tile component has 2 child image nodes: id=3 (tile face, flags=0x80 icon mode) and id=2 (tile back).
- **Confirmed icon ID mappings** from hover data: P4=76069, S9=76071. Icon IDs live in the 76050-76150 range.
- **AtkValues[1]** = hovered tile name (e.g. "Bamboo (9)"), **AtkValues[2]** = hovered tile icon ID — but only for whichever single tile is currently hovered.

## Approaches Tried (All Dead Ends)

### 1. PartId / UV / Asset
Identical for ALL hand tiles (partId=18, UV(97,0,34,45), Asset id=21). Does not distinguish tiles.

### 2. UldAsset → AtkTexture → Resource → FileName
Resource pointer is NULL for all hand tile icon images. The chain is broken — can't reach the FileName.

### 3. AtkImageNode+0xC0 Pointer
The pointer value at offset +0xC0 inside each AtkImageNode **varies per tile** — each tile's face image node has a different pointer value here. However, the target data (what the pointer points to) contains no recognizable "ui/icon" paths in the first 0x40 bytes. We haven't fully analyzed whether tiles of the same type share the same pointer value.

### 4. Deep Recursive Pointer Scan (FindIconPathDeep)
Followed pointers up to 3 levels deep from the +0xC0 target, scanning for "ui/icon" strings. Never found any icon paths. Caused 2 fatal crashes (AccessViolationException) due to TOCTOU race conditions — memory freed between IsReadable() check and Marshal.ReadByte(). **Approach removed from code.**

### 5. Icon ID Int32 Scan (76050-76150 range)
Scanned all component data, image node bytes, and pointer targets for int32 values in the known icon ID range. NOT found as a raw int32 anywhere in tile node data.

### 6. AtkValues[16-21]
Contains 6 icon IDs (76058, 76061, 76064, 76072, 76072, 76073) but only 6 values for 13 tiles. Purpose unclear — may relate to non-tile UI elements.

### 7. Addon+0x288 Pointer Array
14 pointers to data structures. No icon IDs found in them.

## Suggested Next Approaches (Ranked)

### 1. Research Existing Plugins (Highest Priority)
Search GitHub for FFXIV Doman Mahjong plugins/tools. Keywords: "ffxiv mahjong dalamud", "doman mahjong plugin", "EmjL addon". Someone has likely solved this already.

### 2. Game Agent / State Manager
FFXIVClientStructs likely has an AgentMahjong or similar that holds the actual game state (tile arrays, hand, discards) outside the UI layer. The UI nodes just render what the agent tells them. This is how most Dalamud addons read game data.

### 3. Pointer Fingerprinting via AtkImageNode+0xC0
The +0xC0 pointer varies per tile. If tiles of the same type share the same pointer (or pointer target), we could group tiles by identity. Combined with the hover-based AtkValues lookup (which gives icon ID for one tile at a time), we could build a complete mapping. See discussion below.

### 4. Lumina Icon → Tile Mapping
We know icon 76069=P4 and 76071=S9. Build the full icon ID → tile name mapping from game data sheets via Lumina. Then we only need to find ONE place that stores the icon ID per tile.

### 5. AtkComponentBase Callback / Event Data
Component type 1045/1022 nodes may have event handlers or callback data encoding tile identity. Haven't explored vtables or event dispatch.

### 6. Network Packet Hooking
Hook the network receive callback on EmjL to get raw tile data from the server.

## Discussion: Pointer Fingerprinting (#3)

The +0xC0 pointer in each tile's AtkImageNode is the most promising lead we haven't fully exploited. Here's the idea:

- Each tile's face image has a **unique pointer at +0xC0** (the loaded icon texture resource).
- If two tiles showing the same tile type (e.g., two "1 of Bamboo") reference the **same texture object** (same pointer value), then matching pointers = same tile type.
- We could build a mapping dynamically: hover over each tile type once to learn its icon ID from AtkValues[2], associate that with whatever +0xC0 pointer value it has, and from then on identify all tiles by pointer comparison.
- Even without hovering, if the pointer target bytes form a unique "fingerprint" per tile type, we could compare the first N bytes of the target data to group tiles.

**Open questions:**
- Do two tiles of the same type actually share the same +0xC0 pointer value? Or does each tile get its own unique texture instance?
- If pointer values differ per tile even for same type, do the first N bytes of the target data still match?
- Is the +0xC0 pointer stable across frames, or does it change every draw?

These can be answered with a simple dump that compares +0xC0 values across all 13 hand tiles, without any dangerous deep pointer following.
