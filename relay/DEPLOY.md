# Deployment playbook — operator-only

Walks the relay operator through bringing the lobby-share feature live.
Targets someone who has never touched Cloudflare Workers before.

Prerequisites:
- Discord account with **Manage Webhooks** on the target channel
- Cloudflare account (free; no card required)
- Node.js 20+ installed locally
- Built and tested End K not DLL working in your AU client

Total wall-clock: ~30 min.

---

## Step 1 — Pick the Discord channel & make a webhook

1. In Discord: `Server Settings → Integrations → Webhooks → New Webhook`
2. Choose the channel (e.g. `#lobby-board`)
3. Name it `End K not Lobby Bot` (or anything)
4. Click `Copy Webhook URL`. Save it somewhere temporarily — it looks like:

   ```
   https://discord.com/api/webhooks/123.../abcXYZ...
   ```

   Anyone holding this URL can post in the channel. Treat as secret.

5. Repeat for a **second, private channel** (e.g. `#reports`, visible to operators
   only) and keep that webhook URL too — that one receives players' `/report`
   submissions. Reports must never land in the public lobby channel, so this is a
   different webhook, not the same one. Skipping it is allowed: the report endpoint
   then accepts and drops (hosts still keep a local `EndKnot-Reports.log`).

## Step 2 — Cloudflare account + KV namespace

1. Sign up at https://dash.cloudflare.com if you don't have an account
2. From any shell on your dev machine:

   ```bash
   cd relay
   npm install
   npx wrangler login              # opens browser, OAuth in
   npx wrangler kv namespace create STATE
   ```

3. Wrangler prints something like:

   ```
   { binding = "STATE", id = "abc123..." }
   ```

4. Open `relay/wrangler.toml` and replace `REPLACE_WITH_KV_NAMESPACE_ID` with that `id`.

## Step 3 — Generate the secrets

```bash
# In a shell where openssl is available (WSL / Mac / Linux / Git Bash):
openssl rand -hex 32        # → HMAC key. Save this; you'll also paste it into the DLL.
openssl rand -hex 32        # → admin token. Save for moderation commands.
```

On Windows-only PowerShell without openssl:

```powershell
$bytes = New-Object byte[] 32
(New-Object Security.Cryptography.RNGCryptoServiceProvider).GetBytes($bytes)
($bytes | ForEach-Object { $_.ToString('x2') }) -join ''
```

Run twice for the two values.

## Step 4 — Push secrets to the Worker

```bash
cd relay
npx wrangler secret put DISCORD_WEBHOOK_URL
#   → paste the Discord webhook URL from Step 1

npx wrangler secret put DISCORD_REPORT_WEBHOOK_URL
#   → paste the SECOND (operator-only) webhook URL from Step 1.5.
#   Optional: skip it and /api/report answers {"status":"disabled"} and drops
#   reports. Never point this at the lobby channel.

npx wrangler secret put ADMIN_TOKEN
#   → paste the admin token from Step 3

npx wrangler secret put SHARED_HMAC_KEYS
#   → paste the HMAC key from Step 3.
#   This is a comma-separated list of valid keys (newest first).
#   For the first deploy you only have one — just paste the single key.
#   Later, when rotating, re-put this with "K_new,K_old".
```

If you already ran `npx wrangler secret put SHARED_HMAC_KEY` (singular) on an
earlier draft of these docs, that legacy name still works — the Worker falls
back to it when `SHARED_HMAC_KEYS` is empty. But re-putting under the plural
name enables painless rotation later, so do that now:

```bash
npx wrangler secret delete SHARED_HMAC_KEY       # remove the legacy single-key secret
npx wrangler secret put SHARED_HMAC_KEYS          # paste the same key value
```

## Step 5 — Local smoke test

```bash
npx wrangler dev
# → terminal prints "Ready on http://localhost:8787"
```

In another terminal:

```bash
curl http://localhost:8787/
# → {"name":"endknot-lobby-relay","env":"production"}
```

Ctrl+C the dev server.

## Step 6 — Deploy to Cloudflare

```bash
npx wrangler deploy
```

Output ends with a URL like:

```
https://endknot-lobby-relay.<your-subdomain>.workers.dev
```

This is the relay URL. Save it.

Smoke-test the deploy:

```bash
curl https://endknot-lobby-relay.<your-subdomain>.workers.dev/
# → {"name":"endknot-lobby-relay","env":"production"}
```

## Step 7 — Bake constants into the DLL

The secrets live in a **separate gitignored file** — you cannot accidentally
commit them as long as you follow this exact procedure:

```bash
# From repo root:
cp Modules/LobbyShareSecrets.Default.cs Modules/LobbyShareSecrets.cs
```

Open `Modules/LobbyShareSecrets.cs` (NOT `.Default.cs`!) and fill in:

```csharp
internal static class LobbyShareSecrets
{
    internal const string RelayUrl = "https://endknot-lobby-relay.<your-subdomain>.workers.dev";
    internal const string HmacKey = "<paste HMAC key from Step 3>";
    internal const string FcSalt = "EndKnotLobbyShareV1Salt";
}
```

How this is safe:
- `Modules/LobbyShareSecrets.cs` is in `.gitignore` — `git status` will never show it.
- `EndKnot.csproj` detects the file at build time and swaps it in; the empty
  `LobbyShareSecrets.Default.cs` is removed from the compile.
- Source-builders who clone the public repo never get your file — their build
  uses the empty defaults and the feature is OFF (no announcements leave the host).

Sanity check before continuing:

```bash
git status Modules/LobbyShareSecrets.cs
# Expected output: nothing (the file is ignored)

git check-ignore Modules/LobbyShareSecrets.cs
# Expected output: Modules/LobbyShareSecrets.cs
```

If either command shows your file as tracked or untracked-but-visible, STOP
and check `.gitignore`.

## Step 8 — Build & test release DLL

```bash
dotnet build EndKnot.csproj -c Release -o build/
```

Copy `build/EndKnot.dll` to `<AmongUs>/BepInEx/plugins/EndKnot.dll`.
Copy `build/EndKnot.pdb` alongside it — copying the DLL on its own leaves a stale PDB behind,
and the mismatch strips file/line info from exception stack traces.

Launch AU as host:
1. Open Client Options → toggle **Share Lobby Code to Discord** ON
2. Create an online lobby
3. Within ~5s, the embed should appear in your Discord channel:
   ```
   🎫 Lobby Open
   Code: `ABCDEF`    Region: NA    Players: 1 / 15
   Mode: Standard    Version: 0.4.0    Host: <your-name>
   ```
4. Start the game → embed flips to `🎮 In Game` (orange)
5. End / leave the game → embed gets deleted

If any step fails, see "Debugging" below.

## Step 9 — Verify nothing leaked into git

```bash
git diff --cached
# Expected: no LobbyShareSecrets.cs in the staged set.
git ls-files | grep -i secret
# Expected: Modules/LobbyShareSecrets.Default.cs (and only this).
```

If `LobbyShareSecrets.cs` shows up anywhere in `git ls-files`, you've somehow
forced-added it. Remove it from history before pushing:

```bash
git rm --cached Modules/LobbyShareSecrets.cs
git commit -m "remove accidentally tracked secrets file"
```

---

## Debugging

### `wrangler tail` — live logs

```bash
cd relay
npx wrangler tail
```

Every request to the Worker prints here. Look for:
- `[error] bad signature` → DLL HMAC key ≠ Worker secret
- `[error] rate limited (ip)` → you're testing too fast (back off ~60s)
- `[error] discord post failed: 401` → Webhook URL got revoked or wrong
- `[error] invalid region` → AU returned a region name we don't recognize; check `wrangler tail` log for raw value

### Host's in-game popup

If the host has `ShareLobbyToDiscord` ON and announce fails, the host sees:

```
[ロビー共有] ロビー告知に失敗 (401): bad signature
```

That's the relay's exact error code + message. Cross-reference with `wrangler tail`.

### Banning an abuser

Get their `fcHash` from `wrangler tail` while they're announcing:

```bash
curl -X POST https://<your-worker>.workers.dev/admin/ban \
    -H "Authorization: Bearer <ADMIN_TOKEN>" \
    -H "content-type: application/json" \
    -d '{"fcHash":"<64-hex from tail>"}'
```

List current denylist:

```bash
curl https://<your-worker>.workers.dev/admin/list \
    -H "Authorization: Bearer <ADMIN_TOKEN>"
```

---

## Rolling secrets after a leak

1. Generate a new HMAC key (Step 3)
2. `wrangler secret put SHARED_HMAC_KEY` with the new value
3. Update `Modules/LobbyShare.cs` constant in your build dir
4. Build new release, distribute
5. Old DLLs will start receiving `401 bad signature` and silently stop announcing — that's fine, the feature is opt-in anyway

---

## Known limitations (MVP)

- **Abandoned-lobby messages linger for up to `STALE_SECONDS` + 10 min.** A clean quit sends `/api/close` and the embed goes immediately. A host that dies abruptly (watchdog restart, official-server ban, crash) can't send anything, so the embed stays until the scheduled sweep reaps it — worst case ~70 min with the default settings. Force it early with `POST /admin/sweep`.
- **First-time region detection is unverified.** If your AU client returns a region name format we didn't anticipate, the feature silently no-ops. Check `BepInEx/LogOutput.log` for `[LobbyShare][Info] unrecognized region: '...'` on first test and update `NormalizeRegion` in `Modules/LobbyShare.cs`.
- **Multiple Harmony postfixes on `ShipStatus.Begin`.** We add a postfix; the existing `ShipStatusBeginPatch.Prefix` returns `RolesIsAssigned`. Harmony docs say postfixes still run even if a prefix returned false, but verify on first test by watching for the `/api/start ok` log line.

## Cost & quota monitoring

In the Cloudflare dashboard:
- Workers & Pages → endknot-lobby-relay → Metrics
- Watch `Requests/day` (free cap 100k) and `KV writes/day` (free cap 1k)

Writes are the tight one — a high-churn streaming day can approach the 1k cap on
its own (see the per-op table in README.md). Reads are not a concern; a metrics
panel dominated by "Not found" reads is the denylist and rate-limit lookups
behaving normally, not a fault.

If you ever approach the free cap, raise `RATE_LIMIT_SECONDS` in `wrangler.toml`
to slow things down. The Worker will return 503 if you exceed the free tier —
**no auto-billing happens** as long as you haven't manually enabled paid plans.

The sweep runs on a cron trigger; `wrangler deploy` registers it from
`[triggers]` in `wrangler.toml`. Confirm it under Workers & Pages →
endknot-lobby-relay → Settings → Trigger Events after deploying, and watch its
`sweep: {...}` lines with `npx wrangler tail`.
