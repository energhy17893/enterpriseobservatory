# enterprise-observatory-mcp-server

Read-only MCP server over the product's own API, so an agent (Claude Code,
Claude Desktop) sees the same alerts, entities, findings and collector state
the operator sees — one read model, no psql.

## Run

```bash
cd tools/mcp && npm ci && npm run build
```

Environment (set by the operator; the server never stores or prints them):

| Variable | Meaning |
|---|---|
| `EO_BASE_URL` | Product origin, default `http://localhost:5000` |
| `EO_MCP_USERNAME` / `EO_MCP_PASSWORD` | A **Viewer** account created in Configure → Accounts; nothing here needs more |

Self-signed HTTPS: set `NODE_EXTRA_CA_CERTS` to the product's CA, or use the
http origin on the same machine.

Register it for Claude Code at user scope (the repo ships no `.mcp.json`, so
a developer's own project-level file is never overwritten); the credentials
are read from the user environment at start:

```bash
claude mcp add --scope user enterprise-observatory -- node C:/src/enterpriseobservatory/tools/mcp/dist/index.js
```

## Tools (all read-only)

`eo_get_health`, `eo_get_overview`, `eo_list_alerts`, `eo_list_alert_groups`,
`eo_list_entities`, `eo_get_entity`, `eo_list_series`, `eo_get_series`,
`eo_list_collectors`, `eo_get_coverage`, `eo_get_self_metrics`,
`eo_get_simplivity`, `eo_get_events`, `eo_list_vcenter_events`,
`eo_get_compliance`, `eo_list_findings`, `eo_list_exceptions`,
`eo_get_report`, `eo_list_maintenance_windows`.

Answers are the API's JSON unchanged; a list over 40,000 characters is cut
to the rows that fit and says so (`truncated`), so filter or page.

## Check

```bash
npm test
```

Spawns the server over stdio, asserts every tool is prefixed and read-only,
and — when credentials are set and the product answers — calls two tools live.
