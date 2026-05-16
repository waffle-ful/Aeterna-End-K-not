# DLL ↔ Relay contract (frozen before Phase 2)

This is the wire format the DLL (`Modules/LobbyShare.cs`) and the Worker
(`relay/src/index.ts`) agree on. Both sides must follow it byte-for-byte
or signatures break.

## Endpoints

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/announce` | new lobby created |
| `POST` | `/api/start` | game has started — edit embed to "in-game" |
| `POST` | `/api/end` | game finished / lobby closed — delete message |

All three require these headers:

```
X-Timestamp: <unix seconds, no fraction>
X-Signature: <hex sha256 HMAC>
content-type: application/json
```

`X-Signature` is computed as:

```
HMAC_SHA256(key = SHARED_HMAC_KEY, message = X-Timestamp + "." + raw_request_body)
```

- The body MUST be the exact bytes sent on the wire (no whitespace re-normalization)
- `SHARED_HMAC_KEY` is a constant baked into the DLL and stored as a Worker secret
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

### `POST /api/start` and `POST /api/end`

```json
{ "code": "ABCDEF", "fcHash": "<same 64 hex>" }
```

The relay verifies `fcHash` matches the value stored at announce time;
otherwise responds `403 fcHash mismatch`. This is the only ownership check
— sufficient for protecting against accidental cross-mutation, not
sufficient for a determined attacker.

## Responses

All responses are JSON. Status codes:

| status | meaning |
|---|---|
| `200 {"status":"announced","messageId":"..."}` | first announce, Discord message posted |
| `200 {"status":"dedup","messageId":"..."}` | same host re-announcing same code inside DEDUP window |
| `200 {"status":"ignored"}` | host is on denylist (silent ack — don't surface to user) |
| `200 {"status":"started"}` | start-edit succeeded |
| `200 {"status":"ended"}` | end-delete succeeded |
| `200 {"status":"no-op"}` | start/end called for an unknown code (already expired) |
| `200 {"status":"kv-cleared","warn":"..."}` | end: KV cleared but Discord delete failed; UI should treat as success |
| `400 {"error":"..."}` | malformed body |
| `401 {"error":"bad signature"}` | HMAC/timestamp invalid |
| `403 {"error":"fcHash mismatch"}` | start/end called by wrong host |
| `409 {"error":"code already announced by another host"}` | someone else owns this code |
| `429 {"error":"rate limited (ip\|host)"}` | DLL should back off ~RATE_LIMIT_SECONDS |
| `502 {"error":"discord post failed: ..."}` | upstream Discord error; retry later |

## DLL-side behavior expectations (Phase 2 must implement)

1. **Opt-in only.** Default-off. Toggle in Client Options: `ShareLobbyToDiscord`.
2. **Trigger points.**
   - announce: `LobbyBehaviour.Start` Postfix, after game code is known & host is confirmed.
   - start: `IntroCutscene.CoBegin` Postfix (or equivalent game-start hook) — only if announce succeeded.
   - end: `EndGameManager.SetEverythingUp` Postfix — only if announce succeeded.
   - cancel: `LobbyBehaviour.OnDestroy` — also fires `end` so abandoned lobbies clear quickly.
3. **Fire-and-forget.** All HTTP via `UnityWebRequest` async; never block game thread.
4. **Failure UX.** Log + `Utils.SendMessage` to the host with the relay's error message. Match EHR's existing host-only error pattern.
5. **Idempotency.** OK to call `start` / `end` more than once. Relay treats unknown codes as `no-op`.
6. **Region exclusion.** If `IRegionInfo.Name` doesn't normalize to NA/EU/AS, don't announce (silently no-op). Don't crash.
7. **Android.** No platform exclusion. UnityWebRequest works the same way.
8. **HMAC key + FC_SALT.** Stored as `internal const` in `Modules/LobbyShare.cs`. Both are extractable from the DLL — that's accepted. Rotation = new release + new Worker secret.

## Things deliberately NOT in this contract

- Player-count update mid-lobby. (Could add a `/api/update` later.)
- Per-game-mode embed customization. (Mode is just a label.)
- AU matchmaker-side code verification. (Requires the host's EOS bearer token — out of scope for the MVP.)
- Multiple Discord channels. (One relay = one channel. Spin up another Worker for another channel.)
