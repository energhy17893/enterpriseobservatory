#!/usr/bin/env node
/**
 * MCP server for Enterprise Observatory's read-only API.
 *
 * Every tool is a GET against the product's own read model (ADR-0007: one
 * model, many views) so an agent sees exactly what the operator sees — the
 * same alerts, findings and health, with the same Unknown/stale semantics.
 * Nothing here acknowledges, clears, accepts or configures.
 *
 * Authentication is the product's cookie session: the server signs in once
 * with EO_MCP_USERNAME / EO_MCP_PASSWORD (a Viewer account is enough) and
 * re-signs on a 401. The password is read from the environment and never
 * logged, echoed or returned.
 */

import { McpServer, type ToolCallback } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { z } from "zod";

const BASE_URL = (process.env.EO_BASE_URL ?? "http://localhost:5000").replace(/\/+$/, "");
const USERNAME = process.env.EO_MCP_USERNAME;
const PASSWORD = process.env.EO_MCP_PASSWORD;

/** Largest text a tool returns; a Kibar-sized findings list is ~5,000 rows, far beyond this. */
export const CHARACTER_LIMIT = 40_000;

type Query = Record<string, string | number | boolean | undefined>;

class ApiFailure extends Error {
  constructor(readonly status: number | null, readonly detail: string) {
    super(detail);
  }
}

let cookie: string | null = null;

async function signIn(): Promise<void> {
  if (!USERNAME || !PASSWORD) {
    throw new ApiFailure(
      null,
      "EO_MCP_USERNAME and EO_MCP_PASSWORD are not set. Create a Viewer account in the product " +
        "(Configure → Accounts) and put its credentials in the environment of the MCP host; " +
        "the server never stores them.",
    );
  }

  const response = await fetch(`${BASE_URL}/api/auth/signin`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json" },
    body: JSON.stringify({ username: USERNAME, password: PASSWORD }),
  });

  if (!response.ok) {
    throw new ApiFailure(response.status, `sign-in as '${USERNAME}' was refused (${response.status})`);
  }

  const session = response.headers
    .getSetCookie()
    .map((header) => header.split(";")[0] ?? "")
    .find((pair) => pair.startsWith("eo.session="));

  if (!session) {
    throw new ApiFailure(response.status, "sign-in succeeded but the server set no eo.session cookie");
  }

  cookie = session;
}

async function get(path: string, query: Query = {}): Promise<unknown> {
  const url = new URL(`${BASE_URL}${path}`);
  for (const [key, value] of Object.entries(query)) {
    if (value !== undefined && value !== "") url.searchParams.set(key, String(value));
  }

  for (let attempt = 0; attempt < 2; attempt++) {
    // /health is anonymous; everything else needs the session first.
    if (cookie === null && path !== "/health") await signIn();

    let response: Response;
    try {
      response = await fetch(url, {
        headers: { Accept: "application/json", Cookie: cookie ?? "" },
        signal: AbortSignal.timeout(60_000),
      });
    } catch (cause) {
      throw new ApiFailure(null, `${BASE_URL} could not be reached: ${cause instanceof Error ? cause.message : String(cause)}`);
    }

    // The session expired (12 h sliding): sign in again, once.
    if (response.status === 401 && attempt === 0) {
      cookie = null;
      continue;
    }

    // /health answers 503 with the same report when the product is unhealthy;
    // that report is the answer, not a failure.
    if (response.ok || (path === "/health" && response.status === 503)) {
      return response.json();
    }

    const body = await response.text().catch(() => "");
    throw new ApiFailure(response.status, body);
  }

  throw new ApiFailure(401, "still unauthorised after signing in again");
}

/**
 * The sentence an agent reads when a call fails. It must say what to do
 * next, not only what happened.
 */
function describeFailure(failure: ApiFailure, path: string): string {
  // The product's own refusal sentence beats ours when it sent one.
  let detail = failure.detail;
  try {
    const body = JSON.parse(detail) as { detail?: unknown };
    if (typeof body.detail === "string") detail = body.detail;
  } catch {
    // not JSON; keep the text
  }

  switch (failure.status) {
    case null:
      return `Error: ${detail}. Check EO_BASE_URL (${BASE_URL}) and that the EnterpriseObservatory service is running; eo_get_health needs no credentials.`;
    case 401:
      return `Error: ${path} refused the session even after signing in again (${detail}). The EO_MCP_USERNAME account or its password is wrong, or the account was removed.`;
    case 403:
      return `Error: ${path} is not allowed for this account's role (${detail}). Every tool here works with Viewer; if this one does not, the account was demoted or the endpoint changed.`;
    case 404:
      return `Error: ${path} — ${detail || "nothing with that id"}. Ids must be exactly as the API returns them; find the entity with eo_list_entities (search=name) and use its id field.`;
    case 429:
      return `Error: ${path} was rate-limited (${detail}). Wait a moment and retry; do not loop.`;
    default:
      return failure.status >= 500
        ? `Error: ${path} answered ${failure.status} (${detail}). The product itself failed; call eo_get_health and check the host log before retrying.`
        : `Error: ${path} answered ${failure.status}: ${detail}`;
  }
}

/** Cuts a list-shaped answer down to the character limit; anything else is cut as text. */
function fit(data: unknown): { text: string; structured: unknown } {
  let text = JSON.stringify(data);
  if (text.length <= CHARACTER_LIMIT) return { text, structured: data };

  const container = data as Record<string, unknown>;
  const key = Array.isArray(data) ? null : Array.isArray(container.items) ? "items" : Array.isArray(container.findings) ? "findings" : null;
  const list = (key === null ? data : container[key]) as unknown[] | undefined;

  if (Array.isArray(list)) {
    let kept = list;
    while (text.length > CHARACTER_LIMIT && kept.length > 1) {
      kept = kept.slice(0, Math.ceil(kept.length / 2));
      const shrunk = key === null ? kept : { ...container, [key]: kept };
      text = JSON.stringify(shrunk);
    }
    const note = `truncated: ${kept.length} of ${list.length} rows shown; narrow with a filter or page with offset/limit`;
    const structured = key === null ? { items: kept, truncated: note } : { ...container, [key]: kept, truncated: note };
    return { text: JSON.stringify(structured), structured };
  }

  return { text: text.slice(0, CHARACTER_LIMIT) + "\n…truncated", structured: { truncated: true } };
}

const server = new McpServer({ name: "enterprise-observatory-mcp-server", version: "0.1.0" });

const readOnly = { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false };

/** Registers one GET as one tool. The API's JSON is the answer; it is not reshaped. */
function tool<S extends z.ZodRawShape>(
  name: string,
  title: string,
  description: string,
  shape: S,
  call: (args: z.objectOutputType<S, z.ZodTypeAny>) => Promise<unknown>,
  pathOf: (args: z.objectOutputType<S, z.ZodTypeAny>) => string,
): void {
  const handler = (async (args: unknown): Promise<CallToolResult> => {
      const typed = args as z.objectOutputType<S, z.ZodTypeAny>;
      try {
        const { text, structured } = fit(await call(typed));
        return {
          content: [{ type: "text", text }],
          structuredContent: (typeof structured === "object" && structured !== null && !Array.isArray(structured)
            ? structured
            : { items: structured }) as Record<string, unknown>,
        };
      } catch (error) {
        const failure = error instanceof ApiFailure ? error : new ApiFailure(null, error instanceof Error ? error.message : String(error));
        return { isError: true, content: [{ type: "text", text: describeFailure(failure, pathOf(typed)) }] };
      }
    }) as ToolCallback<S>;

  server.registerTool(name, { title, description, inputSchema: shape, annotations: readOnly }, handler);
}

const paging = {
  offset: z.number().int().min(0).default(0).describe("Rows to skip"),
  limit: z.number().int().min(1).max(500).default(50).describe("Rows to return (max 500)"),
};

const id = (what: string) => z.string().min(1).describe(`${what} id exactly as the API returns it (e.g. "KBVc01:vm-1234")`);

tool("eo_get_health", "Product health", "The product's own /health report: up, database, collectors; 503 is returned as a report, not an error. Anonymous.", {}, () => get("/health"), () => "/health");

tool("eo_get_overview", "Estate overview", "Triage overview: health counts, open alerts by severity, collector state, blind spots — what the Overview screen shows.", {}, () => get("/api/overview"), () => "/api/overview");

tool(
  "eo_list_alerts",
  "List alerts",
  "Alert instances from the inbox, open ones first. Unknown is not a state: it is state=Open with a staleReason (SourceDisabled, SourceSilent…). Filter by severity (Critical/Warning/Info), state (Open/Acknowledged/Silenced/Resolved), category, source instance id, or free text. Returns {items,total,offset,limit}.",
  { severity: z.string().optional(), state: z.string().optional(), category: z.string().optional(), source: z.string().optional().describe("Source instance id, e.g. KBVc01"), search: z.string().optional(), ...paging },
  (a) => get("/api/alerts", a),
  () => "/api/alerts",
);

tool(
  "eo_list_alert_groups",
  "Alerts grouped by rule",
  "The same alerts folded by rule (reference §11.5): one row per rule with its count and entities. Same filters as eo_list_alerts.",
  { severity: z.string().optional(), state: z.string().optional(), category: z.string().optional(), source: z.string().optional(), search: z.string().optional(), ...paging },
  (a) => get("/api/alerts/grouped", a),
  () => "/api/alerts/grouped",
);

tool(
  "eo_list_entities",
  "List entities",
  "Entity explorer (ADR-0007 tier 1): name, kind, health, alert count, last seen. kind: EsxiHost, VirtualMachine, Datastore, Cluster, VCenter. health: Unknown/Healthy/Warning/Critical. Vanished entities are excluded unless includeVanished. Returns {items,total,offset,limit}.",
  { kind: z.string().optional(), health: z.enum(["Unknown", "Healthy", "Warning", "Critical"]).optional(), source: z.string().optional().describe("Source instance id"), search: z.string().optional().describe("Name substring"), includeVanished: z.boolean().default(false), ...paging },
  (a) => get("/api/entities", a),
  () => "/api/entities",
);

tool(
  "eo_get_entity",
  "Entity detail",
  "One entity (tier 2): health with basis, identity marks, relationships (clickable graph edges), its alerts, time-to-full for datastores, HA scorecard and N+1 for clusters, and other sources' annotations (simplivity.*, redfish.*; ADR-0027) with carried-forward marks.",
  { id: id("Entity") },
  (a) => get(`/api/entities/${encodeURIComponent(a.id)}`),
  (a) => `/api/entities/${a.id}`,
);

tool(
  "eo_list_series",
  "Available counters",
  "Which measurement series exist for an entity (counter id, unit, instances).",
  { id: id("Entity") },
  (a) => get(`/api/entities/${encodeURIComponent(a.id)}/series`),
  (a) => `/api/entities/${a.id}/series`,
);

tool(
  "eo_get_series",
  "Measurement series",
  "Points of one counter for one entity between from and to (ISO-8601 UTC); maxPoints thins the answer server-side. Get counter ids from eo_list_series.",
  { id: id("Entity"), counter: z.string().min(1).describe("Counter id from eo_list_series"), instance: z.string().optional(), from: z.string().optional(), to: z.string().optional(), maxPoints: z.number().int().min(10).max(5000).optional() },
  (a) => get(`/api/entities/${encodeURIComponent(a.id)}/series/${encodeURIComponent(a.counter)}`, { instance: a.instance, from: a.from, to: a.to, maxPoints: a.maxPoints }),
  (a) => `/api/entities/${a.id}/series/${a.counter}`,
);

tool("eo_list_collectors", "Collector state", "Every collector role per source (Inventory, Observation, Events, Configuration): health, last success, consecutive failures, backing off, partial failures. 'Cannot list inventory' and 'cannot read metrics' are different fixes (ADR-0009).", {}, () => get("/api/collectors"), () => "/api/collectors");

tool("eo_get_coverage", "What could be read", "Per source and property: asked vs answered, blind/partial/complete. The only place a rule's silence can be told from a gap.", {}, () => get("/api/coverage"), () => "/api/coverage");

tool("eo_get_self_metrics", "Self-metrics", "The product observing itself: per-role cycle durations, budgets, queue and fold measurements (ADR-0025).", {}, () => get("/api/metrics"), () => "/api/metrics");

tool("eo_get_simplivity", "SimpliVity federation", "The SimpliVity page: clusters → hosts (state, arbiter, version), Storage HA, hardware, backups, capacity, open findings and alarms.", {}, () => get("/api/simplivity"), () => "/api/simplivity");

tool("eo_get_events", "Event board", "The product's own event board (alert transitions, collector events).", {}, () => get("/api/events"), () => "/api/events");

tool(
  "eo_list_vcenter_events",
  "vCenter events",
  "Events read from vCenter's event stream, newest first. search is case-insensitive across message, type id, VM, host and user.",
  { source: z.string().optional().describe("vCenter instance id"), search: z.string().optional(), ...paging },
  (a) => get("/api/vcenter-events", a),
  () => "/api/vcenter-events",
);

tool("eo_get_compliance", "Posture scorecards", "Every catalogue's scorecard (Broadcom SCG, eo-continuity, eo-bestpractice, eo-simplivity): counts by verdict, stale, per-control totals.", {}, () => get("/api/compliance"), () => "/api/compliance");

tool(
  "eo_list_findings",
  "Compliance findings",
  "Findings across catalogues, one per (control, entity, subject). Always filter: a Kibar-sized estate has thousands. state: Failing, Passing, NotEvaluated, Accepted, Excepted. Each finding carries verdict, observed, expected, reason (for NotEvaluated), stale, and its basis citation.",
  { control: z.string().optional().describe("Control id, e.g. eo-cont.backup-freshness"), entity: z.string().optional().describe("Entity id"), state: z.enum(["Failing", "Passing", "NotEvaluated", "Accepted", "Excepted"]).optional() },
  (a) => get("/api/compliance/findings", a),
  () => "/api/compliance/findings",
);

tool("eo_list_exceptions", "Compliance exceptions", "Standing exceptions operators declared for controls/entities, with who and why.", {}, () => get("/api/compliance/exceptions"), () => "/api/compliance/exceptions");

tool(
  "eo_get_report",
  "Reports",
  "One of the four reports as JSON: alerts (filters severity/state/category/source/from/to), compliance (catalogue registry id, control, entity, from/to — transitions in the window), capacity, continuity.",
  { report: z.enum(["alerts", "compliance", "capacity", "continuity"]), severity: z.string().optional(), state: z.string().optional(), category: z.string().optional(), source: z.string().optional(), catalogue: z.string().optional(), control: z.string().optional(), entity: z.string().optional(), from: z.string().optional().describe("ISO-8601 UTC"), to: z.string().optional().describe("ISO-8601 UTC") },
  ({ report, ...query }) => get(`/api/reports/${report}`, query),
  (a) => `/api/reports/${a.report}`,
);

tool("eo_list_maintenance_windows", "Maintenance windows", "Declared maintenance windows: scope, who declared them, from/until, whether ended.", {}, () => get("/api/maintenance"), () => "/api/maintenance");

async function main(): Promise<void> {
  if (!USERNAME || !PASSWORD) {
    console.error("enterprise-observatory-mcp-server: EO_MCP_USERNAME/EO_MCP_PASSWORD not set; every tool will answer with that error until they are.");
  }
  await server.connect(new StdioServerTransport());
  console.error(`enterprise-observatory-mcp-server: stdio, ${BASE_URL}`);
}

main().catch((error) => {
  console.error("enterprise-observatory-mcp-server failed:", error instanceof Error ? error.message : error);
  process.exit(1);
});
