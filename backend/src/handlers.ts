import { djIdFromPubkey, verifySignature } from "./auth";
import {
  ClubRecord,
  DeleteBody,
  Env,
  NONCE_MAX_AGE_MS,
  PublishBody,
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

  const djId = await djIdFromPubkey(auth.pubkey);

  const existingRaw = await env.CLUBS_KV.get(`club:${plotKey}`);
  if (existingRaw) {
    const existing = JSON.parse(existingRaw) as ClubRecord;
    if (existing.djId !== djId) {
      return jsonResponse({ error: "plot owned by another DJ" }, 403);
    }
  }

  const record: ClubRecord = {
    streamUrl: parsed.streamUrl,
    displayName: parsed.displayName,
    djId,
    pubkey: auth.pubkey,
    updatedAt: Date.now(),
  };
  await env.CLUBS_KV.put(`club:${plotKey}`, JSON.stringify(record));
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
  return jsonResponse({ ok: true });
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
  if (age < 0) {
    return `clock skew: client clock is ahead by ${Math.round(-age / 1000)}s. server=${serverNow} client=${nonce}`;
  }
  if (age > NONCE_MAX_AGE_MS) {
    return `clock skew: client clock is behind by ${Math.round(age / 1000)}s. server=${serverNow} client=${nonce}`;
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
