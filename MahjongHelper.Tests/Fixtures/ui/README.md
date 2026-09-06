# UI dump fixtures

Offline goldens for leftover / hand-strip classify. Live captures stay under
`%APPDATA%/MahjongHelper/ui-dumps/` and `captures/` (often 300KB+).

## Re-dump

1. Release-build the plugin, reload, wait ~10s.
2. Sit the board you want.
3. `/mj dump-ui south-seat-west-chi-m4-m6` (or any slug).
4. Copy the file from `%APPDATA%/MahjongHelper/ui-dumps/` into this folder.
5. Trim `allIconNodes` to trays + leftover faces + ponds + own strip needed
   for the assert. Keep `expected.snapLine`. Do not commit a 389KB snap.

Goldens: `south-seat-west-chi-m4-m6`, `west-seat-empty-table`,
`west-seat-north-pon-s2-p1`, `south-seat-east-pon-east-chi-s7-s9`,
`east-seat-own-red-west-pon-s2`.

`/mj snap` still writes `captures/snap-*.json`. A dump-ui file is the same
node fields plus seat/round and an optional expected sidecar.
