# Telesto HTTP telegrams for AZPC `/mj` testing

Cursor cloud agents (and humans) can ask a running FFXIV + Dalamud client to run Mahjong Helper slash commands through [Telesto](https://github.com/paissaheavyindustries/Telesto). This file is the in-repo recipe. Full telegram types live on the [Telesto wiki](https://github.com/paissaheavyindustries/Telesto/wiki/Telesto-documentation).

Do **not** invent other HTTP endpoints. For Helper testing, only `ExecuteCommand` is used.

## URL

POST JSON to either:

- `http://localhost:45678/`
- `http://127.0.0.1:45678/`

Prefer **`http://localhost:45678/`**. Telesto’s HTTP.sys ACL is bound to the `localhost` host name. A request to `127.0.0.1` without the matching host header fails with **Invalid Hostname**.

If you must use `127.0.0.1`, set:

```http
Host: localhost
```

## Required JSON shape

Every telegram needs `version`, **`id`**, `type`, and `payload`:

```json
{
  "version": 1,
  "id": 1,
  "type": "ExecuteCommand",
  "payload": {
    "command": "/mj snap"
  }
}
```

`id` is a number (Telesto’s telegram identifier). It is **required**. Omitting it produces this in-game error:

```text
Exception in telegram processing: The given key 'id' was not present in the dictionary.
```

The wiki form is the same: `{ "version": 1, "id": x, "type": y, "payload": z }`.

## Commands used here

Same `ExecuteCommand` shape; only `payload.command` changes.

| Command | What Helper does |
| --- | --- |
| `/mj snap` | JSON-only. Writes `%APPDATA%/MahjongHelper/captures/snap-*.json` plus `%APPDATA%/MahjongHelper/solver_snap.json`. Does **not** take a PNG. |
| `/mj screenshot` | Writes a PNG via CaptureFallback (or `/mj screenshot game` for the Square writer). Telesto only runs the slash command; it does not press PrintScreen itself. |

Example screenshot telegram:

```json
{
  "version": 1,
  "id": 2,
  "type": "ExecuteCommand",
  "payload": {
    "command": "/mj screenshot"
  }
}
```

## Script

`scripts/mj-snap.ps1` already POSTs to `http://localhost:45678/` with the required body (`version`, `id`, `type`, `payload.command = /mj snap`). Prefer that script over a hand-rolled POST.

If Telesto is down, the script falls back to writing `%APPDATA%/MahjongHelper/captures/request_snap` for the plugin file-watch.
