import { corsHeaders, handleDelete, handleGet, handlePost } from "./handlers";
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

    const match = url.pathname.match(/^\/clubs\/([^/]+)\/?$/);
    if (!match) {
      return jsonResponse({ error: "not found" }, 404);
    }
    const plotKey = decodeURIComponent(match[1]!);

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
  },
};

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json", ...corsHeaders() },
  });
}
