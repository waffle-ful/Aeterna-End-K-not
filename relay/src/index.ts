// End K not lobby relay
//
// Receives lobby announcements from hosts running End K not and posts them to a
// Discord channel via webhook. The Discord webhook URL is held in Worker Secrets
// — the DLL only knows this Worker's public URL.
//
// Threat model:
//   • Casual curl-attacks against the public URL are blocked by HMAC signature
//     check (SHARED_HMAC_KEYS) + 5-min timestamp window. Multiple keys are
//     accepted simultaneously so rotation doesn't break already-shipped DLLs:
//     add the new key alongside the old one, ship a release using the new key,
//     then drop the old key after a grace window (or immediately if leaked).
//   • A motivated reverser who pulls a key out of any released DLL bypasses
//     HMAC for that key. Mitigation = drop the leaked key from SHARED_HMAC_KEYS
//     (only old DLLs using that exact key go silent) or add BLOCKED_VERSIONS
//     entry for the leaked release.
//   • Denylist (fcHash) is best-effort; an attacker with a valid HMAC key can
//     spoof fcHash freely. Treat the denylist as "stops a known griefer from
//     their usual Steam account", not "cryptographic ban".
//
// Routes:
//   POST /api/announce   — host announces a new lobby (HMAC required)
//   POST /api/start      — game started, edit embed to in-game (HMAC required)
//   POST /api/end        — game ended, delete message (HMAC required)
//   POST /api/report     — a player's in-game /report, forwarded by the host to the
//                          operator-only channel (HMAC required, separate webhook)
//   POST /admin/ban      — bearer-auth: add fcHash to denylist
//   POST /admin/unban    — bearer-auth: remove fcHash from denylist
//   GET  /admin/list     — bearer-auth: list current denylist
//   POST /admin/sweep    — bearer-auth: run the stale-lobby sweep on demand
//   GET  /                — health check
//
// Scheduled (see [triggers] in wrangler.toml):
//   Every 10 min — sweep lobbies whose host died without sending /api/close
//   (process kill, official-server ban, crash). Without this, any abrupt host
//   death orphans the Discord embed permanently: KV expires at TTL and nothing
//   ever deletes the message. This is the self-healing path the 2026-07-01
//   delete-retry fix assumed existed but never had.
//
// KV keys (binding STATE):
//   code:<CODE>          — { messageId, fcHash, createdAt, lastSeenAt, status, announce } TTL=ANNOUNCE_TTL_SECONDS
//   rl:ip:<ip>           — "1" TTL=RATE_LIMIT_SECONDS
//   rl:fc:<fcHash>       — "1" TTL=RATE_LIMIT_SECONDS
//   rl:rep:<fcHash>      — "1" TTL=REPORT_RATE_LIMIT_SECONDS (per reporting player)
//   deny:fc:<fcHash>     — "1" (no TTL, manual unban)

export interface Env {
    STATE: KVNamespace;
    DISCORD_WEBHOOK_URL: string;
    // Separate webhook for /api/report. Deliberately NOT the announce channel — the
    // announce channel is public-facing, reports are operator-only. Unset = the
    // report endpoint accepts and silently drops (mirrors the DLL's IsConfigured
    // no-op), so a source build never leaks reports into the lobby channel.
    DISCORD_REPORT_WEBHOOK_URL?: string;
    ADMIN_TOKEN: string;
    // Comma-separated list of currently-valid HMAC keys (newest first).
    // Falls back to legacy single-key SHARED_HMAC_KEY if unset.
    SHARED_HMAC_KEYS?: string;
    SHARED_HMAC_KEY?: string;
    // Comma-separated list of exact modVersion strings to reject at announce.
    // Empty / unset = no version block.
    BLOCKED_VERSIONS?: string;
    ENV: string;
    ANNOUNCE_TTL_SECONDS: string;
    RATE_LIMIT_SECONDS: string;
    // How long an entry may go without ANY write (announce / update / start / end)
    // before the scheduled sweep treats it as an abandoned lobby and deletes the
    // embed. Must comfortably exceed the longest realistic single game, because a
    // game in progress writes nothing between /api/start and /api/end.
    STALE_SECONDS?: string;
    // Per-reporting-player floor between accepted /api/report calls. The host DLL
    // enforces its own cooldown too; this is the server-side backstop that also
    // caps the KV write cost of the endpoint.
    REPORT_RATE_LIMIT_SECONDS?: string;
    // Legacy — kept in interface for back-compat with deployed wrangler.toml that
    // still declares the var. No longer read (dedup is folded into idempotent
    // /api/announce PATCH behavior).
    DEDUP_WINDOW_SECONDS?: string;
}

interface AnnounceBody {
    code: string;
    region: string;
    players: number;
    max: number;
    mode: string;
    modVersion: string;
    hostName: string;
    fcHash: string;
}

interface LifecycleBody {
    code: string;
    fcHash: string;
}

// A player's in-game /report, relayed by the host. Unlike every other endpoint this
// one carries no lobby state: it must work in a lobby that was never announced (the
// host may have lobby sharing off) and while a game is running, so nothing here is
// looked up in or written to `code:`.
interface ReportBody {
    // Lobby code, or "" when there is none (local/freeplay game).
    code: string;
    // Host's fcHash — signs who forwarded the report, and what the denylist gates.
    fcHash: string;
    // Reporting player's name and fcHash. The hash is what rate-limits and what the
    // operator bans; the name alone is not unique and is trivially spoofed.
    reporter: string;
    reporterHash: string;
    text: string;
    region: string;
    mode: string;
    modVersion: string;
    // "lobby" / "in-game" / "meeting" — where the reporter was when they typed it.
    phase: string;
}

interface AnnouncePublic {
    code: string;
    region: string;
    players: number;
    max: number;
    mode: string;
    modVersion: string;
    hostName: string;
}

interface CodeEntry {
    messageId: string;
    fcHash: string;
    createdAt: number;
    // Epoch ms of the last write touching this entry. Absent on entries written by
    // pre-sweep deploys — callers fall back to createdAt.
    lastSeenAt?: number;
    status: "open" | "in-game";
    announce: AnnouncePublic;
}

const CODE_RE = /^[A-Z0-9]{6}$/;
const HEX64_RE = /^[a-f0-9]{64}$/;
const HEX_SIG_RE = /^[a-f0-9]{64}$/;

// Accept both short codes (DLL-side normalized) and the vanilla long names.
const REGION_ALIASES: Record<string, string> = {
    "NA": "NA", "NORTH AMERICA": "NA",
    "EU": "EU", "EUROPE": "EU",
    "AS": "AS", "ASIA": "AS",
};

const COLOR_OPEN = 0x5865f2;     // Discord blurple
const COLOR_IN_GAME = 0xfaa61a;  // amber

const SIGNATURE_SKEW_SECONDS = 300;
const ANONYMOUS_HOST_NAME = "Anonymous";

// A lobby with no writes for this long is treated as abandoned by the sweep.
// A game in progress writes nothing between /api/start and /api/end, so this has
// to sit well above the longest realistic single game.
const DEFAULT_STALE_SECONDS = 3600;
// Minimum gap between keepalive-only KV writes (see handleUpdate). Deliberately
// half the DLL's KeepaliveSeconds (600) — the two sides run on independent clocks,
// so an equal threshold would let every other keepalive land just under the bar and
// silently double the effective refresh interval.
const TOUCH_MIN_INTERVAL_SECONDS = 300;
// An in-game entry legitimately goes quiet: nothing is written between /api/start
// and /api/end, and the DLL's keepalive only runs during the lobby phase (there is
// no LobbyBehaviour while a game is running). Give those entries a longer leash so
// a long game can't have its own embed swept out from under it.
const IN_GAME_STALE_MULTIPLIER = 3;
// Cap per sweep run so one pass can't blow the KV write budget on a bad day.
const SWEEP_MAX_DELETES = 25;

const COLOR_REPORT = 0xed4245;   // Discord red
// Per-reporter floor between accepted reports. Chat is cheap to spam and every
// accepted report costs a KV write, which is the free tier's binding limit.
// Must stay SHORTER than the DLL's ReportCooldownSeconds (180) — the DLL acks the
// reporter as soon as it accepts, so a report this endpoint then 429s is a loss the
// player was told went through. Raising this above the DLL's floor reintroduces that.
const DEFAULT_REPORT_RATE_LIMIT_SECONDS = 120;
// Report text cap. Well under Discord's 4096-char embed description limit; the
// reporter is typing into an Among Us chat box, so anything longer is a paste bomb.
const REPORT_TEXT_MAX = 400;
const REPORT_NAME_MAX = 32;

export default {
    async fetch(req: Request, env: Env): Promise<Response> {
        const url = new URL(req.url);
        const method = req.method.toUpperCase();
        const path = url.pathname;

        try {
            if (method === "GET" && path === "/") return ok({ name: "endknot-lobby-relay", env: env.ENV });

            if (path.startsWith("/admin/")) {
                const auth = req.headers.get("authorization") ?? "";
                if (auth !== `Bearer ${env.ADMIN_TOKEN}`) return err(401, "unauthorized");
                if (method === "POST" && path === "/admin/ban") return await handleAdminBan(req, env, true);
                if (method === "POST" && path === "/admin/unban") return await handleAdminBan(req, env, false);
                if (method === "GET" && path === "/admin/list") return await handleAdminList(env);
                if (method === "POST" && path === "/admin/sweep") return ok(await sweepStaleLobbies(env));
                return err(404, "not found");
            }

            if (method === "POST" && (path === "/api/announce" || path === "/api/start" || path === "/api/end" || path === "/api/close" || path === "/api/update" || path === "/api/report")) {
                const raw = await req.text();
                const sigOk = await verifySignature(req, env, raw);
                if (!sigOk) return err(401, "bad signature");
                if (path === "/api/announce") return await handleAnnounce(req, env, raw);
                if (path === "/api/report") return await handleReport(env, raw);
                if (path === "/api/update") return await handleUpdate(env, raw);
                if (path === "/api/start") return await handleLifecycle(env, raw, "start");
                if (path === "/api/end") return await handleLifecycle(env, raw, "end");
                return await handleLifecycle(env, raw, "close");
            }

            return err(404, "not found");
        } catch (e) {
            return err(500, `internal error: ${(e as Error).message ?? "unknown"}`);
        }
    },

    // Cron trigger — see [triggers] in wrangler.toml.
    async scheduled(_event: ScheduledController, env: Env, ctx: ExecutionContext): Promise<void> {
        ctx.waitUntil(sweepStaleLobbies(env).then(r => {
            if (r.deleted > 0 || r.failed > 0) console.log(`sweep: ${JSON.stringify(r)}`);
        }));
    },
} satisfies ExportedHandler<Env>;

// ─── sweep ─────────────────────────────────────────────────────────────────────

interface SweepResult { scanned: number; deleted: number; failed: number; codes: string[]; }

// Deletes the Discord embed for lobbies whose host vanished without sending
// /api/close — process kill, official-server ban, crash, watchdog restart. Those
// paths can't be closed from the client (the process is gone before the HTTP call
// lands), so the server has to reap them or the message stays up forever.
//
// Liveness signal is lastSeenAt, refreshed by every announce/update/start/end.
// Entries written before this field existed fall back to createdAt.
async function sweepStaleLobbies(env: Env): Promise<SweepResult> {
    const staleMs = num(env.STALE_SECONDS, DEFAULT_STALE_SECONDS) * 1000;
    const now = Date.now();
    const result: SweepResult = { scanned: 0, deleted: 0, failed: 0, codes: [] };

    let cursor: string | undefined;
    do {
        const page = await env.STATE.list({ prefix: "code:", cursor });
        cursor = page.list_complete ? undefined : page.cursor;

        for (const k of page.keys) {
            result.scanned++;
            if (result.deleted + result.failed >= SWEEP_MAX_DELETES) return result;

            const entry = await env.STATE.get(k.name, "json") as CodeEntry | null;
            // Expired between list and get — nothing to delete on the Discord side
            // that we still have a handle for.
            if (!entry) continue;
            const limit = entry.status === "in-game" ? staleMs * IN_GAME_STALE_MULTIPLIER : staleMs;
            if (now - lastSeen(entry) <= limit) continue;

            const code = k.name.slice("code:".length);
            const r = await deleteDiscordMessage(env.DISCORD_WEBHOOK_URL, entry.messageId);
            if (!r.ok) {
                // Keep the KV entry so the next run still has the messageId.
                result.failed++;
                console.log(`sweep: delete failed for ${code}: ${r.error}`);
                continue;
            }
            await env.STATE.delete(k.name);
            result.deleted++;
            result.codes.push(code);
        }
    } while (cursor);

    return result;
}

// ─── signature ─────────────────────────────────────────────────────────────────

async function verifySignature(req: Request, env: Env, body: string): Promise<boolean> {
    const sigHeader = (req.headers.get("x-signature") ?? "").toLowerCase();
    const tsHeader = req.headers.get("x-timestamp") ?? "";
    if (!HEX_SIG_RE.test(sigHeader)) return false;
    const ts = Number(tsHeader);
    if (!Number.isFinite(ts)) return false;
    const now = Math.floor(Date.now() / 1000);
    if (Math.abs(now - ts) > SIGNATURE_SKEW_SECONDS) return false;

    const keys = getHmacKeys(env);
    if (keys.length === 0) return false;

    const enc = new TextEncoder();
    const msg = enc.encode(`${tsHeader}.${body}`);
    // Try each key. We can't short-circuit on first match without breaking
    // timing-safety across keys, but the keys-list is tiny (≤ a handful) so the
    // extra HMACs are negligible vs the per-key sign already needed.
    let matched = false;
    for (const k of keys) {
        const key = await crypto.subtle.importKey(
            "raw", enc.encode(k), { name: "HMAC", hash: "SHA-256" }, false, ["sign"],
        );
        const macBuf = await crypto.subtle.sign("HMAC", key, msg);
        const expected = toHex(new Uint8Array(macBuf));
        if (timingSafeEqual(sigHeader, expected)) matched = true;
    }
    return matched;
}

function getHmacKeys(env: Env): string[] {
    // Prefer the plural list. Empty / unset → fall back to the singular legacy var
    // so previously-deployed Workers (with only SHARED_HMAC_KEY set) keep working
    // until the operator re-puts under SHARED_HMAC_KEYS.
    const raw = (env.SHARED_HMAC_KEYS && env.SHARED_HMAC_KEYS.trim().length > 0)
        ? env.SHARED_HMAC_KEYS
        : (env.SHARED_HMAC_KEY ?? "");
    return raw.split(",").map(s => s.trim()).filter(s => s.length > 0);
}

function isBlockedVersion(modVersion: string, env: Env): boolean {
    const raw = (env.BLOCKED_VERSIONS ?? "").trim();
    if (raw.length === 0) return false;
    const list = raw.split(",").map(s => s.trim()).filter(s => s.length > 0);
    return list.includes(modVersion);
}

function toHex(buf: Uint8Array): string {
    let out = "";
    for (let i = 0; i < buf.length; i++) out += buf[i].toString(16).padStart(2, "0");
    return out;
}

function timingSafeEqual(a: string, b: string): boolean {
    if (a.length !== b.length) return false;
    let diff = 0;
    for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
    return diff === 0;
}

// ─── handlers ──────────────────────────────────────────────────────────────────

async function handleAnnounce(req: Request, env: Env, raw: string): Promise<Response> {
    const parsed = safeParse<Partial<AnnounceBody>>(raw);
    if (!parsed) return err(400, "invalid json");

    const v = validateAnnounce(parsed);
    if (!v.ok) return err(400, v.error);
    const a = v.value;

    if (isBlockedVersion(a.modVersion, env)) {
        // Explicit error — operator deliberately gated this version; host should upgrade.
        return err(426, "version blocked — upgrade required");
    }

    if (await env.STATE.get(`deny:fc:${a.fcHash}`)) {
        // Silent-ack so a banned host can't probe the denylist.
        return ok({ status: "ignored" });
    }

    const existing = await env.STATE.get(`code:${a.code}`, "json") as CodeEntry | null;
    if (existing && existing.fcHash !== a.fcHash) {
        return err(409, "code already announced by another host");
    }

    const publicData = stripFcHash(a);

    // Same-host re-announce (lobby returning from a game, player count change, etc.):
    // PATCH the existing embed rather than POST a new one. This is the "one lobby = one
    // message" invariant — keeps the Discord channel from spamming on every Play-Again.
    // No rate-limit applies because we're not creating new state; per-IP / per-host
    // limits only gate first announces.
    if (existing) {
        const r = await editDiscordMessage(env.DISCORD_WEBHOOK_URL, existing.messageId, buildEmbed(publicData, "open"));
        if (!r.ok) return err(502, `discord patch failed: ${r.error}`);
        existing.status = "open";
        existing.announce = publicData;
        await putEntry(env, a.code, existing);
        return ok({ status: "refreshed", messageId: existing.messageId });
    }

    const ip = req.headers.get("cf-connecting-ip") ?? "unknown";
    const rateLimitSec = num(env.RATE_LIMIT_SECONDS, 60);
    if (await env.STATE.get(`rl:ip:${ip}`)) return err(429, "rate limited (ip)");
    if (await env.STATE.get(`rl:fc:${a.fcHash}`)) return err(429, "rate limited (host)");

    const sendResult = await postDiscordMessage(env.DISCORD_WEBHOOK_URL, buildEmbed(publicData, "open"));
    if (!sendResult.ok) return err(502, `discord post failed: ${sendResult.error}`);

    const entry: CodeEntry = {
        messageId: sendResult.messageId,
        fcHash: a.fcHash,
        createdAt: Date.now(),
        status: "open",
        announce: publicData,
    };

    await Promise.all([
        putEntry(env, a.code, entry),
        env.STATE.put(`rl:ip:${ip}`, "1", { expirationTtl: kvTtl(rateLimitSec) }),
        env.STATE.put(`rl:fc:${a.fcHash}`, "1", { expirationTtl: kvTtl(rateLimitSec) }),
    ]);

    return ok({ status: "announced", messageId: sendResult.messageId });
}

async function handleLifecycle(env: Env, raw: string, phase: "start" | "end" | "close"): Promise<Response> {
    const body = safeParse<Partial<LifecycleBody>>(raw);
    if (!body) return err(400, "invalid json");

    const code = (body.code ?? "").toString().toUpperCase();
    const fcHash = (body.fcHash ?? "").toString().toLowerCase();
    if (!CODE_RE.test(code)) return err(400, "invalid code");
    if (!HEX64_RE.test(fcHash)) return err(400, "invalid fcHash");

    const entry = await env.STATE.get(`code:${code}`, "json") as CodeEntry | null;
    if (!entry) return ok({ status: "no-op" });
    if (entry.fcHash !== fcHash) return err(403, "fcHash mismatch");

    if (phase === "start") {
        // Game starting — flip embed to in-game (amber).
        const r = await editDiscordMessage(
            env.DISCORD_WEBHOOK_URL,
            entry.messageId,
            buildEmbed(entry.announce, "in-game"),
        );
        if (!r.ok) return err(502, `discord patch failed: ${r.error}`);
        entry.status = "in-game";
        await putEntry(env, code, entry);
        return ok({ status: "started" });
    }

    if (phase === "end") {
        // Game ended — flip embed BACK to "open" so the same code stays usable
        // for Play Again. KV entry stays alive; we only DELETE on /api/close.
        const r = await editDiscordMessage(
            env.DISCORD_WEBHOOK_URL,
            entry.messageId,
            buildEmbed(entry.announce, "open"),
        );
        if (!r.ok) return err(502, `discord patch failed: ${r.error}`);
        entry.status = "open";
        await putEntry(env, code, entry);
        return ok({ status: "lobby-resumed" });
    }

    // phase === "close" — lobby truly destroyed (host left). DELETE message, THEN KV.
    const r = await deleteDiscordMessage(env.DISCORD_WEBHOOK_URL, entry.messageId);
    if (!r.ok) {
        // Discord delete still failing after retries. Do NOT delete KV — keeping the
        // entry preserves messageId so a later close retry / scheduled sweep can finish
        // the job. The old code deleted KV here and returned 2xx ("kv-cleared"), which
        // permanently orphaned the embed AND made the client log it as success. Surface
        // 502 instead so the failure is observable and the handle is retained.
        return err(502, `discord delete failed: ${r.error}`);
    }
    await env.STATE.delete(`code:${code}`);
    return ok({ status: "closed" });
}

// PATCH the existing embed with new player count / max / mode. Status (open vs
// in-game) is preserved — this endpoint is for live updates, not state changes.
// DLL fires this throttled (~5s + diff-detect) from LobbyBehaviour.Update.
async function handleUpdate(env: Env, raw: string): Promise<Response> {
    const body = safeParse<Partial<{ code: string; fcHash: string; players?: number; max?: number; mode?: string }>>(raw);
    if (!body) return err(400, "invalid json");

    const code = (body.code ?? "").toString().toUpperCase();
    const fcHash = (body.fcHash ?? "").toString().toLowerCase();
    if (!CODE_RE.test(code)) return err(400, "invalid code");
    if (!HEX64_RE.test(fcHash)) return err(400, "invalid fcHash");

    const entry = await env.STATE.get(`code:${code}`, "json") as CodeEntry | null;
    if (!entry) return ok({ status: "no-op" });
    if (entry.fcHash !== fcHash) return err(403, "fcHash mismatch");

    let changed = false;
    if (typeof body.players === "number") {
        const p = Math.floor(body.players);
        if (p >= 1 && p <= 15 && entry.announce.players !== p) { entry.announce.players = p; changed = true; }
    }
    if (typeof body.max === "number") {
        const m = Math.floor(body.max);
        if (m >= 1 && m <= 15 && entry.announce.max !== m) { entry.announce.max = m; changed = true; }
    }
    if (typeof body.mode === "string") {
        const m = sanitize(body.mode, 32) || "Standard";
        if (entry.announce.mode !== m) { entry.announce.mode = m; changed = true; }
    }

    if (!changed) {
        // Nothing to show differently — but this call is also the DLL's keepalive, and
        // the sweep reaps entries by staleness. Refresh lastSeenAt (no Discord call) if
        // the entry is getting old; skip the write entirely otherwise so a chatty client
        // can't burn the KV write budget.
        if (Date.now() - lastSeen(entry) > TOUCH_MIN_INTERVAL_SECONDS * 1000) {
            await putEntry(env, code, entry);
            return ok({ status: "touched" });
        }
        return ok({ status: "no-change" });
    }

    const phase = entry.status === "in-game" ? "in-game" : "open";
    const r = await editDiscordMessage(env.DISCORD_WEBHOOK_URL, entry.messageId, buildEmbed(entry.announce, phase));
    if (!r.ok) return err(502, `discord patch failed: ${r.error}`);

    await putEntry(env, code, entry);
    return ok({ status: "updated" });
}

// Forwards a player's /report to the operator-only channel. Deliberately stateless:
// no `code:` read, no `code:` write, and it works for a lobby that was never
// announced — reports must not depend on the host having lobby sharing turned on.
//
// Cost: exactly one KV write (the rate-limit key) per ACCEPTED report, and zero on
// every rejected one. Writes are the free tier's binding limit, so nothing periodic
// may ever be added to this path.
async function handleReport(env: Env, raw: string): Promise<Response> {
    const parsed = safeParse<Partial<ReportBody>>(raw);
    if (!parsed) return err(400, "invalid json");

    const v = validateReport(parsed);
    if (!v.ok) return err(400, v.error);
    const r = v.value;

    // Denylist covers both ends: a banned host can't use their lobby as a relay, and
    // a banned player can't keep spamming reports through an innocent host. Silent-ack
    // both so neither can probe the list.
    if (await env.STATE.get(`deny:fc:${r.fcHash}`)) return ok({ status: "ignored" });
    if (await env.STATE.get(`deny:fc:${r.reporterHash}`)) return ok({ status: "ignored" });

    if (await env.STATE.get(`rl:rep:${r.reporterHash}`)) return err(429, "rate limited (reporter)");

    const webhook = (env.DISCORD_REPORT_WEBHOOK_URL ?? "").trim();
    // No report channel configured — accept and drop rather than 500. Reporting must
    // never fail loudly in front of players just because the operator hasn't set the
    // secret; the host log carries the status so the operator can still notice.
    if (webhook.length === 0) return ok({ status: "disabled" });

    const sendResult = await postDiscordMessage(webhook, buildReportEmbed(r));
    // Don't burn the rate-limit key on a failed send — the reporter should be able to
    // try again immediately rather than be locked out by our own outage.
    if (!sendResult.ok) return err(502, `discord post failed: ${sendResult.error}`);

    await env.STATE.put(`rl:rep:${r.reporterHash}`, "1", {
        expirationTtl: kvTtl(num(env.REPORT_RATE_LIMIT_SECONDS, DEFAULT_REPORT_RATE_LIMIT_SECONDS)),
    });

    return ok({ status: "reported" });
}

async function handleAdminBan(req: Request, env: Env, ban: boolean): Promise<Response> {
    const body = await safeJson<{ fcHash?: string }>(req);
    const fcHash = (body?.fcHash ?? "").toString().toLowerCase();
    if (!HEX64_RE.test(fcHash)) return err(400, "invalid fcHash");
    if (ban) await env.STATE.put(`deny:fc:${fcHash}`, "1");
    else await env.STATE.delete(`deny:fc:${fcHash}`);
    return ok({ status: ban ? "banned" : "unbanned", fcHash });
}

async function handleAdminList(env: Env): Promise<Response> {
    const list = await env.STATE.list({ prefix: "deny:fc:" });
    return ok({ entries: list.keys.map(k => k.name.slice("deny:fc:".length)) });
}

// ─── validation ────────────────────────────────────────────────────────────────

function validateAnnounce(b: Partial<AnnounceBody>):
    | { ok: true; value: AnnounceBody }
    | { ok: false; error: string } {
    const code = (b.code ?? "").toString().toUpperCase();
    if (!CODE_RE.test(code)) return { ok: false, error: "invalid code" };

    const regionRaw = (b.region ?? "").toString().trim().toUpperCase();
    const region = REGION_ALIASES[regionRaw];
    if (!region) return { ok: false, error: "invalid region" };

    const players = Math.floor(Number(b.players ?? -1));
    if (!Number.isFinite(players) || players < 1 || players > 15) return { ok: false, error: "invalid players" };

    const max = Math.floor(Number(b.max ?? -1));
    if (!Number.isFinite(max) || max < 1 || max > 15) return { ok: false, error: "invalid max" };

    const mode = sanitize(b.mode, 32) || "Standard";
    const modVersion = sanitize(b.modVersion, 32) || "unknown";
    const hostName = sanitize(b.hostName, 32) || ANONYMOUS_HOST_NAME;

    const fcHash = (b.fcHash ?? "").toString().toLowerCase();
    if (!HEX64_RE.test(fcHash)) return { ok: false, error: "invalid fcHash" };

    return { ok: true, value: { code, region, players, max, mode, modVersion, hostName, fcHash } };
}

function validateReport(b: Partial<ReportBody>):
    | { ok: true; value: ReportBody }
    | { ok: false; error: string } {
    // Every string here is attacker-controlled (a player typed it into chat), so
    // everything goes through sanitize() before it can reach a Discord embed.
    const text = sanitize(b.text, REPORT_TEXT_MAX);
    if (text.length === 0) return { ok: false, error: "empty text" };

    const fcHash = (b.fcHash ?? "").toString().toLowerCase();
    if (!HEX64_RE.test(fcHash)) return { ok: false, error: "invalid fcHash" };

    const reporterHash = (b.reporterHash ?? "").toString().toLowerCase();
    if (!HEX64_RE.test(reporterHash)) return { ok: false, error: "invalid reporterHash" };

    // Unlike the announce path, an empty code is legal: a report can come from a
    // local/freeplay game that has no lobby code at all.
    const code = (b.code ?? "").toString().toUpperCase();
    if (code.length > 0 && !CODE_RE.test(code)) return { ok: false, error: "invalid code" };

    const regionRaw = (b.region ?? "").toString().trim().toUpperCase();
    const region = REGION_ALIASES[regionRaw] ?? "";

    const reporter = sanitize(b.reporter, REPORT_NAME_MAX) || ANONYMOUS_HOST_NAME;
    const mode = sanitize(b.mode, 32) || "Standard";
    const modVersion = sanitize(b.modVersion, 32) || "unknown";
    const phase = sanitize(b.phase, 16) || "unknown";

    return { ok: true, value: { code, fcHash, reporter, reporterHash, text, region, mode, modVersion, phase } };
}

function sanitize(raw: unknown, max: number): string {
    if (typeof raw !== "string") return "";
    let s = raw.replace(/[\x00-\x1f\x7f]/g, "").trim();
    // Neuter Discord mentions & code-block escapes as defense-in-depth.
    s = s.replace(/@/g, "@​").replace(/```/g, "ʼʼʼ");
    if (s.length > max) s = s.slice(0, max);
    return s;
}

function stripFcHash(a: AnnounceBody): AnnouncePublic {
    const { fcHash: _, ...rest } = a;
    return rest;
}

// ─── discord ───────────────────────────────────────────────────────────────────

function buildEmbed(a: AnnouncePublic, phase: "open" | "in-game"): unknown {
    const isOpen = phase === "open";
    return {
        title: isOpen ? "🎫 Lobby Open" : "🎮 In Game",
        color: isOpen ? COLOR_OPEN : COLOR_IN_GAME,
        fields: [
            { name: "Code", value: "`" + a.code + "`", inline: true },
            { name: "Region", value: a.region, inline: true },
            { name: "Players", value: `${a.players} / ${a.max}`, inline: true },
            { name: "Mode", value: a.mode, inline: true },
            { name: "Version", value: a.modVersion, inline: true },
            { name: "Host", value: a.hostName, inline: true },
        ],
        timestamp: new Date().toISOString(),
        footer: { text: "End K not lobby relay" },
    };
}

function buildReportEmbed(r: ReportBody): unknown {
    return {
        title: "📮 Player Report",
        color: COLOR_REPORT,
        description: r.text,
        fields: [
            { name: "Reporter", value: r.reporter, inline: true },
            { name: "Code", value: r.code.length > 0 ? "`" + r.code + "`" : "—", inline: true },
            { name: "Region", value: r.region.length > 0 ? r.region : "—", inline: true },
            { name: "Mode", value: r.mode, inline: true },
            { name: "Phase", value: r.phase, inline: true },
            { name: "Version", value: r.modVersion, inline: true },
            // Full hashes, not truncated — this is what /admin/ban takes verbatim.
            { name: "Reporter hash", value: "`" + r.reporterHash + "`", inline: false },
            { name: "Host hash", value: "`" + r.fcHash + "`", inline: false },
        ],
        timestamp: new Date().toISOString(),
        footer: { text: "End K not player report" },
    };
}

interface DiscordPostOk { ok: true; messageId: string; }
interface DiscordOpResult { ok: boolean; error?: string; }

async function postDiscordMessage(webhook: string, embed: unknown): Promise<DiscordPostOk | { ok: false; error: string }> {
    const r = await fetch(addQuery(webhook, "wait", "true"), {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ embeds: [embed] }),
    });
    if (!r.ok) return { ok: false, error: `${r.status} ${await safeText(r)}` };
    const j = await r.json() as { id?: string };
    if (!j.id) return { ok: false, error: "discord did not return message id" };
    return { ok: true, messageId: j.id };
}

async function editDiscordMessage(webhook: string, messageId: string, embed: unknown): Promise<DiscordOpResult> {
    const r = await fetch(`${webhook}/messages/${messageId}`, {
        method: "PATCH",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ embeds: [embed] }),
    });
    if (!r.ok) return { ok: false, error: `${r.status} ${await safeText(r)}` };
    return { ok: true };
}

async function deleteDiscordMessage(webhook: string, messageId: string): Promise<DiscordOpResult> {
    // Discord rate-limits message deletes aggressively and occasionally returns a
    // transient 5xx. A single failed DELETE used to orphan the embed forever, so
    // retry 429 (honoring Retry-After) and 5xx with bounded backoff. 404 = the
    // message is already gone, which is success for our purposes.
    const maxAttempts = 4;
    let lastError = "";
    for (let attempt = 1; attempt <= maxAttempts; attempt++) {
        const r = await fetch(`${webhook}/messages/${messageId}`, { method: "DELETE" });
        if (r.ok || r.status === 404) return { ok: true };

        lastError = `${r.status} ${await safeText(r)}`;
        if (attempt === maxAttempts) break;

        if (r.status === 429) { await sleep(retryAfterMs(r, 1000)); continue; }
        if (r.status >= 500) { await sleep(attempt * 500); continue; }
        // Other 4xx (401/403/etc.) are not transient — retrying won't help.
        break;
    }
    return { ok: false, error: lastError };
}

// ─── helpers ───────────────────────────────────────────────────────────────────

function ok(body: unknown): Response {
    return new Response(JSON.stringify(body), {
        status: 200,
        headers: { "content-type": "application/json" },
    });
}

function err(status: number, message: string): Response {
    return new Response(JSON.stringify({ error: message }), {
        status,
        headers: { "content-type": "application/json" },
    });
}

function safeParse<T>(s: string): T | null {
    try { return JSON.parse(s) as T; } catch { return null; }
}

async function safeJson<T>(req: Request): Promise<T | null> {
    try { return await req.json() as T; } catch { return null; }
}

async function safeText(r: Response): Promise<string> {
    try { return (await r.text()).slice(0, 200); } catch { return ""; }
}

// Bounded sleep for retry backoff. Clamped to [0, 5000]ms so a hostile/garbage
// Retry-After can't stall the Worker (the client also has an 8s HTTP timeout).
function sleep(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, Math.max(0, Math.min(ms, 5000))));
}

// Discord 429 carries Retry-After (seconds, may be fractional). Fall back to
// x-ratelimit-reset-after, then to the caller's default. +100ms jitter pad.
function retryAfterMs(r: Response, fallbackMs: number): number {
    const ra = r.headers.get("retry-after") ?? r.headers.get("x-ratelimit-reset-after");
    const secs = ra ? Number(ra) : NaN;
    if (Number.isFinite(secs) && secs >= 0) return secs * 1000 + 100;
    return fallbackMs;
}

function num(v: string | undefined, fallback: number): number {
    const n = Number(v);
    return Number.isFinite(n) && n > 0 ? n : fallback;
}

// Cloudflare KV requires expirationTtl >= 60 seconds. Clamp any per-key TTL up
// to that floor so a stray config tuning below 60 doesn't make the entire
// announce path 500 (we hit this with DEDUP_WINDOW_SECONDS=30 on first deploy).
const KV_MIN_TTL_SECONDS = 60;
function kvTtl(seconds: number): number {
    return Math.max(KV_MIN_TTL_SECONDS, Math.floor(seconds));
}

// Stamp liveness and write with a SLIDING TTL measured from now — not a TTL that
// decays from createdAt. The decaying version expired the KV entry out from under
// a still-live embed once a lobby outlived ANNOUNCE_TTL_SECONDS; after that
// /api/close returned "no-op" and the Discord message was orphaned forever.
async function putEntry(env: Env, code: string, entry: CodeEntry): Promise<void> {
    entry.lastSeenAt = Date.now();
    await env.STATE.put(`code:${code}`, JSON.stringify(entry), { expirationTtl: kvTtl(entryTtl(env)) });
}

// The KV entry must always outlive the sweep's own staleness limit — if it expires
// first, the messageId is gone and nothing can ever delete the Discord message.
// Derived rather than read straight from ANNOUNCE_TTL_SECONDS so no combination of
// operator-tuned vars can reintroduce that orphan path.
function entryTtl(env: Env): number {
    const staleSec = num(env.STALE_SECONDS, DEFAULT_STALE_SECONDS);
    return Math.max(num(env.ANNOUNCE_TTL_SECONDS, 10800), staleSec * IN_GAME_STALE_MULTIPLIER * 2);
}

function lastSeen(entry: CodeEntry): number {
    return typeof entry.lastSeenAt === "number" ? entry.lastSeenAt : entry.createdAt;
}

function addQuery(url: string, key: string, value: string): string {
    return url + (url.includes("?") ? "&" : "?") + `${encodeURIComponent(key)}=${encodeURIComponent(value)}`;
}
