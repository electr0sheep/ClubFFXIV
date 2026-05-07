import { Env } from "./types";

const MAX_URL_LEN = 512;
const PROBE_TIMEOUT_MS = 4000;
const MAX_REDIRECT_HOPS = 3;

const CACHE_PREFIX = "urlcheck:";
const CACHE_TTL_OK = 7 * 24 * 60 * 60;
// Failures get a short TTL so a transient outage on a legit Icecast host
// doesn't lock it out for a week. Long enough to absorb a publish retry storm,
// short enough that the DJ can republish after fixing their server.
const CACHE_TTL_FAIL = 5 * 60;

const BLOCKED_HOSTS = new Set([
  "localhost",
  "ip6-localhost",
  "ip6-loopback",
  "broadcasthost",
]);

const AUDIO_MIME_EXACT = new Set([
  "application/ogg",
  "application/vnd.apple.mpegurl",
  "application/x-mpegurl",
  "application/dash+xml",
]);

// Hosts whose pages serve text/html and rely on the plugin's client-side
// yt-dlp extractor to find the actual audio stream. Probing them with HEAD
// would always fail the audio-content-type check. Mirror of UrlClassifier's
// YtDlpHostsPattern in the plugin (ClubFFXIV/Audio/UrlClassifier.cs) — keep
// these two lists in sync.
const YT_DLP_HOSTS: readonly string[] = [
  "twitch.tv",
  "youtube.com",
  "youtu.be",
  "soundcloud.com",
  "mixcloud.com",
  "nicovideo.jp",
  "twitcasting.tv",
];

function isYtDlpHost(host: string): boolean {
  const h = host.toLowerCase();
  for (const allowed of YT_DLP_HOSTS) {
    if (h === allowed || h.endsWith("." + allowed)) return true;
  }
  return false;
}

interface CachedVerdict {
  ok: boolean;
  reason?: string;
  ts: number;
}

export type ValidationResult = { ok: true } | { ok: false; reason: string };

/// Cheap, pure syntax checks: scheme, length, credentials, private/loopback
/// literals. Runs on every redirect hop too — a redirect to 127.0.0.1 is just
/// as bad as a publish that points there directly.
export function validateStreamUrlSyntax(s: unknown): string | null {
  const parsed = parseAndCheckSyntax(s);
  return "error" in parsed ? parsed.error : null;
}

type SyntaxParseResult = { url: URL } | { error: string };

function parseAndCheckSyntax(s: unknown): SyntaxParseResult {
  if (typeof s !== "string") return { error: "streamUrl must be a string" };
  if (s.length === 0) return { error: "streamUrl cannot be empty" };
  if (s.length > MAX_URL_LEN) return { error: `streamUrl too long (max ${MAX_URL_LEN})` };

  let u: URL;
  try {
    u = new URL(s);
  } catch {
    return { error: "streamUrl is not a valid URL" };
  }

  if (u.protocol !== "http:" && u.protocol !== "https:") {
    return { error: "streamUrl must be http or https" };
  }
  if (u.username !== "" || u.password !== "") {
    return { error: "streamUrl cannot contain credentials" };
  }

  const host = normalizeHost(u.hostname);
  if (host.length === 0) return { error: "streamUrl host is empty" };
  if (BLOCKED_HOSTS.has(host)) {
    return { error: "streamUrl host is not allowed (loopback)" };
  }
  if (isIPv4Literal(host) && isPrivateIPv4(host)) {
    return { error: "streamUrl host is not allowed (private/loopback IPv4)" };
  }
  if (isIPv6Literal(host) && isPrivateIPv6(host)) {
    return { error: "streamUrl host is not allowed (private/loopback IPv6)" };
  }

  return { url: u };
}

/// Full validation: syntax + KV-cached HEAD probe (with range-GET fallback for
/// HEAD-broken Icecast/Shoutcast servers). Cache writes are best-effort;
/// failure to cache never blocks a publish.
export async function validateStreamUrl(
  env: Env,
  url: string,
): Promise<ValidationResult> {
  const parsed = parseAndCheckSyntax(url);
  if ("error" in parsed) return { ok: false, reason: parsed.error };

  // yt-dlp-handled platforms (YouTube, Twitch, SoundCloud, etc.) serve
  // text/html at the URL listeners paste — the plugin extracts the actual
  // stream client-side. A HEAD probe would always fail the audio-content-type
  // check, so trust the host instead. Syntax + host whitelist is the bar.
  if (isYtDlpHost(parsed.url.hostname)) return { ok: true };

  const k = await cacheKey(url);
  const cachedRaw = await env.CLUBS_KV.get(k);
  if (cachedRaw) {
    try {
      const cached = JSON.parse(cachedRaw) as CachedVerdict;
      if (cached.ok) return { ok: true };
      return { ok: false, reason: cached.reason ?? "previously rejected" };
    } catch {
      // Corrupt cache entry — fall through and re-probe.
    }
  }

  const verdict = await probeUrl(url, 0);

  try {
    const value: CachedVerdict = verdict.ok
      ? { ok: true, ts: Date.now() }
      : { ok: false, reason: verdict.reason, ts: Date.now() };
    await env.CLUBS_KV.put(k, JSON.stringify(value), {
      expirationTtl: verdict.ok ? CACHE_TTL_OK : CACHE_TTL_FAIL,
    });
  } catch {
    // KV quota / transient KV failure — return the verdict without caching.
  }

  return verdict;
}

async function probeUrl(url: string, hops: number): Promise<ValidationResult> {
  if (hops > MAX_REDIRECT_HOPS) {
    return { ok: false, reason: "too many redirects" };
  }
  const syntaxErr = validateStreamUrlSyntax(url);
  if (syntaxErr) return { ok: false, reason: `redirect target rejected: ${syntaxErr}` };

  let res: Response;
  try {
    res = await fetch(url, {
      method: "HEAD",
      redirect: "manual",
      signal: AbortSignal.timeout(PROBE_TIMEOUT_MS),
    });
  } catch {
    // HEAD often fails on Shoutcast/Icecast (connection reset, malformed
    // response). Fall back to a tiny ranged GET before giving up.
    return probeWithRange(url, hops);
  }

  if (res.status >= 300 && res.status < 400) {
    return followRedirect(res, url, hops);
  }
  if (res.status === 405 || res.status === 501) {
    return probeWithRange(url, hops);
  }
  if (!res.ok) {
    return { ok: false, reason: `host returned ${res.status}` };
  }

  const verdict = inspectHeaders(res.headers);
  if (verdict !== null) return verdict;

  // 200 with no Content-Type and no icy-* signal — try ranged GET in case the
  // Content-Type only shows up on a real read.
  return probeWithRange(url, hops);
}

async function probeWithRange(url: string, hops: number): Promise<ValidationResult> {
  let res: Response;
  try {
    res = await fetch(url, {
      method: "GET",
      headers: { "Range": "bytes=0-0" },
      redirect: "manual",
      signal: AbortSignal.timeout(PROBE_TIMEOUT_MS),
    });
  } catch (e) {
    return { ok: false, reason: `unreachable: ${(e as Error).message}` };
  }

  try {
    if (res.status >= 300 && res.status < 400) {
      return followRedirect(res, url, hops);
    }
    // 206 = partial content, 416 = range not satisfiable (server doesn't
    // honor ranges but we still got headers, which is what we wanted).
    if (!res.ok && res.status !== 206 && res.status !== 416) {
      return { ok: false, reason: `host returned ${res.status}` };
    }

    const verdict = inspectHeaders(res.headers);
    if (verdict !== null) return verdict;
    return { ok: false, reason: "missing or non-audio content-type" };
  } finally {
    // Discard the byte we asked for so we don't dribble bandwidth.
    if (res.body) await res.body.cancel().catch(() => {});
  }
}

function followRedirect(
  res: Response,
  fromUrl: string,
  hops: number,
): Promise<ValidationResult> {
  const loc = res.headers.get("location");
  if (!loc) return Promise.resolve({ ok: false, reason: "redirect with no location" });
  let nextUrl: string;
  try {
    nextUrl = new URL(loc, fromUrl).toString();
  } catch {
    return Promise.resolve({ ok: false, reason: "invalid redirect location" });
  }
  return probeUrl(nextUrl, hops + 1);
}

/// Returns ok/fail if the headers are conclusive, null if we should fall back
/// to a ranged GET.
function inspectHeaders(h: Headers): ValidationResult | null {
  // Shoutcast/Icecast advertise these regardless of Content-Type. A server
  // setting any of them is unambiguously a streaming audio source.
  if (h.get("icy-name") || h.get("icy-metaint") || h.get("icy-genre")) {
    return { ok: true };
  }
  const ct = h.get("content-type");
  if (!ct) return null;
  if (isAudioContentType(ct)) return { ok: true };
  return { ok: false, reason: `content-type "${ct}" is not an audio stream` };
}

function isAudioContentType(ct: string): boolean {
  const base = ct.split(";")[0]!.trim().toLowerCase();
  if (base.startsWith("audio/")) return true;
  if (AUDIO_MIME_EXACT.has(base)) return true;
  return false;
}

async function cacheKey(url: string): Promise<string> {
  const buf = new TextEncoder().encode(url);
  const hash = await crypto.subtle.digest("SHA-256", buf);
  const bytes = new Uint8Array(hash);
  let hex = "";
  for (const b of bytes) hex += b.toString(16).padStart(2, "0");
  return CACHE_PREFIX + hex;
}

function normalizeHost(host: string): string {
  let h = host.toLowerCase();
  if (h.startsWith("[") && h.endsWith("]")) h = h.slice(1, -1);
  return h;
}

function isIPv4Literal(host: string): boolean {
  return /^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$/.test(host);
}

function isIPv6Literal(host: string): boolean {
  // Anything with a colon that isn't an IPv4-mapped-via-dots-only string. URL
  // hostnames don't contain ":" except for IPv6 literals (port lives on the
  // URL object separately).
  return host.includes(":");
}

function isPrivateIPv4(ip: string): boolean {
  const parts = ip.split(".").map((p) => Number(p));
  if (parts.length !== 4) return false;
  for (const p of parts) {
    if (!Number.isInteger(p) || p < 0 || p > 255) return false;
  }
  const [a, b] = parts as [number, number, number, number];
  if (a === 0) return true;
  if (a === 10) return true;
  if (a === 127) return true;
  if (a === 169 && b === 254) return true;
  if (a === 172 && b >= 16 && b <= 31) return true;
  if (a === 192 && b === 168) return true;
  if (a === 100 && b >= 64 && b <= 127) return true; // CGNAT
  if (a >= 224) return true; // multicast + reserved
  return false;
}

function isPrivateIPv6(host: string): boolean {
  const h = host.toLowerCase();
  if (h === "::" || h === "::1") return true;
  // ULA fc00::/7
  if (/^f[cd][0-9a-f]{2}:/.test(h)) return true;
  // Link-local fe80::/10 — first 10 bits are 1111111010, so the second nibble
  // is 8/9/a/b and the third nibble can be anything.
  if (/^fe[89ab][0-9a-f]:/.test(h)) return true;
  // IPv4-mapped: ::ffff:a.b.c.d — recurse into IPv4 check.
  const mapped = h.match(/^::ffff:(\d+\.\d+\.\d+\.\d+)$/);
  if (mapped) return isPrivateIPv4(mapped[1]!);
  return false;
}
