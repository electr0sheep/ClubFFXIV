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
  CURRENT_SCHEMA_VERSION,
  DeleteBody,
  DIRECTORY_KEY,
  DirectoryEntry,
  DirectoryIndex,
  Door,
  ENC_NONCE_LEN,
  ENC_URL_MAX_LEN,
  ENC_URL_MIN_LEN,
  Env,
  MAX_DESCRIPTION_LEN,
  NONCE_MAX_AGE_MS,
  PublishBody,
  WardIndex,
  WardIndexEntry,
} from "./types";

export async function handleGet(
  req: Request,
  env: Env,
  plotKey: string,
): Promise<Response> {
  const raw = await env.CLUBS_KV.get(`club:${plotKey}`);
  if (!raw) return jsonResponse({ error: "not found" }, 404);
  const record = JSON.parse(raw) as ClubRecord;

  // v2 encrypted-URL records: encryption *is* the access control, so the
  // ciphertext is served to anyone who asks. Legacy records (passwordHash
  // present, no encUrl) still go through the server-side hash gate below.
  const isV2Encrypted = !!record.encUrl;

  if (record.passwordHash && !isV2Encrypted) {
    const provided = new URL(req.url).searchParams.get("passwordHash");
    if (provided !== record.passwordHash) {
      return jsonResponse(
        {
          streamUrl: "",
          displayName: record.displayName,
          description: record.description ?? "",
          djId: record.djId,
          door: record.door,
          updatedAt: record.updatedAt,
          listed: record.listed !== false,
          passwordRequired: true,
          passwordSalt: record.passwordSalt,
        },
        provided === null ? 200 : 401,
      );
    }
  }

  return jsonResponse({
    streamUrl: record.streamUrl,
    displayName: record.displayName,
    description: record.description ?? "",
    djId: record.djId,
    door: record.door,
    updatedAt: record.updatedAt,
    listed: record.listed !== false,
    // Either gating mechanism (legacy hash or v2 ciphertext) signals
    // password-required to the listener; clients that don't yet know
    // about encUrl still see the flag and prompt accordingly.
    passwordRequired: !!record.passwordSalt,
    // Salt is fine to expose — it's bound to a high-entropy passphrase that
    // the registry never sees in plaintext.
    passwordSalt: record.passwordSalt,
    // v2 fields. Absent on legacy records; clients fall back to the
    // passwordHash flow when they see passwordRequired but no encUrl.
    encUrl: record.encUrl,
    encNonce: record.encNonce,
    schemaVersion: record.schemaVersion,
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

  // Detect the password mode up front so we can correctly interpret the
  // streamUrl field. v2 encrypted publishes legitimately carry an empty
  // streamUrl (the URL travels in encUrl); reject empty URLs only for the
  // unprotected and legacy paths where the registry expects a plaintext URL.
  const passwordModeErr = passwordFieldsError(parsed);
  if (passwordModeErr) return jsonResponse({ error: passwordModeErr }, 400);
  const isV2Encrypted = !!parsed.encUrl && !!parsed.encNonce;

  if (parsed.displayName === undefined) {
    return jsonResponse({ error: "missing displayName" }, 400);
  }
  if (!isV2Encrypted) {
    if (!parsed.streamUrl) {
      return jsonResponse({ error: "missing streamUrl" }, 400);
    }
    const syntaxErr = validateStreamUrlSyntax(parsed.streamUrl);
    if (syntaxErr) {
      return jsonResponse({ error: syntaxErr }, 400);
    }
  } else if (parsed.streamUrl) {
    // Defense-in-depth: an encrypted publish with a plaintext URL alongside
    // would silently leak the URL into the record. Reject the contradiction
    // rather than guessing which one the client meant.
    return jsonResponse(
      { error: "streamUrl must be empty for encrypted publishes" },
      400,
    );
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
  // cache covers explicit URL changes within its TTL window. Encrypted
  // publishes skip the probe entirely: the registry can't see the URL, so
  // there's nothing to probe — the plugin runs its own preflight syntax
  // check (StreamUrlValidator) before encrypting.
  if (!isV2Encrypted && parsed.streamUrl !== previousStreamUrl) {
    const urlCheck = await validateStreamUrl(env, parsed.streamUrl);
    if (!urlCheck.ok) {
      return jsonResponse({ error: `streamUrl rejected: ${urlCheck.reason}` }, 400);
    }
  }

  // Default to listed for back-compat: pre-listed-flag clients sent no `listed`
  // field and expected to appear in any future browse list.
  const listed = parsed.listed !== false;

  const passwordRequired = !!parsed.passwordSalt;

  const record: ClubRecord = {
    // v2-encrypted records never carry the plaintext URL; the registry
    // physically cannot leak what it doesn't store. Legacy records keep the
    // plaintext URL gated behind the hash check on GET.
    streamUrl: isV2Encrypted ? "" : parsed.streamUrl,
    displayName,
    description,
    djId,
    pubkey: auth.pubkey,
    updatedAt: Date.now(),
    door: parsed.door,
    listed,
    passwordSalt: parsed.passwordSalt,
    // Only one of (passwordHash) or (encUrl + encNonce) is set per record;
    // passwordFieldsError above enforces that. The unset branch produces
    // `undefined`, which JSON.stringify drops, so legacy fields don't bloat
    // v2 records and vice versa.
    passwordHash: isV2Encrypted ? undefined : parsed.passwordHash,
    encUrl: isV2Encrypted ? parsed.encUrl : undefined,
    encNonce: isV2Encrypted ? parsed.encNonce : undefined,
    schemaVersion: isV2Encrypted ? CURRENT_SCHEMA_VERSION : undefined,
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
        // Don't store the real URL on password-protected entries — anyone
        // walking past the ward fetches the index, and the URL would be
        // visible to listeners who haven't authenticated. The per-club GET
        // is the only place the URL is released, gated on the hash check.
        streamUrl: passwordRequired ? "" : parsed.streamUrl,
        displayName,
        description,
        djId,
        door: parsed.door,
        updatedAt: record.updatedAt,
        passwordRequired: passwordRequired || undefined,
      });
    }
  }

  // Maintain the public directory index. The ward index always tracks
  // calibrated clubs (so spatial proximity discovery is unaffected by `listed`);
  // the directory is purely the opt-in browse list.
  if (listed) {
    await addToDirectory(env, plotKey, {
      // Same logic as the ward index — password-protected URLs aren't in the
      // directory blob; listeners must hit the per-club GET with the hash.
      streamUrl: passwordRequired ? "" : parsed.streamUrl,
      displayName,
      description,
      djId,
      door: parsed.door,
      updatedAt: record.updatedAt,
      passwordRequired: passwordRequired || undefined,
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

// Argon2id base64 length sanity bounds. 16-byte salt → 24 base64 chars,
// 32-byte hash → 44 base64 chars. Generous upper bounds (256) catch obvious
// overflows / typos without locking us into a specific PHC parameter set.
const SALT_MIN_B64 = 16;
const SALT_MAX_B64 = 256;
const HASH_MIN_B64 = 32;
const HASH_MAX_B64 = 256;
const BASE64_RE = /^[A-Za-z0-9+/=]+$/;

function isPresent(s: string | undefined): boolean {
  return typeof s === "string" && s.length > 0;
}

/// Returns a malformed-body error string, or null when the body sits in
/// exactly one of the three valid password modes:
///   - Unprotected: all four password fields absent.
///   - Legacy: passwordSalt + passwordHash; no encUrl/encNonce. Still
///     accepted on POST so older plugin builds keep publishing until they
///     upgrade.
///   - v2 encrypted: passwordSalt + encUrl + encNonce + schemaVersion=2;
///     no passwordHash.
/// Anything else (mixed fields, partial pairs, wrong schemaVersion) is
/// rejected with a precise reason so the caller can surface it.
function passwordFieldsError(b: PublishBody): string | null {
  const haveSalt = isPresent(b.passwordSalt);
  const haveHash = isPresent(b.passwordHash);
  const haveEncUrl = isPresent(b.encUrl);
  const haveEncNonce = isPresent(b.encNonce);
  const haveAnyV2 = haveEncUrl || haveEncNonce;

  if (haveHash && haveAnyV2) {
    return "passwordHash and encUrl cannot both be set";
  }

  if (haveAnyV2) {
    if (!haveSalt) return "passwordSalt required when encUrl/encNonce set";
    if (!haveEncUrl || !haveEncNonce) {
      return "encUrl and encNonce must both be present";
    }
    if (b.schemaVersion !== CURRENT_SCHEMA_VERSION) {
      return `schemaVersion must be ${CURRENT_SCHEMA_VERSION} for encrypted publishes`;
    }
    if (b.passwordSalt!.length < SALT_MIN_B64 || b.passwordSalt!.length > SALT_MAX_B64) {
      return "passwordSalt out of range";
    }
    if (!BASE64_RE.test(b.passwordSalt!)) return "passwordSalt must be base64";
    if (b.encUrl!.length < ENC_URL_MIN_LEN || b.encUrl!.length > ENC_URL_MAX_LEN) {
      return "encUrl out of range";
    }
    if (!BASE64_RE.test(b.encUrl!)) return "encUrl must be base64";
    if (b.encNonce!.length !== ENC_NONCE_LEN) return "encNonce wrong length";
    if (!BASE64_RE.test(b.encNonce!)) return "encNonce must be base64";
    return null;
  }

  if (haveSalt || haveHash) {
    if (!haveSalt || !haveHash) {
      return "passwordSalt and passwordHash must both be present or both absent";
    }
    if (b.schemaVersion !== undefined && b.schemaVersion !== 1) {
      return "legacy passwordHash records use schemaVersion 1 (or omit it)";
    }
    if (b.passwordSalt!.length < SALT_MIN_B64 || b.passwordSalt!.length > SALT_MAX_B64) {
      return "passwordSalt out of range";
    }
    if (b.passwordHash!.length < HASH_MIN_B64 || b.passwordHash!.length > HASH_MAX_B64) {
      return "passwordHash out of range";
    }
    if (!BASE64_RE.test(b.passwordSalt!)) return "passwordSalt must be base64";
    if (!BASE64_RE.test(b.passwordHash!)) return "passwordHash must be base64";
    return null;
  }

  // Unprotected.
  if (b.schemaVersion !== undefined) {
    return "schemaVersion is only meaningful for password-protected publishes";
  }
  return null;
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
