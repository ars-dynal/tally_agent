# Cloud Ingestion API Contract (v1)

Contract between the Windows agent and the Cloud Run ingestion service.
The agent **never** talks to BigQuery or GCS directly.

## Conventions

* Base URL: configured `cloud.ingestionApiUrl` (HTTPS mandatory outside Development).
* Auth on every request: `Authorization: Bearer <agent-token>` plus
  `X-Agent-Id: <agentId>` and `X-Environment: Development|Testing|Production`.
* The token is a per-agent credential issued by the admin, revocable server-side,
  scoped to one `company_id`. Server must reject mismatched agent/company pairs.
* All timestamps ISO-8601 UTC.
* `401`/`403` responses cause the agent to pause uploads and raise a critical
  `AuthenticationFailure` alert — return them only for real credential problems.

---

## GET /v1/ping

Connectivity + credential probe (installer test button, manager test button).

**200** `{ "ok": true, "server_time": "2026-07-30T12:00:00Z" }`

---

## POST /v1/batches

Upload one extracted batch.

* Body: gzip-compressed NDJSON — one JSON object per row.
  `Content-Type: application/x-ndjson`, `Content-Encoding: gzip`.
* Every row carries meta columns `_sync_timestamp`, `_sync_id`, `_company`
  (matching the BigQuery schema in Tally_Schema_Design.xlsx).

Request headers (batch envelope):

| Header | Example | Notes |
|---|---|---|
| X-Batch-Id | `TALLY-SERVER-01-dynel-electric-vouchers-2026-07-23-2026-07-30-000042-9f2c1ab34e01` | **deterministic**: `{agent_id}-{company_id}-{dataset}-{window_from}-{window_to}-{sequence}-{sha256[:12]}`, created once, persisted in the agent's SQLite, reused verbatim on every retry |
| X-Dataset | `vouchers` | dataset key (33 known values) |
| X-Company | `Dynel%20Electric%20Private%20Limited` | URL-encoded Tally company |
| X-Company-Id | `dynel-electric` | tenant key |
| X-Sequence | `42` | monotonic per dataset |
| X-Sync-Id | `a1b2c3d4e5f6` | sync-run correlation id |
| X-Record-Count | `4813` | rows in payload |
| X-Checksum-Sha256 | `9f2c...` | hash of the gzip payload — server MUST verify |
| X-Schema-Version | `1.0` | reject unknown versions with 400 |
| X-Agent-Version | `1.0.0` | |
| X-Extract-Start / X-Extract-End | ISO UTC | extraction wall-clock |
| X-Window-From / X-Window-To | `2026-07-23` | voucher date window (optional) |
| X-Retry-Count | `3` | delivery attempt count |
| X-Control-Totals | JSON | record count, debit/credit/amount totals, cancelled count — drives reconciliation |

Responses:

| Status | Body | Agent behaviour |
|---|---|---|
| 200/201/202 | `{"status":"accepted","batch_id":"..."}` | ack: delete local payload |
| 200 or 409 | `{"status":"duplicate","batch_id":"..."}` | ack (idempotent re-send) |
| 400/422 | `{"status":"rejected","errors":[...]}` | failed-final, critical `SchemaMismatch` alert, payload kept locally |
| 401/403 | — | pause uploads, critical `AuthenticationFailure` |
| 413 | — | failed-final, advise smaller `uploadBatchMaxRecords` |
| 429 | honour `Retry-After` | retry |
| 5xx / network | — | exponential backoff retry (1m→30m cap, jitter), forever |

**Server-side pipeline & duplicate-proofing (implementation requirement):**

1. **Authenticate** the agent (token ↔ agent_id ↔ company_id binding).
2. **Validate** metadata + recompute payload SHA-256 vs `X-Checksum-Sha256` (mismatch ⇒ 400).
3. **Store** raw payload at `gs://{raw-bucket}/{company_id}/{dataset}/{batch_id}.ndjson.gz`
   (key = batch_id ⇒ replays overwrite identically — idempotent).
4. **Register** the batch in the `ingestion_control` table (dedupe on `batch_id` —
   an already-registered batch returns `{"status":"duplicate"}` without reprocessing):
   `batch_id, agent_id, company_id, dataset, sequence, window_from, window_to,
   record_count, checksum, schema_version, agent_version, control_totals,
   status, received_at, loaded_at, processed_at, error`.
5. **Start or queue** the BigQuery load job (GCS → `stg_{dataset}`), then MERGE
   staging → warehouse, advancing `ingestion_control.status`:

   | status | meaning |
   |---|---|
   | `accepted` | raw file safely stored in Cloud Storage (what the 200 to the agent asserts) |
   | `loaded` | load job completed into staging |
   | `processed` | staging merged into warehouse |
   | `failed` | a step failed — `error` set, raw file retained for replay, alert raised |

**MERGE keys — stable source keys ONLY (`_sync_id`/`_sync_timestamp` are audit columns, never key material):**

* voucher datasets: `source_company_id + voucher_guid`
* masters: `source_company_id + master_id` (or `source_company_id + master_guid`)
* voucher child records: `source_company_id + voucher_guid + entry_type + stable_line_identifier`
  (line ordinal per entry type; re-extraction replaces the voucher's child set deterministically)
* SNAPSHOT tables (trial_balance, stock_items, outstanding_*): replace per
  `source_company_id` per sync (WRITE_TRUNCATE semantics from the schema workbook)

Recency between versions of a key: `source_last_seen_at` / batch load time.
Voucher rows carry `is_cancelled`, `is_deleted`, `source_status`, `source_last_seen_at`;
deletions detected by GUID-manifest reconciliation are **status updates, never physical
deletes** of raw records.

---

## POST /v1/heartbeat

Every 5 minutes. JSON body:

```json
{
  "agent_id": "TALLY-SERVER-01", "company_id": "dynel-electric",
  "machine_name": "ACCOUNTS-SERVER", "windows_version": "...",
  "agent_version": "1.0.0", "environment": "Production",
  "service_status": "running",
  "tally_connected": true, "tally_company_open": true,
  "tally_company": "Dynel Electric Private Limited",
  "last_successful_sync_utc": "...", "last_attempted_sync_utc": "...",
  "current_operation": "idle",
  "pending_batches": 4, "failed_batches": 0,
  "last_error": "TallyTimeout: ...",
  "disk_free_mb": 51200, "memory_used_mb": 145,
  "internet_connected": true, "timestamp_utc": "..."
}
```

**200** `{ "ok": true, "commands": [ { "type": "sync_now" } ] }` — `commands`
optional; supported types: `sync_now`, `update` (`{"type":"update","version":"1.1.0"}`).

**Server watchdog requirement:** no heartbeat for >15 minutes ⇒ raise the
`ServiceStopped` / agent-down alert to the developer/admin (the dead agent cannot
report itself).

---

## POST /v1/errors

Immediate critical reports and periodic grouped summaries.

```json
{
  "agent_id": "...", "company_id": "...", "machine_name": "...",
  "company_name": "...", "category": "TallyPortUnavailable",
  "severity": "critical", "message": "...", "stack_trace": "...",
  "timestamp_utc": "...", "operation": "extract:vouchers",
  "dataset": "vouchers", "batch_id": null, "retry_count": 3,
  "agent_version": "1.0.0", "is_summary": false, "occurrences": 1
}
```

**200** `{ "ok": true }`. Server fans out to: admin email (`notifications.adminEmail`
registered with the agent), Google Chat/Slack, and the monitoring dashboard.
Category values are the fixed taxonomy listed in ARCHITECTURE.md §10.

---

## GET /v1/updates/check?current=1.0.0&channel=production

Controlled update channel — return only versions **approved for that channel**.

**204** no update · **200**:

```json
{ "version": "1.1.0",
  "url": "https://storage.googleapis.com/agent-releases/TallyAgentSetup-1.1.0.exe",
  "sha256": "...", "mandatory": false }
```

Agent behaviour: download to temp → verify SHA-256 (+ Authenticode) → run the
installer silently (`/SILENT`) which stops the service, swaps binaries, preserves
ProgramData, restarts the service. A failed health check after update triggers
reinstall of the previous cached Setup.exe (rollback).
