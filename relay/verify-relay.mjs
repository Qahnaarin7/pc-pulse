import assert from "node:assert/strict";
import { createCipheriv, createDecipheriv, randomBytes } from "node:crypto";
import { once } from "node:events";
import WebSocket from "ws";

const url = process.env.PCPULSE_RELAY_TEST_URL ?? "http://127.0.0.1:8788";
const enrollment = process.env.PCPULSE_TEST_ENROLLMENT;
if (!enrollment || !/^[A-Za-z0-9_-]{43}$/.test(enrollment)) {
  throw new Error("Set PCPULSE_TEST_ENROLLMENT to the test Worker's enrollment key.");
}
const device = randomBytes(16).toString("base64url");
const hostKey = randomBytes(32).toString("base64url");
const phoneKey = randomBytes(32).toString("base64url");
const key = randomBytes(32);
const resource = `${url}/v1/devices/${device}`;
const inbound = new URL(resource + "/host");
inbound.protocol = inbound.protocol === "https:" ? "wss:" : "ws:";
const speak = (object, token = phoneKey) => fetch(resource + "/request", {
  method: "POST",
  headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
  body: JSON.stringify(object),
});

const noAccount = await speak({}, "A".repeat(43));
assert.equal(noAccount.status, 401);

async function connect(extra) {
  const connection = new WebSocket(inbound, {
    headers: {
      Authorization: `Bearer ${hostKey}`,
      "X-PC-Pulse-Client": phoneKey,
      ...extra,
    },
  });
  await once(connection, "open");
  return connection;
}

await assert.rejects(connect({ "X-PC-Pulse-Enrollment": "Z".repeat(43) }), /401/);
const host = await connect({ "X-PC-Pulse-Enrollment": enrollment });
let seen = "";
const deliver = (socket) => socket.on("message", (message) => {
  const request = JSON.parse(message.toString());
  seen = JSON.parse(open("request", request)).path;
  socket.send(JSON.stringify({ id: request.id, data: seal("response", request.id, { model: "Test GPU", temp: 59 }) }));
});
try {
  deliver(host);
  const id = randomBytes(16).toString("hex");
  const command = { id, data: seal("request", id, { method: "GET", path: "/v1/temperatures", at: Date.now() }) };
  assert.equal((await speak(command, hostKey)).status, 401);
  assert.equal((await speak({ id, data: "!@#" })).status, 400);
  const response = await speak(command);
  assert.equal(response.status, 200);
  const reply = await response.json();
  assert.equal(reply.id, id);
  assert.equal(seen, "/v1/temperatures");
  assert.deepEqual(JSON.parse(open("response", reply)), { model: "Test GPU", temp: 59 });
  assert.throws(() => open("request", reply));

  host.close();
  await once(host, "close");
  const rebooted = await connect({});
  try {
    deliver(rebooted);
    const nextId = randomBytes(16).toString("hex");
    const next = await speak({ id: nextId, data: seal("request", nextId, { path: "/v1/processes" }) });
    assert.equal(next.status, 200);
    assert.equal(seen, "/v1/processes");
  } finally {
    rebooted.close();
  }
  console.log("Relay registration, authentication, encrypted round trip, and reconnection passed.");
} finally {
  host.close();
}

function aad(direction, id) {
  return Buffer.from(`pcpulse/relay:v1:${direction}:${id}`);
}

function seal(direction, id, object) {
  const nonce = randomBytes(12);
  const cipher = createCipheriv("aes-256-gcm", key, nonce);
  cipher.setAAD(aad(direction, id));
  return Buffer.concat([nonce, cipher.update(JSON.stringify(object)), cipher.final(), cipher.getAuthTag()]).toString("base64url");
}

function open(direction, message) {
  const data = Buffer.from(message.data, "base64url");
  const decipher = createDecipheriv("aes-256-gcm", key, data.subarray(0, 12));
  decipher.setAAD(aad(direction, message.id));
  decipher.setAuthTag(data.subarray(-16));
  return Buffer.concat([decipher.update(data.subarray(12, -16)), decipher.final()]).toString();
}
