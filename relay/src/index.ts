// End K not lobby relay
//
// Receives lobby announcements from hosts running End K not and posts them to a
// Discord channel via webhook. The Discord webhook URL is held in Worker Secrets
// — the DLL only knows this Worker's public URL.
//
// Threat model:
//   • Casual curl-attacks against the public URL are blocked by HMAC signature
//     check (SHARED_HMAC_KEY) + 5-min timestamp window.
//   • A motivated reverser who pulls the key out of the DLL bypasses HMAC.
//     Mitigation = rotate the key + ship a new DLL build. The relay also rate-
//     limits per-IP so botnets are slow even after a leak.
//   • Denylist (fcHash) is best-effort; an attacker with the HMAC key can spoof
//     fcHash freely. Treat the denylist as "stops a known griefer from their
//     usual Steam account", not "cryptographic ban".
//
// Routes:
//   POST /api/announce   — host announces a new lobby (HMAC required)
//   POST /api/start      — game started, edit embed to in-game (HMAC required)
//   POST /api/end        — game ended, delete message (HMAC required)
//   POST /admin/ban      — bearer-auth: add fcHash to denylist
//   POST /admin/unban    — bearer-auth: remove fcHash from denylist
//   GET  /admin/list     — bearer-auth: list current denylist
//   GET  /                — health check
//
// KV keys (binding STATE):
//   code:<CODE>          — { messageId, fcHash, createdAt, status, announce } TTL=ANNOUNCE_TTL_SECONDS
//   rl:ip:<ip>           — "1" TTL=RATE_LIMIT_SECONDS
//   rl:fc:<fcHash>       — "1" TTL=RATE_LIMIT_SECONDS
//   deny:fc:<fcHash>     — "1" (no TTL, manual unban)
//   dedup:<CODE>:<fcHash>— "1" TTL=DEDUP_WINDOW_SECONDS

export interface Env {
    STATE: KVNamespace;
    DISCORD_WEBHOOK_URL: string;
    ADMIN_TOKEN: string;
    SHARED_HMAC_KEY: string;
    ENV: string;
    ANNOUNCE_TTL_SECONDS: string;
    RATE_LIMIT_SECONDS: string;
    DEDUP_WINDOW_SECONDS: string;
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
                return err(404, "not found");
            }

            if (method === "POST" && (path === "/api/announce" || path === "/api/start" || path === "/api/end")) {
                const raw = await req.text();
                const sigOk = await verifySignature(req, env, raw);
                if (!sigOk) return err(401, "bad signature");
                if (path === "/api/announce") return await handleAnnounce(req, env, raw);
                if (path === "/api/start") return await handleLifecycle(env, raw, "start");
                return await handleLifecycle(env, raw, "end");
            }

            return err(404, "not found");
        } catch (e) {
            return err(500, `internal error: ${(e as Error).message ?? "unknown"}`);
        }
    },
} satisfies ExportedHandler<Env>;

// ─── signature ─────────────────────────────────────────────────────────────────

async function verifySignature(req: Request, env: Env, body: string): Promise<boolean> {
    const sigHeader = (req.headers.get("x-signature") ?? "").toLowerCase();
    const tsHeader = req.headers.get("x-timestamp") ?? "";
    if (!HEX_SIG_RE.test(sigHeader)) return false;
    const ts = Number(tsHeader);
    if (!Number.isFinite(ts)) return false;
    const now = Math.floor(Date.now() / 1000);
    if (Math.abs(now - ts) > SIGNATURE_SKEW_SECONDS) return false;
    if (!env.SHARED_HMAC_KEY) return false;

    const enc = new TextEncoder();
    const key = await crypto.subtle.importKey(
        "raw",
        enc.encode(env.SHARED_HMAC_KEY),
        { name: "HMAC", hash: "SHA-256" },
        false,
        ["sign"],
    );
    const macBuf = await crypto.subtle.sign("HMAC", key, enc.encode(`${tsHeader}.${body}`));
    const expected = toHex(new Uint8Array(macBuf));
    return timingSafeEqual(sigHeader, expected);
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

    if (await env.STATE.get(`deny:fc:${a.fcHash}`)) {
        // Silent-ack so a banned host can't probe the denylist.
        return ok({ status: "ignored" });
    }

    const ip = req.headers.get("cf-connecting-ip") ?? "unknown";
    const rateLimitSec = num(env.RATE_LIMIT_SECONDS, 60);
    if (await env.STATE.get(`rl:ip:${ip}`)) return err(429, "rate limited (ip)");
    if (await env.STATE.get(`rl:fc:${a.fcHash}`)) return err(429, "rate limited (host)");

    const dedupSec = num(env.DEDUP_WINDOW_SECONDS, 30);
    const dedupKey = `dedup:${a.code}:${a.fcHash}`;
    if (await env.STATE.get(dedupKey)) {
        const existing = await env.STATE.get(`code:${a.code}`, "json") as CodeEntry | null;
        if (existing) return ok({ status: "dedup", messageId: existing.messageId });
    }

    const existing = await env.STATE.get(`code:${a.code}`, "json") as CodeEntry | null;
    if (existing && existing.fcHash !== a.fcHash) {
        return err(409, "code already announced by another host");
    }

    const publicData = stripFcHash(a);
    const sendResult = await postDiscordMessage(env.DISCORD_WEBHOOK_URL, buildEmbed(publicData, "open"));
    if (!sendResult.ok) return err(502, `discord post failed: ${sendResult.error}`);

    const entry: CodeEntry = {
        messageId: sendResult.messageId,
        fcHash: a.fcHash,
        createdAt: Date.now(),
        status: "open",
        announce: publicData,
    };

    const announceTtl = num(env.ANNOUNCE_TTL_SECONDS, 10800);
    await Promise.all([
        env.STATE.put(`code:${a.code}`, JSON.stringify(entry), { expirationTtl: announceTtl }),
        env.STATE.put(`rl:ip:${ip}`, "1", { expirationTtl: rateLimitSec }),
        env.STATE.put(`rl:fc:${a.fcHash}`, "1", { expirationTtl: rateLimitSec }),
        env.STATE.put(dedupKey, "1", { expirationTtl: dedupSec }),
    ]);

    return ok({ status: "announced", messageId: sendResult.messageId });
}

async function handleLifecycle(env: Env, raw: string, phase: "start" | "end"): Promise<Response> {
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
        const r = await editDiscordMessage(
            env.DISCORD_WEBHOOK_URL,
            entry.messageId,
            buildEmbed(entry.announce, "in-game"),
        );
        if (!r.ok) return err(502, `discord patch failed: ${r.error}`);
        entry.status = "in-game";
        const remaining = remainingTtl(entry, env);
        await env.STATE.put(`code:${code}`, JSON.stringify(entry), { expirationTtl: remaining });
        return ok({ status: "started" });
    } else {
        const r = await deleteDiscordMessage(env.DISCORD_WEBHOOK_URL, entry.messageId);
        await env.STATE.delete(`code:${code}`);
        if (!r.ok) return ok({ status: "kv-cleared", warn: r.error });
        return ok({ status: "ended" });
    }
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
    const r = await fetch(`${webhook}/messages/${messageId}`, { method: "DELETE" });
    if (!r.ok && r.status !== 404) return { ok: false, error: `${r.status} ${await safeText(r)}` };
    return { ok: true };
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

function num(v: string | undefined, fallback: number): number {
    const n = Number(v);
    return Number.isFinite(n) && n > 0 ? n : fallback;
}

function remainingTtl(entry: CodeEntry, env: Env): number {
    const announceTtl = num(env.ANNOUNCE_TTL_SECONDS, 10800);
    const elapsed = Math.floor((Date.now() - entry.createdAt) / 1000);
    return Math.max(60, announceTtl - elapsed);
}

function addQuery(url: string, key: string, value: string): string {
    return url + (url.includes("?") ? "&" : "?") + `${encodeURIComponent(key)}=${encodeURIComponent(value)}`;
}
