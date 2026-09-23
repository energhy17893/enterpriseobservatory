/**
 * The one check that fails if the server breaks: spawn it over stdio, list
 * the tools, and — when credentials and a product are there — call two.
 * No framework; `npm test`.
 */
import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";

const serverPath = fileURLToPath(new URL("./index.js", import.meta.url));
const client = new Client({ name: "smoke", version: "0" });
await client.connect(new StdioClientTransport({ command: process.execPath, args: [serverPath], env: process.env as Record<string, string> }));

const { tools } = await client.listTools();
assert.ok(tools.length >= 18, `expected at least 18 tools, got ${tools.length}`);
for (const t of tools) {
  assert.ok(t.name.startsWith("eo_"), `${t.name} lacks the eo_ prefix`);
  assert.equal(t.annotations?.readOnlyHint, true, `${t.name} is not marked read-only`);
  assert.ok(t.description && t.description.length > 20, `${t.name} has no description`);
}

// Without credentials the tool must answer with an actionable error, not crash.
const health = await client.callTool({ name: "eo_get_health", arguments: {} });
const text = (health.content as { type: string; text?: string }[])[0]?.text ?? "";

if (process.env.EO_MCP_USERNAME && process.env.EO_MCP_PASSWORD) {
  assert.ok(!health.isError, `eo_get_health failed: ${text}`);
  const overview = await client.callTool({ name: "eo_list_entities", arguments: { limit: 1 } });
  assert.ok(!overview.isError, `eo_list_entities failed: ${(overview.content as { text?: string }[])[0]?.text}`);
  const page = overview.structuredContent as { total: number; items: unknown[] };
  assert.ok(page.total >= page.items.length, "page total is smaller than its items");
  console.log(`ok: ${tools.length} tools; live: entities total ${page.total}`);
} else {
  // /health is anonymous, so it answers even without credentials.
  console.log(`ok: ${tools.length} tools; no credentials, live calls skipped (health: ${health.isError ? "error" : "answered"})`);
}

await client.close();
