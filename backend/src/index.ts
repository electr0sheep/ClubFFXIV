import {
  corsHeaders,
  handleDelete,
  handleGet,
  handlePost,
  handleWardListing,
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
        return jsonResponse({ error: (e as Error).message }, 500);
      }
    }

    const clubMatch = url.pathname.match(/^\/clubs\/([^/]+)\/?$/);
    if (clubMatch) {
      const plotKey = decodeURIComponent(clubMatch[1]!);
      try {
        switch (req.method) {
          case "GET":
            return await handleGet(env, plotKey);
          case "POST":
            return await handlePost(req, env, plotKey);
          case "DELETE":
            return await handleDelete(req, env, plotKey);
          default:
            return jsonResponse({ error: "method not allowed" }, 405);
        }
      } catch (e) {
        return jsonResponse({ error: (e as Error).message }, 500);
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
