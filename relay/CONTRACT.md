# DLL ↔ Relay contract (frozen before Phase 2)

This is the wire format the DLL (`Modules/LobbyShare.cs`) and the Worker
(`relay/src/index.ts`) agree on. Both sides must follow it byte-for-byte
or signatures break.

## Endpoints

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/announce` | new lobby created — or, if same host re-announces existing code, PATCH the embed in place (handles Play-Again / player-count refreshes) |
| `POST` | `/api/start` | game has started — PATCH embed to in-game (amber) |
| `POST` | `/api/end` | game finished — PATCH embed back to "open" (blurple). Lobby is still alive; same code is rejoinable |
| `POST` | `/api/close` | host actually left the lobby — DELETE message + KV entry |
| `POST` | `/api/update` | live update of player count / max / mode (status preserved) — DLL fires throttled (~5s diff-detect) during the lobby phase |
| `POST` | `/api/report` | a player's in-game `/report`, forwarded by the host to the **operator-only** channel (separate webhook). Carries no lobby state — works even in a lobby that was never announced |

All of them require these headers:

```
X-Timestamp: <unix seconds, no fraction>
X-Signature: <hex sha256 HMAC>
content-type: application/json
```

`X-Signature` is computed as:

```
HMAC_SHA256(key = <DLL's baked HMAC key>, message = X-Timestamp + "." + raw_request_body)
```

- The body MUST be the exact bytes sent on the wire (no whitespace re-normalization)
- Each DLL release has **one** HMAC key baked in. The Worker holds a
  comma-separated list (`SHARED_HMAC_KEYS`) of currently-accepted keys; any
  signature that verifies under at least one listed key is accepted. This is
  how rotation stays non-breaking — add new key, ship new DLL, drop old key
  after grace.
- Skew tolerance: ±300 seconds

If signature is invalid or timestamp is skewed, the relay responds `401 bad signature`.

## Bodies

### `POST /api/announce`

```json
{
  "code":       "ABCDEF",
  "region":     "NA",
  "players":    3,
  "max":        15,
  "mode":       "Standard",
  "modVersion": "0.3.0-alpha",
  "hostName":   "WafflePlayer",
  "fcHash":     "<sha256 hex of friend_code + FC_SALT>"
}
```

Field rules:

- `code` — uppercase `[A-Z0-9]{6}`. The DLL passes whatever AU's `GameCode.IntToGameName` returns, uppercased.
- `region` — one of `NA` / `EU` / `AS`. The DLL normalizes:
  - `North America` → `NA`
  - `Europe` → `EU`
  - `Asia` → `AS`
  - (Anything else → don't announce — see fallback rules below.)
- `players` — 1..15. Current lobby occupancy at time of announce.
- `max` — 1..15. `GameOptions.MaxPlayers`.
- `mode` — short string ≤ 32 chars. End K not custom game mode name (`Standard`, `FFA`, `Speedrun`, ...). Empty → relay rewrites to `Standard`.
- `modVersion` — `Main.PluginVersion`. Empty → relay rewrites to `unknown`.
- `hostName` — host's Among Us name. Empty / control-only → relay rewrites to `Anonymous`. The relay strips `@`, control chars, and code-block escapes.
- `fcHash` — `sha256_hex(friend_code + FC_SALT)`. 64 lowercase hex chars.
  - `FC_SALT` is a constant string baked into the DLL. Public. Its only role is preventing rainbow-table lookups of arbitrary friend codes from a KV dump.

### `POST /api/update`

```json
{
  "code":    "ABCDEF",
  "fcHash":  "<same 64 hex>",
  "players": 5,
  "max":     15,
  "mode":    "Standard"
}
```

Field rules:
- `code` + `fcHash` — same validation as other lifecycle endpoints.
- `players`, `max`, `mode` — all optional. Only present fields are updated.
- If no field actually differs from the stored value, the relay returns
  `200 {"status":"no-change"}` and does NOT call Discord.
- `/api/update` doubles as the DLL's **keepalive**: the DLL fires it every
  `KeepaliveSeconds` (600) during the lobby phase even when nothing changed, so the
  sweep can tell a quiet lobby from an abandoned one. On a no-change call the relay
  refreshes `lastSeenAt` only if the entry hasn't been touched for 600s
  (`200 {"status":"touched"}`); otherwise it skips the KV write entirely.
- The embed's status (open vs in-game) is preserved — `/api/update` never
  flips state, it only refreshes data fields.

### `POST /api/report`

```json
{
  "code":         "ABCDEF",
  "fcHash":       "<host's 64 hex>",
  "reporter":     "SomePlayer",
  "reporterHash": "<reporting player's 64 hex>",
  "text":         "the door in Admin never opens",
  "region":       "North America",
  "mode":         "Standard",
  "modVersion":   "0.9.0-dev",
  "phase":        "in-game"
}
```

Field rules:

- `text` — required, 1..400 chars after sanitizing. Empty (or control-chars-only) → `400 empty text`.
  Both sides clamp at 400 (`ReportTextMax` in the DLL, `REPORT_TEXT_MAX` on the relay).
- `code` — **may be empty**, unlike every other endpoint: a report can come from a
  local/freeplay game that has no lobby code. Non-empty values must match `[A-Z0-9]{6}`.
- `fcHash` — the **host** who forwarded the report. `reporterHash` — the **player who
  typed it**. Both are checked against the denylist; a hit on either returns
  `200 {"status":"ignored"}` (silent, so neither side can probe the list).
- `reporter` — the reporting player's Among Us name, ≤ 32 chars. Empty → `Anonymous`.
  Attacker-controlled: the relay strips `@`, control chars, and code-block escapes,
  same as `hostName`.
- `region` — accepts the short code or the vanilla long form. Unrecognized → omitted
  from the embed rather than rejected (a report must not be lost over metadata).
- `phase` — `lobby` / `in-game` / `meeting`.
- Rate limit is **per `reporterHash`** (`REPORT_RATE_LIMIT_SECONDS`, default 120) —
  a lobby full of players can each report, but one player can't spam. The DLL applies
  its own per-player floor first (`ReportCooldownSeconds`, 180s), so the relay limit is
  the backstop. **The DLL's floor must stay longer than this one.** The DLL acks the
  reporter the moment it accepts, so anything it accepts and the relay then 429s is a
  silent loss the player was told went through — and the two clocks are independent, so
  equal values are not enough.
- Nothing here touches `code:` — no read, no write. An accepted report costs **exactly
  one KV write** (the rate-limit key); every rejected one costs zero.
- If `DISCORD_REPORT_WEBHOOK_URL` is unset the relay returns `200 {"status":"disabled"}`
  and drops the report. It never falls back to the announce webhook — reports must
  never land in the public lobby channel.

### `POST /api/start`, `POST /api/end`, and `POST /api/close`

```json
{ "code": "ABCDEF", "fcHash": "<same 64 hex>" }
```

The relay verifies `fcHash` matches the value stored at announce time;
otherwise responds `403 fcHash mismatch`. This is the only ownership check
— sufficient for protecting against accidental cross-mutation, not
sufficient for a determined attacker.

**Lifecycle semantics:**
- `/api/start` flips the embed to amber "🎮 In Game" but the KV entry persists.
- `/api/end` flips the embed BACK to blurple "🎫 Lobby Open". The KV entry
  stays alive — the lobby is still in memory and rejoinable. This is what
  allows Play-Again to look correct in Discord without spamming new messages.
- `/api/close` is the actual terminal: DELETE Discord message, then DELETE KV
  entry. The DLL fires this only when the host destroys the lobby (returns
  to MainMenu). The Discord DELETE is retried (429 honoring Retry-After, 5xx with
  backoff). If it still fails, the KV entry is **kept** (so its `messageId` isn't
  lost) and the relay responds `502` — it never deletes KV on a failed Discord
  delete, because that would orphan the embed permanently.
- Abandoned lobbies that never receive `/api/close` (host process killed by the
  watchdog, official-server ban, crash, or a close that never reached the relay)
  are cleaned up by the **scheduled sweep**: a cron trigger (every 10 min) deletes
  the Discord message for any entry whose `lastSeenAt` is older than
  `STALE_SECONDS` (default 3600; 3× that while `status == "in-game"`, since a game
  in progress writes nothing and the DLL keepalive only runs during the lobby
  phase), then deletes the KV entry. This is the only path that can recover an
  abrupt host death — the client is gone before it can send anything, so the
  server has to reap.
- `ANNOUNCE_TTL_SECONDS` is a **sliding** TTL, refreshed on every write, and acts
  as a floor: the Worker raises it if `STALE_SECONDS` would otherwise outlive it.
  The KV entry must never expire before the sweep gets its chance, because the
  `messageId` dies with it and nothing can delete the embed afterwards. The TTL
  used to decay from `createdAt`, which expired the entry out from under any lobby
  that outlived 3h — after which `/api/close` returned `no-op` and the embed was
  orphaned permanently.

## Responses

All responses are JSON. Status codes:

| status | meaning |
|---|---|
| `200 {"status":"updated"}` | /api/update: one or more fields changed; embed PATCHed |
| `200 {"status":"no-change"}` | /api/update: all submitted fields matched stored values; no PATCH sent, no KV write |
| `200 {"status":"touched"}` | /api/update: nothing changed, but `lastSeenAt` was refreshed (keepalive) |
| `200 {"status":"announced","messageId":"..."}` | first announce, Discord message posted |
| `200 {"status":"refreshed","messageId":"..."}` | same host re-announced existing code; existing embed was PATCHed in place |
| `200 {"status":"ignored"}` | announce: host is on denylist / report: host **or** reporter is (silent ack — don't surface to user) |
| `200 {"status":"reported"}` | /api/report: forwarded to the operator channel |
| `200 {"status":"disabled"}` | /api/report: `DISCORD_REPORT_WEBHOOK_URL` unset; report accepted and dropped |
| `200 {"status":"started"}` | start-edit succeeded |
| `200 {"status":"lobby-resumed"}` | end: embed flipped back to "open" (Play-Again ready) |
| `200 {"status":"closed"}` | close: Discord message + KV entry deleted |
| `200 {"status":"no-op"}` | start/end/close called for an unknown code (already expired or never announced) |
| `400 {"error":"..."}` | malformed body |
| `401 {"error":"bad signature"}` | HMAC/timestamp invalid |
| `403 {"error":"fcHash mismatch"}` | start/end called by wrong host |
| `409 {"error":"code already announced by another host"}` | someone else owns this code |
| `426 {"error":"version blocked — upgrade required"}` | `modVersion` listed in `BLOCKED_VERSIONS`; DLL should surface as "please update" |
| `429 {"error":"rate limited (ip\|host)"}` | DLL should back off ~RATE_LIMIT_SECONDS |
| `429 {"error":"rate limited (reporter)"}` | /api/report: this player reported within REPORT_RATE_LIMIT_SECONDS |
| `502 {"error":"discord post failed: ..."}` | announce: upstream Discord post error; retry later |
| `502 {"error":"discord delete failed: ..."}` | close: Discord delete still failing after retries; KV kept (messageId retained), embed not yet removed |

## DLL-side behavior expectations (Phase 2 must implement)

1. **Opt-in only.** Default-off. Toggle in Client Options: `ShareLobbyToDiscord`.
2. **Trigger points.**
   - announce: `LobbyBehaviour.Start` Postfix (delayed by a few seconds via LateTask
     until `PlayerControl.LocalPlayer` is available). Fires on both first-time and
     re-entry; server idempotently PATCHes on re-entry.
   - start: `ShipStatus.Begin` Postfix — only if announce succeeded.
   - end: `AmongUsClient.OnGameEnd` Postfix — only if announce succeeded.
     PATCHes the embed back to "open"; lobby still alive.
   - close: `LobbyBehaviour.OnDestroy` Postfix — gated by an `inGame` flag that is
     true between `start` and `end`. If `inGame=true` the OnDestroy is a
     lobby→ship transition and `close` is skipped; if `inGame=false` the host
     is leaving the lobby and `close` fires (DELETE message + KV).
3. **Fire-and-forget.** All HTTP via `UnityWebRequest` async; never block game thread.
4. **Failure UX.** Log + `Utils.SendMessage` to the host with the relay's error message. Match EHR's existing host-only error pattern.
5. **Idempotency.** OK to call `start` / `end` more than once. Relay treats unknown codes as `no-op`.
6. **Region exclusion.** If `IRegionInfo.Name` doesn't normalize to NA/EU/AS, don't announce (silently no-op). Don't crash.
7. **Android.** No platform exclusion. UnityWebRequest works the same way.
8. **HMAC key + FC_SALT.** Stored as `internal const` in `Modules/LobbyShareSecrets.cs` (gitignored; defaults to empty in `LobbyShareSecrets.Default.cs`). Both are extractable from the DLL — that's accepted. Rotation = ship a new release with a new key while leaving the old key in `SHARED_HMAC_KEYS` for a grace window so old DLLs keep working; remove the old key after the window (or immediately on confirmed leak).
9. **Reports are independent of the announce toggle.** `/report` works with
   `ShareLobbyToDiscord` off and in a lobby that was never announced — it is a
   moderation/bug channel, not part of lobby sharing. The only gate is
   `IsConfigured` (baked secrets), and even then the host writes the report to
   `EndKnot_Logs/EndKnot-Reports.log` **first**, so an unset secret, a relay outage
   or a source build still leaves the operator a local copy.
10. **Reports are never echoed by the mod** — no host chat line, no lobby message; the
    reporter gets a private ack and nothing else is displayed. **The reporter's own
    typing is a different matter, and the mod cannot fully cover it:**
    - `/cmd report ...` — genuinely private. Vanilla routes `/cmd` messages to the host
      only, so no other client ever receives the text.
    - bare `/report ...` from a **non-modded** client — vanilla has already broadcast it
      by the time the host sees it. `alwaysHidden` puts it through the flood-clear, which
      scrubs the scrollback but **cannot retract a chat bubble that already rendered**.
      Assume the lobby saw it for a moment. This is the same known compromise as Whisper
      and the faction chats; the in-game strings therefore teach `/cmd report`.
    - bare `/report ...` from a **modded** client or the host — cancelled before send,
      so nothing goes out.
11. **Kill switch.** `Options.ReportCommandEnabled` (default on) disables the command,
    matching every other "anyone types it, the host answers" command.

## Things deliberately NOT in this contract

- Player-count update mid-lobby. (Could add a `/api/update` later.)
- Per-game-mode embed customization. (Mode is just a label.)
- AU matchmaker-side code verification. (Requires the host's EOS bearer token — out of scope for the MVP.)
- Multiple Discord channels. (One relay = one channel. Spin up another Worker for another channel.)
