# Cloud Ingestion API Contract (v2.1)

Contract between the Windows agent and the `tally-ingestion-api` Cloud Run
service. **This document matches the code in
`src/TallyAgent.Core/Cloud/IngestionApiClient.cs` as of agent v2.0.** The agent
never talks to BigQuery or GCS directly.

## Conventions

* Base URL: configured `cloud.ingestionApiUrl` (HTTPS mandatory outside
  Development). All endpoints are RELATIVE to this base — one contract, one
  auth scheme, no `/v1/` split.
* Auth on every request: **`X-API-Token: <agent token>`** (the `Authorization`
  header is left free for Google/API-Gateway IAM). Plus `X-Agent-Id` and
  `X-Environment` headers.
* The token is a per-agent credential bound server-side to
  (`agent_id`, `company_id`, `environment`); revocable; never a GCP key.
* `401`/`403` → the agent pauses uploads 10 min and raises a critical
  `AuthenticationFailure` alert. Return these only for real credential problems.
* All timestamps ISO-8601 UTC.

## GET {base}/health

Connectivity probe (installer test, Manager test button).
**200** any JSON — the agent reads an optional `timestamp` field.
NOTE: if `health` is unauthenticated, a wrong token is NOT detected here;
it surfaces at the first `sync`/`heartbeat` call (known limitation — see
CHANGELOG "Known issues").

## POST {base}/sync

Upload one batch. JSON envelope (the on-disk gzip NDJSON is expanded into
`records` client-side):

```json
{
  "agent_id":        "TALLY-SERVER-01",
  "company_id":      "dynel-electric",
  "batch_id":        "TALLY-SERVER-01-dynel-electric-vouchers-2026-07-23-2026-07-30-000042-9f2c1ab34e01",
  "dataset_name":    "vouchers",
  "tally_company":   "Dynel Electric Private Limited",
  "sequence_no":     42,
  "sync_id":         "a1b2c3d4e5f6",
  "record_count":    4813,
  "checksum_sha256": "…",
  "content_checksum":"…",
  "schema_version":  "1.0",
  "agent_version":   "2.0.0",
  "window_from":     "2026-07-23",
  "window_to":       "2026-07-30",
  "extract_start":   "2026-07-30T09:00:01Z",
  "extracted_at":    "2026-07-30T09:00:14Z",
  "retry_count":     0,
  "records":         [ { "…row…": 1 } ]
}
```

Field notes: `checksum_sha256` is the transport checksum of the agent's local
gzip payload file; `content_checksum` is the **identity** checksum computed
over rows WITHOUT audit fields (`_sync_timestamp`, `_sync_id`,
`source_last_seen_at`) — use it for cross-batch duplicate/replacement
decisions. Each row in `records` still carries the audit fields plus
`_company`. `window_from`/`window_to` are null for masters/snapshots.

`batch_id` is **deterministic**
(`{agent}-{company}-{dataset}-{window_from}-{window_to}-{seq:D6}-{content_sha256[:12]}`),
created once, persisted in the agent's SQLite, and reused verbatim on every
retry — server-side dedupe on it is reliable.

Responses (implemented status matrix):

| Status | Body | Agent behaviour |
|---|---|---|
| 200/201/202 | `{"status":"accepted","batch_id":"…"}` (or empty) | ack: delete local payload |
| 409 | any | treated as `duplicate` → ack |
| 400/422 | `{"status":"rejected","errors":["…"]}` | failed-final, critical `SchemaMismatch` alert, payload kept locally |
| 401/403 | — | pause uploads 10 min, critical `AuthenticationFailure` |
| 413 | — | failed-final, advise smaller `uploadBatchMaxRecords` |
| 429 | honour `Retry-After` | retry |
| 5xx / network | — | exponential backoff (1m→30m cap, jitter), retries forever |

Server-side responsibilities (unchanged from ARCHITECTURE.md): authenticate →
verify `record_count`/`content_checksum` → store raw in GCS keyed by batch_id →
register in `ingestion_control` (`accepted`→`loaded`→`processed`/`failed`) →
BigQuery load + MERGE on stable source keys (never `_sync_id`).

## POST {base}/heartbeat

Every `heartbeatMinutes` (default 5). Body = the `HeartbeatRequest` model
(`src/TallyAgent.Core/Cloud/ApiModels.cs`): agent/company ids, machine name,
Windows version, agent version, environment, service status, Tally
connected/company-open flags, Tally company, last successful/attempted sync,
current operation, pending/failed batch counts, last error, disk free MB,
memory MB, internet flag, timestamp.

**200** `{"ok":true,"commands":[{"type":"sync_now"}]}` — `commands` optional;
supported types: `sync_now`, `update`. A 2xx with a non-JSON body counts as
delivered. Server watchdog requirement: no heartbeat for >15 min ⇒ agent-down
alert to the admin.

## POST {base}/errors

Immediate critical reports and grouped summaries. Body = `ErrorReportRequest`
model (fixed-taxonomy category, severity, message, stack trace, operation,
dataset, batch_id, retry count, agent version, `is_summary`, `occurrences`).
**200** `{"ok":true}`.

## GET {base}/updates/check?current=2.0.0&channel=production

**204/404** no update · **200** `{"version":"…","url":"…","sha256":"…","mandatory":false}`.
Only versions approved for the channel may be returned.

---

### Changes vs v1 of this document

* `Bearer` auth → `X-API-Token` header (Authorization reserved for gateways).
* `/v1/ping|batches|heartbeat|errors|updates` → unversioned
  `health|sync|heartbeat|errors|updates/check` on one base — the previous
  half-migrated split (sync new-style, monitoring old-style) is removed.
* gzip-NDJSON body + 15 metadata headers → JSON envelope with the metadata
  restored as body fields after the first `/sync` migration dropped them.
* New `content_checksum` field (identity, audit-fields excluded); the batch ID
  suffix is now derived from it rather than from the transport checksum.
