# SimpliVity fixtures — provenance

HPE does not publish full example response bodies for the OmniStack REST
API, and this estate has no SimpliVity host to record a real one from yet —
that gap is the reason M6.0b exists (docs/reference-approaches.md §10.7:
"olay akışımızda com.simplivity.* olayı yok ... M6.0b probe'un ilk sorusu").

`simplivity-swagger.json` is HPE's own published OpenAPI 2.0 definition of
the API (title "HPE OmniStack REST API", version 1.25):
https://github.com/HewlettPackard/hpe-simplivity-swagger/blob/master/simplivity-swagger.json

The other files in this folder are instance documents built *from* that
schema — every field name, required-field list and enum value below is
copied from `simplivity-swagger.json`'s `definitions.host`,
`definitions.omnistack_cluster`, `definitions.virtual_machine` and
`definitions.BackupMO`; the values that fill them are synthetic (fictional
hostnames, UUID-shaped ids, plausible capacity numbers), not measurements —
`RedfishProbe --dry --kind simplivity` says so in its own output.

| File | Built from |
|---|---|
| `version.json` | `GET /version` response fields named in reference-approaches.md §10.7 (`REST_API_Version`, `SVTFS_Version`); no schema definition exists for this endpoint in the swagger file. |
| `oauth-token.json` | Standard OAuth2 password-grant token response fields (`access_token`, `token_type`, `expires_in`); `POST /oauth/token`'s swagger path has no response schema either. Not read by `--dry` — the probe never fabricates a token. |
| `hosts.json` | `definitions.host` (swagger lines ~9760–10021). `state` and `upgrade_state` enums copied verbatim. |
| `omnistack_clusters.json` | `definitions.omnistack_cluster` (swagger lines ~10189–10411). `arbiter_*`, `upgrade_state`, `hypervisor_type` enums copied verbatim. |
| `virtual_machines.json` | `definitions.virtual_machine`. `ha_status` enum (`UNKNOWN, OUT_OF_SCOPE, NOT_APPLICABLE, DEGRADED, SAFE, SYNCING, DEFUNCT`) copied verbatim; one VM per named value that a host/VM record can plausibly carry. |
| `backups.json` | `definitions.BackupMO` (swagger lines ~8549–8752). `state`, `type`, `consistency_type`, `backup_store_type` enums copied verbatim. |

`hosts[].hypervisor_object_id` is written in vim25 moRef shape
(`host-21`) on purpose, to exercise `SimplivityReport`'s
"looks like a moRef" check — whether that shape is what a live SimpliVity
Virtual Controller actually returns is itself one of M6.0b's open
measurements (§10.7/§10.8: "ölçüm gerektirir").
