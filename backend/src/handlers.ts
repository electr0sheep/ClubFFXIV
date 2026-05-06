import { djIdFromPubkey, verifySignature } from "./auth";
import { validateStreamUrl, validateStreamUrlSyntax } from "./streamUrlValidation";

/// Thrown by safePut / safeDelete when the underlying KV write fails with a
/// quota / rate-limit signature. Caught at the router so we can return a
/// structured 503 with a Retry-After hint instead of leaking the raw KV
/// error string to the client.
export class QuotaExhaustedError extends Error {
  constructor(public retryAfterSeconds: number, message?: string) {
    super(message ?? "Registry is temporarily at capacity — please try again later.");
    this.name = "QuotaExhaustedError";
  }
}

// Cloudflare KV throws different messages depending on which limit you hit
// (account-wide daily writes on free tier, per-key 1/sec, etc.). Match the
// common substrings rather than relying on a single stable error code.
function isKvQuotaError(err: unknown): boolean {
  const msg = (err instanceof Error ? err.message : String(err)).toLowerCase();
  return (
    msg.includes("daily limit") ||
    msg.includes("rate limit") ||
    msg.includes("quota") ||
    msg.includes("429") ||
    msg.includes("too many requests") ||
    msg.includes("kv put failed") ||
    msg.includes("kv delete failed")
  );
}

// Best-effort retry estimate. Free-tier KV write quotas reset at 00:00 UTC,
// so on a free-tier deployment this is roughly correct. On the paid plan
// you'd hit the per-second / per-namespace bursty limits much earlier and
// "wait until midnight" is overly pessimistic — but a too-long retry hint
// is harmless (the user just retries earlier and either succeeds or sees
// the same message again).
function estimateQuotaRetrySeconds(): number {
  const now = new Date();
  const tomorrowMidnightUtc = new Date(Date.UTC(
    now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() + 1,
    0, 0, 0, 0,
  ));
  return Math.floor((tomorrowMidnightUtc.getTime() - now.getTime()) / 1000);
}

async function safePut(env: Env, key: string, value: string): Promise<void> {
  try {
    await env.CLUBS_KV.put(key, value);
  } catch (e) {
    if (isKvQuotaError(e)) throw new QuotaExhaustedError(estimateQuotaRetrySeconds());
    throw e;
  }
}

async function safeDelete(env: Env, key: string): Promise<void> {
  try {
    await env.CLUBS_KV.delete(key);
  } catch (e) {
    if (isKvQuotaError(e)) throw new QuotaExhaustedError(estimateQuotaRetrySeconds());
    throw e;
  }
}
import {
  ClubRecord,
  DeleteBody,
  DIRECTORY_KEY,
  DirectoryEntry,
  DirectoryIndex,
  Door,
  Env,
  MAX_DESCRIPTION_LEN,
  NONCE_MAX_AGE_MS,
  PublishBody,
  WardIndex,
  WardIndexEntry,
} from "./types";

export async function handleGet(
  env: Env,
  plotKey: string,
): Promise<Response> {
  const raw = await env.CLUBS_KV.get(`club:${plotKey}`);
  if (!raw) return jsonResponse({ error: "not found" }, 404);
  const record = JSON.parse(raw) as ClubRecord;
  return jsonResponse({
    streamUrl: record.streamUrl,
    displayName: record.displayName,
    description: record.description ?? "",
    djId: record.djId,
    door: record.door,
    updatedAt: record.updatedAt,
    listed: record.listed !== false,
  });
}

export async function handlePost(
  req: Request,
  env: Env,
  plotKey: string,
): Promise<Response> {
  const auth = readAuthHeaders(req);
  if (!auth) return jsonResponse({ error: "missing auth headers" }, 401);

  const body = await req.text();
  const sigOk = await verifySignature(
    `POST:${plotKey}:${body}`,
    auth.signature,
    auth.pubkey,
  );
  if (!sigOk) return jsonResponse({ error: "bad signature" }, 401);

  let parsed: PublishBody;
  try {
    parsed = JSON.parse(body) as PublishBody;
  } catch {
    return jsonResponse({ error: "invalid json" }, 400);
  }

  if (!parsed.streamUrl || parsed.displayName === undefined) {
    return jsonResponse({ error: "missing streamUrl or displayName" }, 400);
  }
  const syntaxErr = validateStreamUrlSyntax(parsed.streamUrl);
  if (syntaxErr) {
    return jsonResponse({ error: syntaxErr }, 400);
  }

  const dnResult = sanitizeDisplayName(parsed.displayName);
  if (!dnResult.ok) return jsonResponse({ error: dnResult.error }, 400);
  const displayName = dnResult.value;

  const descResult = sanitizeDescription(parsed.description);
  if (!descResult.ok) return jsonResponse({ error: descResult.error }, 400);
  const description = descResult.value;

  const nonceErr = nonceError(parsed.nonce);
  if (nonceErr) return jsonResponse({ error: nonceErr }, 400);
  const doorErr = parsed.door ? doorError(parsed.door) : null;
  if (doorErr) return jsonResponse({ error: doorErr }, 400);

  const djId = await djIdFromPubkey(auth.pubkey);

  const existingRaw = await env.CLUBS_KV.get(`club:${plotKey}`);
  let previousDoor: Door | undefined;
  let previousStreamUrl: string | undefined;
  if (existingRaw) {
    const existing = JSON.parse(existingRaw) as ClubRecord;
    if (existing.djId !== djId) {
      return jsonResponse({ error: "plot owned by another DJ" }, 403);
    }
    previousDoor = existing.door;
    previousStreamUrl = existing.streamUrl;
  }

  // Probe the stream URL last — after all cheap validations and the ownership
  // check pass — so an unauthorized or malformed publish never triggers a
  // network call. Re-publishes that don't change the URL skip the probe; the
  // cache covers explicit URL changes within its TTL window.
  if (parsed.streamUrl !== previousStreamUrl) {
    const urlCheck = await validateStreamUrl(env, parsed.streamUrl);
    if (!urlCheck.ok) {
      return jsonResponse({ error: `streamUrl rejected: ${urlCheck.reason}` }, 400);
    }
  }

  // Default to listed for back-compat: pre-listed-flag clients sent no `listed`
  // field and expected to appear in any future browse list.
  const listed = parsed.listed !== false;

  const record: ClubRecord = {
    streamUrl: parsed.streamUrl,
    displayName,
    description,
    djId,
    pubkey: auth.pubkey,
    updatedAt: Date.now(),
    door: parsed.door,
    listed,
  };
  await safePut(env, `club:${plotKey}`, JSON.stringify(record));

  // Maintain ward index. If the door moved between wards, remove from the old.
  const worldId = extractWorldId(plotKey);
  if (worldId !== null) {
    if (previousDoor && doorMovedWard(previousDoor, parsed.door)) {
      await removeFromWardIndex(env, worldId, previousDoor, plotKey);
    }
    if (parsed.door) {
      await addToWardIndex(env, worldId, parsed.door, plotKey, {
        streamUrl: parsed.streamUrl,
        displayName,
        description,
        djId,
        door: parsed.door,
        updatedAt: record.updatedAt,
      });
    }
  }

  // Maintain the public directory index. The ward index always tracks
  // calibrated clubs (so spatial proximity discovery is unaffected by `listed`);
  // the directory is purely the opt-in browse list.
  if (listed) {
    await addToDirectory(env, plotKey, {
      streamUrl: parsed.streamUrl,
      displayName,
      description,
      djId,
      door: parsed.door,
      updatedAt: record.updatedAt,
    });
  } else {
    await removeFromDirectory(env, plotKey);
  }

  return jsonResponse({ ok: true, djId, listed });
}

export async function handleDelete(
  req: Request,
  env: Env,
  plotKey: string,
): Promise<Response> {
  const auth = readAuthHeaders(req);
  if (!auth) return jsonResponse({ error: "missing auth headers" }, 401);

  const body = await req.text();
  const sigOk = await verifySignature(
    `DELETE:${plotKey}:${body}`,
    auth.signature,
    auth.pubkey,
  );
  if (!sigOk) return jsonResponse({ error: "bad signature" }, 401);

  let parsed: DeleteBody;
  try {
    parsed = JSON.parse(body || "{}") as DeleteBody;
  } catch {
    return jsonResponse({ error: "invalid json" }, 400);
  }
  const nonceErr = nonceError(parsed.nonce);
  if (nonceErr) return jsonResponse({ error: nonceErr }, 400);

  const existingRaw = await env.CLUBS_KV.get(`club:${plotKey}`);
  if (!existingRaw) return jsonResponse({ error: "not found" }, 404);

  const existing = JSON.parse(existingRaw) as ClubRecord;
  const djId = await djIdFromPubkey(auth.pubkey);
  if (existing.djId !== djId) {
    return jsonResponse({ error: "plot owned by another DJ" }, 403);
  }

  await safeDelete(env, `club:${plotKey}`);

  if (existing.door) {
    const worldId = extractWorldId(plotKey);
    if (worldId !== null) {
      await removeFromWardIndex(env, worldId, existing.door, plotKey);
    }
  }

  await removeFromDirectory(env, plotKey);

  return jsonResponse({ ok: true });
}

export async function handleDirectoryListing(env: Env): Promise<Response> {
  const raw = await env.CLUBS_KV.get(DIRECTORY_KEY);
  const index = (raw ? (JSON.parse(raw) as DirectoryIndex) : {}) as DirectoryIndex;
  return jsonResponse({
    clubs: Object.entries(index).map(([plotKey, e]) => ({ plotKey, ...e })),
  });
}

export async function handleWardListing(
  env: Env,
  worldId: number,
  territoryType: number,
  ward: number,
): Promise<Response> {
  const raw = await env.CLUBS_KV.get(wardKey(worldId, territoryType, ward));
  const index = (raw ? (JSON.parse(raw) as WardIndex) : {}) as WardIndex;
  return jsonResponse({
    worldId,
    territoryType,
    ward,
    clubs: Object.entries(index).map(([plotKey, e]) => ({ plotKey, ...e })),
  });
}

async function addToWardIndex(
  env: Env,
  worldId: number,
  door: Door,
  plotKey: string,
  entry: WardIndexEntry,
): Promise<void> {
  const k = wardKey(worldId, door.territoryType, door.ward);
  const raw = await env.CLUBS_KV.get(k);
  const index: WardIndex = raw ? (JSON.parse(raw) as WardIndex) : {};
  index[plotKey] = entry;
  await safePut(env, k, JSON.stringify(index));
}

async function removeFromWardIndex(
  env: Env,
  worldId: number,
  door: Door,
  plotKey: string,
): Promise<void> {
  const k = wardKey(worldId, door.territoryType, door.ward);
  const raw = await env.CLUBS_KV.get(k);
  if (!raw) return;
  const index = JSON.parse(raw) as WardIndex;
  if (!(plotKey in index)) return;
  delete index[plotKey];
  if (Object.keys(index).length === 0) {
    await safeDelete(env, k);
  } else {
    await safePut(env, k, JSON.stringify(index));
  }
}

async function addToDirectory(
  env: Env,
  plotKey: string,
  entry: DirectoryEntry,
): Promise<void> {
  const raw = await env.CLUBS_KV.get(DIRECTORY_KEY);
  const index: DirectoryIndex = raw ? (JSON.parse(raw) as DirectoryIndex) : {};
  index[plotKey] = entry;
  await safePut(env, DIRECTORY_KEY, JSON.stringify(index));
}

async function removeFromDirectory(
  env: Env,
  plotKey: string,
): Promise<void> {
  const raw = await env.CLUBS_KV.get(DIRECTORY_KEY);
  if (!raw) return;
  const index = JSON.parse(raw) as DirectoryIndex;
  if (!(plotKey in index)) return;
  delete index[plotKey];
  if (Object.keys(index).length === 0) {
    await safeDelete(env, DIRECTORY_KEY);
  } else {
    await safePut(env, DIRECTORY_KEY, JSON.stringify(index));
  }
}

function wardKey(worldId: number, territoryType: number, ward: number): string {
  return `ward:${worldId}:${territoryType}:${ward}`;
}

function doorMovedWard(prev: Door | undefined, next: Door | undefined): boolean {
  if (!prev || !next) return true;
  return prev.territoryType !== next.territoryType || prev.ward !== next.ward;
}

function extractWorldId(plotKey: string): number | null {
  // plotKey format: worldId:territoryType:ward:plot:room:division
  const first = plotKey.split(":")[0];
  if (!first) return null;
  const n = Number(first);
  return Number.isFinite(n) ? n : null;
}

function readAuthHeaders(
  req: Request,
): { pubkey: string; signature: string } | null {
  const pubkey = req.headers.get("x-pubkey");
  const signature = req.headers.get("x-signature");
  if (!pubkey || !signature) return null;
  return { pubkey, signature };
}

function nonceError(nonce: number | undefined): string | null {
  if (typeof nonce !== "number" || !Number.isFinite(nonce)) return "missing or invalid nonce";
  const serverNow = Date.now();
  const age = serverNow - nonce;
  if (Math.abs(age) > NONCE_MAX_AGE_MS) {
    const dir = age < 0 ? "ahead" : "behind";
    return `clock skew: client is ${dir} by ${Math.round(Math.abs(age) / 1000)}s. server=${serverNow} client=${nonce}`;
  }
  return null;
}

// Visual-spoofing chars: bidi formatters / overrides. A club name with an
// explicit RTL override is the most cited prompt-spoofing trick; reject the
// whole class outright.
// U+202A–U+202E: embedding / override + pop. U+2066–U+2069: isolates + pop.
const BIDI_FORMATTING_RE = /[\u202A-\u202E\u2066-\u2069]/;
// C0 / C1 control chars. \t (0x09) is rejected too — display-name fields are
// single-line and tabs only confuse layout. \n (0x0A) is allowed only in
// description (see CONTROL_EXCEPT_LF_RE).
const CONTROL_CHARS_RE = /[\x00-\x1F\x7F-\x9F]/;
const CONTROL_EXCEPT_LF_RE = /[\x00-\x09\x0B-\x1F\x7F-\x9F]/;

type SanitizeResult = { ok: true; value: string } | { ok: false; error: string };

function sanitizeDisplayName(input: unknown): SanitizeResult {
  if (typeof input !== "string") {
    return { ok: false, error: "displayName must be a string" };
  }
  const trimmed = input.trim();
  if (trimmed.length === 0) {
    return { ok: false, error: "displayName cannot be empty" };
  }
  if (trimmed.length > 80) {
    return { ok: false, error: "displayName too long (max 80)" };
  }
  if (CONTROL_CHARS_RE.test(trimmed)) {
    return { ok: false, error: "displayName cannot contain control characters or line breaks" };
  }
  if (BIDI_FORMATTING_RE.test(trimmed)) {
    return { ok: false, error: "displayName cannot contain bidi-override characters" };
  }
  return { ok: true, value: trimmed };
}

function sanitizeDescription(input: unknown): SanitizeResult {
  if (input === undefined || input === null) {
    return { ok: true, value: "" };
  }
  if (typeof input !== "string") {
    return { ok: false, error: "description must be a string" };
  }
  // Normalize line endings to LF so a Windows-pasted description doesn't blow
  // through the length cap or surprise the regex below.
  const normalized = input.replace(/\r\n/g, "\n").replace(/\r/g, "\n");
  const trimmed = normalized.trim();
  if (trimmed.length > MAX_DESCRIPTION_LEN) {
    return { ok: false, error: `description too long (max ${MAX_DESCRIPTION_LEN})` };
  }
  if (CONTROL_EXCEPT_LF_RE.test(trimmed)) {
    return { ok: false, error: "description cannot contain control characters" };
  }
  if (BIDI_FORMATTING_RE.test(trimmed)) {
    return { ok: false, error: "description cannot contain bidi-override characters" };
  }
  return { ok: true, value: trimmed };
}

function doorError(d: Door): string | null {
  for (const k of ["x", "y", "z", "territoryType", "ward"] as const) {
    const v = (d as unknown as Record<string, unknown>)[k];
    if (typeof v !== "number" || !Number.isFinite(v)) return `door.${k} must be a finite number`;
  }
  return null;
}

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json", ...corsHeaders() },
  });
}

export function corsHeaders(): Record<string, string> {
  return {
    "access-control-allow-origin": "*",
    "access-control-allow-methods": "GET,POST,DELETE,OPTIONS",
    "access-control-allow-headers": "content-type,x-pubkey,x-signature",
    "access-control-max-age": "86400",
  };
}
