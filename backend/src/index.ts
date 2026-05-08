import {
  corsHeaders,
  handleDelete,
  handleDirectoryListing,
  handleGet,
  handlePost,
  handleWardListing,
  QuotaExhaustedError,
} from "./handlers";
import { Env } from "./types";

export default {
  async fetch(req: Request, env: Env): Promise<Response> {
    const url = new URL(req.url);

    if (req.method === "OPTIONS") {
      return new Response(null, { status: 204, headers: corsHeaders() });
    }

    if (url.pathname === "/health") {
      return new Response("ok", { status: 200, headers: corsHeaders() });
    }

    if (url.pathname === "/time") {
      return jsonResponse({ now: Date.now() });
    }

    const wardMatch = url.pathname.match(
      /^\/wards\/(\d+)\/(\d+)\/(\d+)\/?$/,
    );
    if (wardMatch && req.method === "GET") {
      const worldId = Number(wardMatch[1]);
      const territoryType = Number(wardMatch[2]);
      const ward = Number(wardMatch[3]);
      try {
        return await handleWardListing(env, worldId, territoryType, ward);
      } catch (e) {
        return errorResponse(e);
      }
    }

    // Bare /clubs is the public directory browse endpoint. Must come BEFORE
    // the /clubs/:plotKey match, since the regex below would treat "directory"
    // as a plotKey otherwise.
    if (url.pathname === "/clubs" || url.pathname === "/clubs/") {
      if (req.method !== "GET") {
        return jsonResponse({ error: "method not allowed" }, 405);
      }
      try {
        return await handleDirectoryListing(env);
      } catch (e) {
        return errorResponse(e);
      }
    }

    const clubMatch = url.pathname.match(/^\/clubs\/([^/]+)\/?$/);
    if (clubMatch) {
      const plotKey = decodeURIComponent(clubMatch[1]!);
      if (!isValidPlotKey(plotKey)) {
        return jsonResponse({ error: "invalid plot key format" }, 400);
      }
      try {
        switch (req.method) {
          case "GET":
            return await handleGet(req, env, plotKey);
          case "POST":
            return await handlePost(req, env, plotKey);
          case "DELETE":
            return await handleDelete(req, env, plotKey);
          default:
            return jsonResponse({ error: "method not allowed" }, 405);
        }
      } catch (e) {
        return errorResponse(e);
      }
    }

    return jsonResponse({ error: "not found" }, 404);
  },
};

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json", ...corsHeaders() },
  });
}

/// Convert a thrown error into a structured response. QuotaExhaustedError
/// becomes a 503 with `code: "QUOTA_EXHAUSTED"` + Retry-After so the plugin
/// can render an actionable message instead of a raw KV error string. All
/// other errors fall through to a generic 500 with the message inlined.
function errorResponse(e: unknown): Response {
  if (e instanceof QuotaExhaustedError) {
    return new Response(JSON.stringify({
      error: e.message,
      code: "QUOTA_EXHAUSTED",
      retryAfterSeconds: e.retryAfterSeconds,
    }), {
      status: 503,
      headers: {
        "content-type": "application/json",
        "retry-after": String(e.retryAfterSeconds),
        ...corsHeaders(),
      },
    });
  }
  return jsonResponse({ error: (e as Error).message }, 500);
}

// PlotKey shape from the plugin: worldId:territoryType:ward:plot:room:division.
// First two are uint, last four are signed int. Length cap (80) bounds storage
// cost from a malicious client trying to use absurdly large numeric strings.
const PLOT_KEY_RE = /^\d+:\d+:-?\d+:-?\d+:-?\d+:-?\d+$/;

function isValidPlotKey(s: string): boolean {
  return s.length > 0 && s.length <= 80 && PLOT_KEY_RE.test(s);
}
