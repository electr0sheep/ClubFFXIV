import { djIdFromPubkey, verifySignature } from "./auth";
import {
  ClubRecord,
  DeleteBody,
  Door,
  Env,
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
    djId: record.djId,
    door: record.door,
    updatedAt: record.updatedAt,
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

  if (!parsed.streamUrl || !parsed.displayName) {
    return jsonResponse({ error: "missing streamUrl or displayName" }, 400);
  }
  if (!validateUrl(parsed.streamUrl)) {
    return jsonResponse({ error: "streamUrl must be http(s)" }, 400);
  }
  if (parsed.displayName.length > 80) {
    return jsonResponse({ error: "displayName too long" }, 400);
  }
  const nonceErr = nonceError(parsed.nonce);
  if (nonceErr) return jsonResponse({ error: nonceErr }, 400);
  const doorErr = parsed.door ? doorError(parsed.door) : null;
  if (doorErr) return jsonResponse({ error: doorErr }, 400);

  const djId = await djIdFromPubkey(auth.pubkey);

  const existingRaw = await env.CLUBS_KV.get(`club:${plotKey}`);
  let previousDoor: Door | undefined;
  if (existingRaw) {
    const existing = JSON.parse(existingRaw) as ClubRecord;
    if (existing.djId !== djId) {
      return jsonResponse({ error: "plot owned by another DJ" }, 403);
    }
    previousDoor = existing.door;
  }

  const record: ClubRecord = {
    streamUrl: parsed.streamUrl,
    displayName: parsed.displayName,
    djId,
    pubkey: auth.pubkey,
    updatedAt: Date.now(),
    door: parsed.door,
  };
  await env.CLUBS_KV.put(`club:${plotKey}`, JSON.stringify(record));

  // Maintain ward index. If the door moved between wards, remove from the old.
  const worldId = extractWorldId(plotKey);
  if (worldId !== null) {
    if (previousDoor && doorMovedWard(previousDoor, parsed.door)) {
      await removeFromWardIndex(env, worldId, previousDoor, plotKey);
    }
    if (parsed.door) {
      await addToWardIndex(env, worldId, parsed.door, plotKey, {
        streamUrl: parsed.streamUrl,
        displayName: parsed.displayName,
        djId,
        door: parsed.door,
        updatedAt: record.updatedAt,
      });
    }
  }

  return jsonResponse({ ok: true, djId });
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

  await env.CLUBS_KV.delete(`club:${plotKey}`);

  if (existing.door) {
    const worldId = extractWorldId(plotKey);
    if (worldId !== null) {
      await removeFromWardIndex(env, worldId, existing.door, plotKey);
    }
  }

  return jsonResponse({ ok: true });
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
  await env.CLUBS_KV.put(k, JSON.stringify(index));
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
    await env.CLUBS_KV.delete(k);
  } else {
    await env.CLUBS_KV.put(k, JSON.stringify(index));
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

function doorError(d: Door): string | null {
  for (const k of ["x", "y", "z", "territoryType", "ward"] as const) {
    const v = (d as unknown as Record<string, unknown>)[k];
    if (typeof v !== "number" || !Number.isFinite(v)) return `door.${k} must be a finite number`;
  }
  return null;
}

function validateUrl(s: string): boolean {
  try {
    const u = new URL(s);
    return u.protocol === "http:" || u.protocol === "https:";
  } catch {
    return false;
  }
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
