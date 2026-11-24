import { DurableObject } from "cloudflare:workers";

const deviceRoute = /^\/v1\/devices\/([A-Za-z0-9_-]{22})\/(host|request)$/;
const secretFormat = /^[A-Za-z0-9_-]{43}$/;
const requestId = /^[a-f0-9]{32}$/;
const encoded = /^[A-Za-z0-9_-]+$/;
const maxRequest = 8192;
const maxReply = 720000;
const maxPending = 4;
const timeoutMs = 10000;
const openSocket = 1;

export default {
  fetch(request, env) {
    const path = new URL(request.url).pathname;
    if (path === "/v1/health" && request.method === "GET") {
      return Response.json({ protocol: "pcpulse-relay-v1" });
    }
    const match = deviceRoute.exec(path);
    const expected = match?.[2] === "host" ? "GET" : "POST";
    if (!match || request.method !== expected) return reply(404);
    if (!secretFormat.test(bearer(request))) return reply(401);
    return env.DEVICES.getByName(match[1]).fetch(request);
  },
};

export class DeviceRelay extends DurableObject {
  #pending = new Map();

  async fetch(request) {
    const path = new URL(request.url).pathname;
    if (path.endsWith("/host")) return this.#openHost(request);
    return this.#sendRequest(request);
  }

  async #openHost(request) {
    if (request.headers.get("Upgrade")?.toLowerCase() !== "websocket") return reply(426);
    const host = bearer(request);
    const client = request.headers.get("X-PC-Pulse-Client") ?? "";
    if (!secretFormat.test(client)) return reply(401);

    const hostHash = await digest(host);
    const clientHash = await digest(client);
    const enrollment = request.headers.get("X-PC-Pulse-Enrollment") ?? "";
    const allowed = await this.ctx.storage.transaction(async (transaction) => {
      const saved = await transaction.get("credentials");
      if (saved) return same(saved.hostHash, hostHash) && same(saved.clientHash, clientHash);
      const expected = this.env.RELAY_ENROLLMENT_KEY;
      if (!expected || !secretFormat.test(expected) || !same(expected, enrollment)) return false;
      await transaction.put("credentials", { hostHash, clientHash });
      return true;
    });
    if (!allowed) return reply(401);

    for (const socket of this.ctx.getWebSockets("pc")) {
      this.#failPending(socket);
      socket.close(1000, "Reconnected");
    }
    const [outbound, accepted] = Object.values(new WebSocketPair());
    this.ctx.acceptWebSocket(accepted, ["pc"]);
    return new Response(null, { status: 101, webSocket: outbound });
  }

  async #sendRequest(request) {
    const saved = await this.ctx.storage.get("credentials");
    if (!saved || !same(saved.clientHash, await digest(bearer(request)))) return reply(401);
    const host = this.ctx.getWebSockets("pc").find((socket) => socket.readyState === openSocket);
    if (!host) return reply(503);
    if (this.#pending.size >= maxPending) return reply(429);
    let envelope;
    try {
      envelope = parse(await readLimited(request, maxRequest), maxRequest);
    } catch {
      return reply(400);
    }
    if (!envelope) return reply(400);
    if (this.#pending.has(envelope.id)) return reply(409);
    if (this.#pending.size >= maxPending) return reply(429);

    let timer;
    try {
      return await new Promise((resolve) => {
        timer = setTimeout(() => resolve(reply(504)), timeoutMs);
        this.#pending.set(envelope.id, { host, resolve });
        try {
          host.send(JSON.stringify(envelope));
        } catch {
          resolve(reply(503));
        }
      });
    } finally {
      clearTimeout(timer);
      this.#pending.delete(envelope.id);
    }
  }

  webSocketMessage(socket, message) {
    const envelope = parse(message, maxReply);
    if (!envelope) return;
    const pending = this.#pending.get(envelope.id);
    if (pending?.host === socket) {
      pending.resolve(Response.json(envelope, { headers: { "Cache-Control": "no-store" } }));
    }
  }

  webSocketClose(socket) {
    this.#failPending(socket);
  }

  webSocketError(socket) {
    this.#failPending(socket);
  }

  #failPending(socket) {
    for (const pending of this.#pending.values()) {
      if (pending.host === socket) pending.resolve(reply(503));
    }
  }
}

function reply(status) {
  return new Response(null, { status, headers: { "Cache-Control": "no-store" } });
}

function bearer(request) {
  const value = request.headers.get("Authorization") ?? "";
  return value.startsWith("Bearer ") ? value.slice(7) : "";
}

async function digest(secret) {
  const bytes = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(secret));
  return btoa(String.fromCharCode(...new Uint8Array(bytes)));
}

function same(first, second) {
  if (first.length !== second.length) return false;
  let difference = 0;
  for (let index = 0; index < first.length; index++) {
    difference |= first.charCodeAt(index) ^ second.charCodeAt(index);
  }
  return difference === 0;
}

function parse(text, limit) {
  if (typeof text !== "string" || text.length > limit) return null;
  try {
    const { id, data } = JSON.parse(text);
    if (typeof id !== "string" || !requestId.test(id) || typeof data !== "string" || !encoded.test(data)) return null;
    return { id, data };
  } catch {
    return null;
  }
}

async function readLimited(request, limit) {
  const reader = request.body?.getReader();
  if (!reader) return "";
  const decoder = new TextDecoder();
  let total = 0;
  let text = "";
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) return text + decoder.decode();
      total += value.byteLength;
      if (total > limit) {
        await reader.cancel();
        throw new Error("Oversize request");
      }
      text += decoder.decode(value, { stream: true });
    }
  } finally {
    reader.releaseLock();
  }
}
