# Tally BigQuery Agent — Solution Architecture

**Version:** 1.1 (incorporates the 7 mandatory pre-development corrections) · **Target:** Windows 10/11 & Windows Server 2016+ · **Runtime:** .NET 8 (self-contained)
**Service name:** `TallyBigQueryAgent` · **Display name:** `Tally BigQuery Data Sync Agent`

> **Revision note (v1.1):** ① MERGE/dedup keys use stable source keys only — `_sync_id` is audit-only; ② batch IDs are deterministic and persisted (retries always reuse the same ID); ③ deleted/cancelled vouchers are tracked via status columns + layered reconciliation cadences — no physical deletes in the warehouse; ④ extraction schema is frozen only after the real-voucher validation matrix passes; ⑤ the GCS→BigQuery load pipeline is fully specified with an ingestion-control table and `accepted → loaded → processed / failed` statuses; ⑥ payload+checkpoint writes follow a strict crash-safe ordering with startup recovery; ⑦ financial reconciliation checks land in BigQuery monitoring tables.

---

## 1. Complete Solution Architecture

### 1.1 High-level component view

```
┌─────────────────────────────── WINDOWS MACHINE (Tally server) ───────────────────────────────┐
│                                                                                              │
│  ┌──────────────┐   XML/HTTP    ┌────────────────────────────────────────────────────────┐   │
│  │  TallyPrime  │◄──────────────┤              TallyBigQueryAgent (Windows Service)      │   │
│  │  port 9000   │  TDL Export   │                                                        │   │
│  └──────────────┘               │  ┌──────────────┐  ┌─────────────┐  ┌───────────────┐  │   │
│                                 │  │ SyncWorker   │  │UploadWorker │  │HeartbeatWorker│  │   │
│  ┌──────────────┐               │  │ (extract →   │  │ (queue →    │  │ (5 min pulse) │  │   │
│  │ Management   │  named-pipe/  │  │  batch →     │  │  HTTPS POST │  └───────────────┘  │   │
│  │ App (WPF)    │◄─────────────►│  │  enqueue)    │  │  w/ retry)  │  ┌───────────────┐  │   │
│  │ Start Menu   │  SQLite +     │  └──────┬───────┘  └──────┬──────┘  │ErrorSummary   │  │   │
│  └──────────────┘  SCM API      │         │                 │         │Worker (dedup) │  │   │
│                                 │         ▼                 ▼         └───────────────┘  │   │
│  ┌──────────────┐               │  ┌──────────────────────────────────────────────────┐  │   │
│  │ Installer    │               │  │   SQLite  C:\ProgramData\TallyBigQueryAgent\     │  │   │
│  │ (Inno Setup) │               │  │   agent.db  (queue, checkpoints, errors, logs)   │  │   │
│  └──────────────┘               │  └──────────────────────────────────────────────────┘  │   │
│                                 └───────────────────────────┬────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┼────────────────────────────────┘
                                                              │ HTTPS only (TLS ≥ 1.2, outbound)
                                                              ▼
        ┌────────────────────┐   ┌─────────────┐   ┌───────────────────┐   ┌──────────────────┐
        │ Cloud Ingestion API │──►│  Cloud Run  │──►│ GCS raw layer     │──►│ BigQuery staging │
        │ (auth: agent token) │   └─────────────┘   │ (NDJSON.gz files) │   │ → MERGE → wh     │
        └────────────────────┘                      └───────────────────┘   └──────────────────┘
                    ▲
                    │ heartbeats / error reports
        ┌───────────┴─────────┐
        │ Monitoring dashboard │ → Email / Google Chat / Slack alerts to developer-admin
        └─────────────────────┘
```

### 1.2 Design principles

1. **The agent never talks to BigQuery.** It only speaks HTTPS to the ingestion API with a per-agent token. No GCP service-account key ever touches the Windows machine.
2. **Extract and upload are decoupled.** `SyncWorker` extracts from Tally and writes gzip-compressed NDJSON batches into a SQLite-backed durable queue. `UploadWorker` drains the queue independently. Tally being up and internet being up are independent failure domains.
3. **Everything is idempotent.** Batch IDs are deterministic (created once, persisted in SQLite, reused verbatim on every retry) and the warehouse MERGE layer dedupes on **stable source keys only** (`source_company_id` + voucher GUID / master ID — see §9.1). `_sync_id` is **never** part of any MERGE or dedup key — it changes every sync and would create duplicates; it is retained purely as an audit column. Re-uploading any batch is therefore a no-op at every layer.
4. **Crash-safe by construction.** Every state transition (checkpoint advance, batch enqueue, batch ack) is a SQLite transaction. The service can be killed at any instant and resume correctly.
5. **Least privilege.** Service runs as `LocalService` account (network-capable, minimal rights) with explicit write ACLs only on `C:\ProgramData\TallyBigQueryAgent`.

### 1.3 Process/component inventory

| Component | Binary | Runs as | Purpose |
|---|---|---|---|
| Agent service | `TallyAgent.Service.exe` | Windows Service (`LocalService`) | Extraction, queueing, upload, heartbeat, error reporting |
| Management app | `TallyAgent.Manager.exe` | Interactive user (Start Menu) | Status, tests, service control, sync-now, diagnostics |
| Config/CLI tool | `TallyAgent.Cli.exe` | Installer / admin console | `test-tally`, `test-cloud`, `save-config`, `sync-now`, `export-diag` |
| Installer | `Tally BigQuery Agent Setup.exe` | Elevated | Install, configure, verify, register service |

---

## 2. Folder & Project Structure

```
tally-bigquery-agent/
├── TallyBigQueryAgent.sln
├── Directory.Build.props                  # shared version, nullable, analyzers
├── src/
│   ├── TallyAgent.Core/                   # class library — all business logic
│   │   ├── Configuration/
│   │   │   ├── AgentConfig.cs             # POCO config model (validated)
│   │   │   ├── ConfigStore.cs             # load/save JSON @ ProgramData, DPAPI fields
│   │   │   └── ConfigValidator.cs
│   │   ├── Security/
│   │   │   ├── DpapiProtector.cs          # ProtectedData wrapper (LocalMachine scope)
│   │   │   └── SecretMasker.cs            # masks tokens in any log output
│   │   ├── Data/
│   │   │   ├── AgentDatabase.cs           # SQLite bootstrap + migrations (schema_version)
│   │   │   ├── BatchQueueRepository.cs    # enqueue/dequeue/ack/fail/archive
│   │   │   ├── CheckpointRepository.cs
│   │   │   ├── ErrorLogRepository.cs
│   │   │   └── HeartbeatRepository.cs
│   │   ├── Tally/
│   │   │   ├── TallyClient.cs             # HttpClient, POST XML, retry, sanitize
│   │   │   ├── TallyEnvelopes.cs          # TDL collection + report envelope builders
│   │   │   ├── TallyXml.cs                # tolerant parse helpers (num/date/bool/text)
│   │   │   ├── Extractors/
│   │   │   │   ├── MasterExtractor.cs     # 13 master collections
│   │   │   │   ├── VoucherExtractor.cs    # Day Book → headers/lines/allocs/inventory
│   │   │   │   ├── ReportExtractor.cs     # TB, BS, P&L, registers, outstanding, stock
│   │   │   │   └── DatasetRegistry.cs     # dataset → extractor + mode + needs-dates
│   │   │   └── TallyException.cs
│   │   ├── Sync/
│   │   │   ├── SyncEngine.cs              # orchestrates full/incremental cycles
│   │   │   ├── SyncPlan.cs                # date windows, lookback, dataset selection
│   │   │   └── BatchBuilder.cs            # rows → NDJSON → gzip → checksum → queue
│   │   ├── Cloud/
│   │   │   ├── IngestionApiClient.cs      # /v1/batches, /v1/heartbeat, /v1/errors
│   │   │   ├── ApiModels.cs               # request/response DTOs
│   │   │   └── RetryPolicy.cs             # exp backoff + jitter, Retry-After aware
│   │   ├── Notifications/
│   │   │   ├── ErrorReporter.cs           # categorise, throttle, aggregate, dispatch
│   │   │   ├── EmailNotifier.cs           # SMTP (optional, via cloud API by default)
│   │   │   └── WebhookNotifier.cs         # Google Chat / Slack / generic JSON
│   │   ├── Diagnostics/
│   │   │   ├── DiagnosticsExporter.cs     # sanitized ZIP bundle
│   │   │   └── SystemInfo.cs              # disk, memory, OS, connectivity probes
│   │   └── AgentVersion.cs
│   ├── TallyAgent.Service/                # Windows Service host
│   │   ├── Program.cs                     # Host builder, UseWindowsService, Serilog
│   │   ├── Workers/
│   │   │   ├── SyncWorker.cs              # timer loop: extract → enqueue
│   │   │   ├── UploadWorker.cs            # drain queue → ingestion API
│   │   │   ├── HeartbeatWorker.cs         # every 5 min
│   │   │   └── ErrorSummaryWorker.cs      # grouped non-critical error digests
│   │   └── appsettings.json
│   ├── TallyAgent.Cli/                    # console: installer & admin verbs
│   │   └── Program.cs                     # test-tally | test-cloud | save-config |
│   │                                      # sync-now | retry-failed | export-diag | status
│   └── TallyAgent.Manager/                # WPF management app (net8.0-windows)
│       ├── App.xaml / MainWindow.xaml     # dashboard UI
│       ├── ViewModels/MainViewModel.cs
│       └── Services/ServiceController.cs  # SCM control + SQLite status reads
├── installer/
│   ├── TallyBigQueryAgent.iss             # Inno Setup 6 script (custom config wizard)
│   └── assets/ (icon.ico, banner.bmp)
├── build/
│   ├── build.ps1                          # publish self-contained + compile installer
│   └── sign.ps1                           # signtool wrapper (optional cert)
├── docs/
│   ├── ARCHITECTURE.md                    # this file
│   ├── CLOUD_API_CONTRACT.md              # OpenAPI-style contract for ingestion API
│   └── INSTALL.md                         # build & install runbook
└── README.md
```

---

## 3. Data Flow Diagram

### 3.1 Steady-state sync cycle

```
 ┌────────────┐    1. timer fires (sync_frequency_minutes, default 15)
 │ SyncWorker │───────────────────────────────────────────────────────┐
 └────────────┘                                                       ▼
   2. Preflight: Tally reachable? company open? disk ok?      ┌──────────────┐
      └─ fail → categorized error → ErrorReporter             │ TallyClient  │
   3. Build SyncPlan:                                         └──────┬───────┘
      • first run  → FULL:  masters + vouchers                       │ XML over HTTP
        from extraction_start_date → today                           ▼
      • later runs → INCREMENTAL:                             ┌──────────────┐
        vouchers  [today − lookback_days … today]             │  TallyPrime  │
        masters   re-snapshot (cheap, full)                   └──────────────┘
        historical windows chunked month-by-month
        + layered cadences (§8.3): nightly 7-day refresh,
          monthly FY reconciliation, periodic full GUID sweep
   4. For each dataset in plan — STRICT crash-safe ordering (see §6.1):
        extract rows → BatchBuilder
        → (1) write NDJSON+gzip to *.tmp file  (+ _sync_timestamp, _sync_id [audit-only],
              _company, source_company_id, is_cancelled, source_status, source_last_seen_at)
        → (2) flush + close file
        → (3) compute SHA-256 checksum of final bytes
        → (4) atomic rename *.tmp → {batch_id}.ndjson.gz
        → (5) BEGIN SQLite transaction
        → (6) INSERT upload_batches row (status='pending', deterministic batch_id)
        → (7) advance sync checkpoint
        → (8) COMMIT
                                                     (extraction never blocks on network)
 ┌──────────────┐   5. poll pending batches (oldest first, per-dataset sequence order)
 │ UploadWorker │──────────────────────────────────────────────────────────────────┐
 └──────────────┘                                                                  ▼
   6. POST /v1/batches  (gzip NDJSON body, metadata headers/envelope)     ┌─────────────────┐
      ├─ 200 {status:"accepted"|"duplicate"} → mark 'acked' → archive     │ Ingestion API   │
      ├─ 401/403 → CRITICAL auth error, pause uploads, alert              │ (Cloud Run)     │
      ├─ 409 duplicate → treat as acked (idempotent)                      └────────┬────────┘
      ├─ 5xx / network → exp backoff (1m→2m→4m…cap 30m) + retry_count++            │
      └─ 400 schema → mark 'failed', alert, keep payload for diagnosis             ▼
                                                                          GCS raw → BQ staging
 ┌──────────────────┐  every 5 min                                        → MERGE → warehouse
 │ HeartbeatWorker  │─────────────► POST /v1/heartbeat {status, queue depth, tally state,
 └──────────────────┘                disk, memory, last sync, last error}
```

### 3.2 Offline behaviour

```
Internet down ──► UploadWorker retries with backoff; queue grows in SQLite (disk-guarded)
Tally down    ──► SyncWorker logs categorized error, skips cycle, retries next tick
Both down     ──► service idles cheaply; heartbeat buffered locally (heartbeat table)
Recovery      ──► queue drains in sequence order; buffered heartbeats summarized
```

---

## 4. SQLite Schema

Database: `C:\ProgramData\TallyBigQueryAgent\agent.db` (WAL mode, `busy_timeout=5000`).
Migrations tracked in `schema_meta`; every migration is additive and versioned.

```sql
PRAGMA journal_mode = WAL;

CREATE TABLE schema_meta (
  key   TEXT PRIMARY KEY,          -- 'schema_version', 'agent_version', 'installed_at'
  value TEXT NOT NULL
);

CREATE TABLE agent_config (        -- non-secret runtime copy of config (secrets DPAPI-blob)
  key        TEXT PRIMARY KEY,
  value      TEXT NOT NULL,
  is_secret  INTEGER NOT NULL DEFAULT 0,   -- 1 → value is base64(DPAPI blob)
  updated_at TEXT NOT NULL
);

CREATE TABLE sync_checkpoints (
  dataset          TEXT NOT NULL,
  company          TEXT NOT NULL,
  last_from_date   TEXT,           -- ISO date of last extracted window start
  last_to_date     TEXT,           -- ISO date of last extracted window end
  last_alter_id    INTEGER,        -- Tally ALTERID high-water mark (when available)
  last_success_utc TEXT,           -- last successful extraction timestamp
  full_sync_done   INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (dataset, company)
);

CREATE TABLE upload_batches (
  batch_id         TEXT PRIMARY KEY,      -- DETERMINISTIC, created once and stored here:
                                          -- {agent_id}-{company_id}-{dataset}-{window_from}-
                                          -- {window_to}-{sequence}-{sha256(payload)[:12]}
                                          -- every retry reuses this exact value
  dataset          TEXT NOT NULL,
  company          TEXT NOT NULL,
  sequence_no      INTEGER NOT NULL,      -- monotonic per dataset
  sync_id          TEXT NOT NULL,
  extract_start_utc TEXT NOT NULL,
  extract_end_utc  TEXT NOT NULL,
  window_from      TEXT,                  -- data date window (vouchers)
  window_to        TEXT,
  record_count     INTEGER NOT NULL,
  payload_path     TEXT NOT NULL,         -- ProgramData\...\queue\{batch_id}.ndjson.gz
  payload_bytes    INTEGER NOT NULL,
  checksum_sha256  TEXT NOT NULL,
  schema_version   TEXT NOT NULL,
  control_totals   TEXT,                  -- JSON: record_count, debit_total, credit_total,
                                          -- amount_total per dataset — drives cloud-side
                                          -- reconciliation (§13)
  status           TEXT NOT NULL DEFAULT 'pending',
                   -- pending | uploading | acked | failed | archived
  retry_count      INTEGER NOT NULL DEFAULT 0,
  next_attempt_utc TEXT,
  last_error       TEXT,
  created_utc      TEXT NOT NULL,
  acked_utc        TEXT
);
CREATE INDEX ix_batches_status ON upload_batches(status, next_attempt_utc);
CREATE INDEX ix_batches_dataset ON upload_batches(dataset, sequence_no);

CREATE TABLE batch_history (               -- completed/archived summary (payload deleted)
  batch_id       TEXT PRIMARY KEY,
  dataset        TEXT NOT NULL,
  record_count   INTEGER NOT NULL,
  status         TEXT NOT NULL,            -- acked | failed-final
  created_utc    TEXT NOT NULL,
  completed_utc  TEXT NOT NULL,
  retry_count    INTEGER NOT NULL
);

CREATE TABLE sync_runs (
  sync_id       TEXT PRIMARY KEY,
  mode          TEXT NOT NULL,             -- full | incremental | manual
  started_utc   TEXT NOT NULL,
  finished_utc  TEXT,
  status        TEXT NOT NULL,             -- running | success | partial | failed
  datasets_json TEXT,
  rows_total    INTEGER DEFAULT 0,
  error_message TEXT
);

CREATE TABLE error_log (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  ts_utc        TEXT NOT NULL,
  category      TEXT NOT NULL,             -- fixed taxonomy (see §10)
  severity      TEXT NOT NULL,             -- critical | error | warning
  message       TEXT NOT NULL,
  stack_trace   TEXT,
  operation     TEXT,
  dataset       TEXT,
  batch_id      TEXT,
  retry_count   INTEGER DEFAULT 0,
  reported      INTEGER NOT NULL DEFAULT 0, -- sent to cloud/notifier?
  group_key     TEXT                        -- category+dataset hash for aggregation
);
CREATE INDEX ix_error_group ON error_log(group_key, ts_utc);

CREATE TABLE heartbeat_history (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  ts_utc       TEXT NOT NULL,
  delivered    INTEGER NOT NULL DEFAULT 0, -- buffered while offline
  payload_json TEXT NOT NULL
);

CREATE TABLE maintenance_schedule (        -- drives the layered sync cadences (§8.3)
  job          TEXT PRIMARY KEY,           -- 'nightly_refresh' | 'monthly_fy_recon'
                                           -- | 'full_guid_recon'
  last_run_utc TEXT,
  last_status  TEXT
);
```

Batch payloads live as files under `ProgramData\TallyBigQueryAgent\queue\`; SQLite stores metadata only — keeps the DB small and payload IO streamable.

---

## 5. Configuration Model

`C:\ProgramData\TallyBigQueryAgent\config.json` — written by installer/CLI/manager, readable only by SYSTEM + Administrators + LocalService (ACL set at install). Secret-valued fields are stored as `dpapi:<base64>` using **DPAPI LocalMachine scope** (service and interactive tools on same machine can decrypt; file copied off-machine is useless).

```jsonc
{
  "schemaVersion": "1.0",
  "tally": {
    "host": "127.0.0.1",
    "port": 9000,
    "company": "Dynel Electric Private Limited",
    "extractionStartDate": "2023-04-01",
    "syncFrequencyMinutes": 15,
    "requestTimeoutSeconds": 120,
    "autoDiscoverCompanies": true,
    "enableMasters": true,
    "enableVouchers": true,
    "enableInventory": true,
    "enableGst": true,
    "enableCostCentres": true,
    "incrementalLookbackDays": 7,
    "fullSyncChunkDays": 31
  },
  "cloud": {
    "ingestionApiUrl": "https://ingest.example.com",
    "agentId": "TALLY-SERVER-01",
    "companyId": "dynel-electric",
    "apiToken": "dpapi:AQAAANCMnd8B...",       // ← encrypted at rest
    "environment": "Production",                // Development | Testing | Production
    "uploadBatchMaxRecords": 5000,
    "heartbeatMinutes": 5
  },
  "notifications": {
    "adminEmail": "dev@example.com",
    "enableEmailAlerts": true,
    "errorWebhookUrl": "dpapi:...",             // webhooks may embed tokens → encrypted
    "googleChatWebhookUrl": "dpapi:...",
    "slackWebhookUrl": "dpapi:...",
    "criticalAlertCooldownMinutes": 30,
    "summaryIntervalMinutes": 60
  },
  "advanced": {
    "logLevel": "Information",
    "queueDiskLimitMb": 2048,
    "minFreeDiskMb": 500,
    "maxUploadRetryMinutes": 30
  }
}
```

Validation rules (enforced by `ConfigValidator` on load and by installer before save): port 1–65535; URL must be `https://` unless environment = Development; frequency 5–1440; lookback 0–90; agentId/companyId `[A-Za-z0-9._-]{1,64}`.

---

## 6. Windows Service Design

* Host: `Host.CreateApplicationBuilder` + `.UseWindowsService()` (`Microsoft.Extensions.Hosting.WindowsServices`). SCM name `TallyBigQueryAgent`.
* **Four `BackgroundService` workers**, independent failure domains, all wired with DI:
  * `SyncWorker` — `PeriodicTimer(syncFrequencyMinutes)`; also listens on a local trigger file/pipe for "Sync now" from the manager app.
  * `UploadWorker` — polls `upload_batches` every 15 s; respects `next_attempt_utc`; serialises per-dataset by `sequence_no`.
  * `HeartbeatWorker` — every 5 min; buffers to `heartbeat_history` when offline and marks delivered when the API acks.
  * `ErrorSummaryWorker` — hourly digest of grouped non-critical errors; immediate dispatch path bypasses it for criticals.
* Logging: **Serilog** → rolling files (`Logs\agent-.log`, 31-day retention, 10 MB size cap) **plus** Windows Event Log (source `TallyBigQueryAgent`, created by installer) for lifecycle + errors.
* Unhandled exception policy: top-level try/catch in each worker loop → categorized error → 30 s pause → continue. Host crash → SCM recovery restarts (below).
* Recovery settings (applied by installer via `sc.exe failure`):
  `sc failure TallyBigQueryAgent reset= 86400 actions= restart/60000/restart/300000/restart/900000`
  (1 min / 5 min / 15 min, counter resets daily) + `sc failureflag ... 1` so recovery also triggers on non-crash exits.
* Account: `NT AUTHORITY\LocalService` + explicit `SeServiceLogonRight`; full-control ACL granted on `C:\ProgramData\TallyBigQueryAgent` at install. Outbound HTTP allowed by default for LocalService.
* Shutdown: `StopAsync` honours a 30 s grace period — finishes the in-flight batch write (SQLite transaction) and exits; never leaves a torn payload (temp-file + atomic rename).

### 6.1 Crash-safe payload + checkpoint protocol (mandatory ordering)

The payload file and the SQLite transaction cannot be committed atomically together, so ordering carries the correctness:

1. Write the payload to `{batch_id}.ndjson.gz.tmp`.
2. Flush and close the file handle.
3. Compute the SHA-256 checksum from the closed file.
4. Atomically rename `*.tmp` → `{batch_id}.ndjson.gz`.
5. `BEGIN` SQLite transaction.
6. `INSERT` the upload-batch record (with checksum + deterministic batch_id).
7. Advance the extraction checkpoint.
8. `COMMIT`.

A crash between 4 and 8 leaves an orphan payload with no DB row — harmless, swept at startup; the window re-extracts because the checkpoint did not advance. A crash before 4 leaves only a `.tmp` — deleted at startup. There is no ordering in which data is lost or double-counted.

**Service startup recovery (every start):**

* delete all stale `*.tmp` files in the queue directory;
* delete orphan payload files that have no `upload_batches` row;
* mark `failed` any `upload_batches` row whose payload file is missing;
* reset batches stuck in `uploading` back to `pending`;
* verify each pending payload's SHA-256 against its stored checksum **before upload** — a mismatch marks the batch `failed` (corruption) and raises a `LocalDatabaseFailure` alert rather than shipping bad bytes.

---

## 7. Installer Design (Inno Setup 6)

Output: `Tally BigQuery Agent Setup.exe` (single EXE, ~40–70 MB self-contained — no .NET runtime prerequisite, satisfying "install runtime if necessary" by embedding it).

Wizard flow:

```
Welcome → License → Install dir
  → [Custom page 1] Tally Settings   (host, port, company, start date, frequency,
                                      dataset toggles, auto-discover checkbox + "Detect" button)
  → [Custom page 2] Cloud Settings   (API URL, Agent ID, Company ID, token (masked), environment)
  → [Custom page 3] Notifications    (admin email, email alerts on/off, webhook URLs)
  → [Verification page] "Test connections"
        runs TallyAgent.Cli test-tally  → shows companies found / actionable error
        runs TallyAgent.Cli test-cloud  → validates URL + token against /v1/ping
        (user may Continue Anyway → recorded as unverified install)
  → Install files → Cli save-config (encrypts secrets via DPAPI)
  → sc create/config + failure actions + eventlog source → sc start
  → Finish (checkbox: Launch management app)
```

Installer behaviours:

* **Upgrade:** same AppId → detects existing install; stops service, replaces binaries, **preserves** `ProgramData` (config, queue, checkpoints, logs), runs migrations on next service start, restarts service. Version stored in registry for Apps & Features.
* **Uninstall:** stops + deletes service, removes binaries and Start Menu entries; final dialog offers **"Keep configuration, queue and logs"** (default yes) vs full purge of `ProgramData\TallyBigQueryAgent`.
* **Repair:** re-running the same-version installer offers Repair (re-copies binaries, re-applies service + ACL + recovery settings, keeps config).
* Closing the installer after completion has zero effect on the running service (service is SCM-owned).
* Both `Setup.exe` and binaries are Authenticode-signed when a cert is configured in `sign.ps1`.

---

## 8. Tally XML Extraction Strategy

Transport: `POST http://{host}:{port}` with `Content-Type: text/xml`, body = TDL envelope. Responses are sanitised before parse (UTF-16 BOM handling, illegal XML char ranges, malformed `&#N;` entities — Tally emits all three; patterns ported from the proven Python connector).

Two request families:

1. **TDL Collections** (masters): `<TYPE>Ledger</TYPE>` + explicit `<FETCH>` list per dataset — Companies, Groups, Ledgers, VoucherTypes, CostCentres, CostCategories, Currencies, Units, StockGroups, StockItems (incl. nested `GSTDETAILS.LIST` → HSN/rate with older-Tally flat-field fallbacks), Godowns, plus standard-cost/price lists. **Every master fetch list includes `MASTERID`, `GUID` and `ALTERID`** — `master_id`/`master_guid` become the stable MERGE key (§9.1) and `ALTERID` the change-detection high-water mark; name fields remain attributes, not identity.
2. **Report exports** (`Export Data` + `REPORTNAME`): `Day Book` (with `SVFROMDATE`/`SVTODATE`/`SVCURRENTCOMPANY`) is the single source for all voucher-level datasets — one fetch per window fans out in-memory to: voucher headers, flat voucher lines, bill allocations, bank allocations, cost-centre allocations (incl. `CATEGORYALLOCATIONS` nesting), inventory entries, sales/purchase registers with CGST/SGST/IGST split, and sales invoice lines. `Trial Balance`, `Balance Sheet`, `Profit and Loss A/c`, `Stock Summary`, and outstanding reports use their native report names with the dual TallyPrime/legacy parse paths (`DSPACCNAME`/`DSPDISPNAME` sibling-pair walk).

All voucher types listed in the spec (sales, purchase, receipt, payment, journal, contra, credit/debit notes, stock journals, delivery/receipt notes, purchase/sales orders, physical stock) arrive through Day Book; the agent tags each row with its `VOUCHERTYPENAME` and never filters server-side — filtering happens in BigQuery, keeping extraction simple and complete.

**Windowing:** full sync chunks the range `[extractionStartDate → today]` into ≤31-day windows (Tally responses degrade on huge ranges); each window becomes its own batch, so a crash mid-history resumes at the last checkpointed window. Where `ALTERID` is available it is recorded as a high-water mark and used to skip unchanged master re-uploads.

### 8.1 Voucher lifecycle columns (edits, cancellations, deletions)

Every voucher-derived row carries lifecycle columns so the warehouse can track state without physical deletes:

| Column | Meaning |
|---|---|
| `is_cancelled` | voucher's `ISCANCELLED` flag as seen in Tally |
| `is_deleted` | set by reconciliation when a previously-seen GUID no longer exists in Tally (deleted vouchers vanish from Day Book — they can only be detected by GUID comparison, never by re-extraction alone) |
| `source_status` | `active` \| `cancelled` \| `deleted` \| `optional` |
| `source_last_seen_at` | UTC timestamp of the last extraction in which this GUID appeared |

Raw records are **never physically removed** from the warehouse; reconciliation updates their current status in place.

### 8.2 Voucher GUID manifest

A lightweight companion dataset `voucher_guid_manifest` (`source_company_id, guid, voucher_date, voucher_type, alter_id?, is_cancelled, extracted_at`) is produced for each reconciled window. The cloud side diffs the manifest against warehouse GUIDs for that window: warehouse-GUID present but manifest-GUID absent ⇒ mark `is_deleted = true, source_status = 'deleted'`.

### 8.3 Layered sync cadences

The 7-day lookback alone cannot catch late edits/deletions of older vouchers, so four cadences run in layers (schedule state in the `maintenance_schedule` table):

| Cadence | Frequency | Window | Purpose |
|---|---|---|---|
| Incremental sync | every 15 min (configurable) | `[today − lookbackDays … today]` | new + recently edited vouchers |
| Recent-data refresh | nightly (~01:30 local) | last 7 days, forced re-extract + manifest | catch same-week edits/cancellations authoritatively |
| Current-FY reconciliation | monthly | financial year to date | GUID manifest + control totals vs warehouse; repairs drift, detects deletions |
| Full voucher GUID reconciliation | periodic (default quarterly, configurable) | `extractionStartDate → today` | GUID-only sweep (cheap — no line data) to catch deletions/edits older than the FY window |

### 8.4 Extraction validation protocol (schema freeze gate)

Do **not** assume one standard Day Book export exposes every required nested field across Tally builds. Before the extraction schema is frozen and any production data flows, the following real-voucher matrix must pass against a live TallyPrime company:

1. Sales invoice with inventory lines **and** GST ledgers
2. Purchase voucher
3. Payment with bill allocation
4. Receipt with bank allocation
5. Journal with cost-centre allocation
6. Credit note and debit note
7. Stock journal
8. Purchase order and sales order
9. Cancelled voucher
10. Voucher with multiple ledger **and** multiple inventory lines

For each case, compare three artefacts side by side: **Tally screen → raw XML response (persisted to a fixtures folder) → parsed agent output**, verifying every field the schema workbook requires (amounts, GST split, allocations, godowns, GUIDs, flags). The CLI ships a `capture-xml` verb to dump raw responses for this purpose. Only when all 10 cases reconcile field-for-field is the extraction schema frozen (schema_version 1.0); any gap found (e.g. a nested list missing from the default Day Book) is closed by extending the TDL request **before** go-live, not patched in production.

Preflight per cycle: TCP probe → company-list request → configured company present? Each failure maps to its own error category (§10) with actionable text ("Enable XML server: F1 → Settings → Connectivity…").

---

## 9. Cloud API Contract (agent ↔ ingestion API)

Full contract in `docs/CLOUD_API_CONTRACT.md`. Summary:

* Auth: `Authorization: Bearer <agent token>`; `X-Agent-Id`, `X-Environment` headers on every call. TLS ≥ 1.2, certificate validation always on (no dev bypass in Production builds).
* `GET  /v1/ping` → `{ "ok": true, "server_time": "..." }` — used by installer test & connectivity probe.
* `POST /v1/batches` — body: gzip NDJSON (`Content-Encoding: gzip`, `Content-Type: application/x-ndjson`). Envelope metadata in headers: `X-Batch-Id`, `X-Dataset`, `X-Company`, `X-Company-Id`, `X-Sequence`, `X-Sync-Id`, `X-Record-Count`, `X-Checksum-Sha256`, `X-Schema-Version`, `X-Agent-Version`, `X-Extract-Start`, `X-Extract-End`, `X-Retry-Count`, `X-Control-Totals` (§13.1).
  Responses: `200 {"status":"accepted","batch_id":...}` · `200/409 {"status":"duplicate"}` (already ingested — agent treats as acked) · `400 {"status":"rejected","errors":[...]}` (schema mismatch → failed-final + alert) · `401/403` auth · `413` too large (agent splits batch and retries) · `429/5xx` retry with backoff honouring `Retry-After`.
* `POST /v1/heartbeat` — JSON body with the full §Monitoring field list; response may include `{"commands":[{"type":"sync_now"|"update","version":...}]}` enabling server-initiated sync and the controlled-update channel.
* `POST /v1/errors` — single or `{"summary":true,"occurrences":N}` grouped reports; cloud side fans out to email/Chat/Slack and the dashboard, so the agent needs no SMTP credentials by default (direct SMTP/webhook remains available as fallback).
* `GET /v1/updates/check?current=1.0.0&channel=stable` → `{version, url, sha256, signature}` — updater downloads to temp, verifies SHA-256 + Authenticode, then hands off to the updater script (stop service → swap → start → health check → rollback on failure). Only versions approved for the agent's environment are offered.

### 9.1 MERGE / deduplication keys (stable source keys ONLY)

`_sync_id` and `_sync_timestamp` are **audit columns only** — they change every sync and must never appear in a MERGE or dedup key. The warehouse merges on:

| Data class | MERGE key |
|---|---|
| Vouchers (headers, flat vouchers, day_book) | `source_company_id + voucher_guid` |
| Masters (ledgers, groups, stock items, …) | `source_company_id + master_id` (or `source_company_id + master_guid`) — Tally `MASTERID`/`GUID` fetched with every master; names are attributes and may be renamed without creating duplicates |
| Voucher child records (lines, bill/bank/cost-centre allocations, inventory entries, invoice lines) | `source_company_id + voucher_guid + entry_type + stable_line_identifier` — `entry_type` distinguishes the child table; `stable_line_identifier` is the 0-based line ordinal within the voucher for that entry type (Tally preserves line order per GUID), so a re-extracted voucher **replaces** its child set deterministically (delete-and-insert per voucher GUID inside the MERGE, keyed by the tuple) |
| SNAPSHOT tables | replaced per `source_company_id` per sync (WRITE_TRUNCATE semantics) |

Recency between two versions of the same key is decided by `source_last_seen_at` / load time of the batch — not `_sync_id`.

### 9.2 Deterministic batch identity

`batch_id` is generated **once** at enqueue time from stable values and persisted in SQLite:

```
batch_id = {agent_id}-{company_id}-{dataset}-{window_from}-{window_to}-{sequence}-{sha256(payload)[:12]}
```

No wall-clock component. Every upload retry — minutes or days later, before or after service restarts — reuses the stored `batch_id` byte-for-byte, so API-level dedupe and GCS object keying are reliable.

*Implemented (Phase A-1):* `BatchIdentity.Compute` + `BatchBuilder` ordering (tmp → flush/close → checksum → derive id → atomic rename → enqueue); per-dataset sequences draw from both `upload_batches` and `batch_history` (schema v2 adds `sequence_no` to history) so they never regress after acks; `TryEnqueue` treats a batch_id collision (byte-identical re-extraction) as a silent no-op. Batches created by older builds keep their stored IDs — IDs are read from SQLite, never recomputed.

### 9.3 GCS → BigQuery loading pipeline (Cloud Run responsibilities)

```
Windows Agent → Cloud Run Ingestion API → GCS raw file → BigQuery load job
             → staging tables → MERGE into warehouse   (+ ingestion_control tracking)
```

On every `POST /v1/batches`, Cloud Run must, in order:

1. **Authenticate** the agent (token ↔ agent_id ↔ company_id binding).
2. **Validate** batch metadata + recompute the payload SHA-256 against `X-Checksum-Sha256` (mismatch ⇒ 400, agent alerts).
3. **Store** the raw file at `gs://{raw-bucket}/{company_id}/{dataset}/{batch_id}.ndjson.gz` (object key = batch_id ⇒ replays overwrite identically).
4. **Register** the batch in the `ingestion_control` BigQuery table (insert-or-detect-duplicate on `batch_id` — a duplicate returns `{"status":"duplicate"}` without reprocessing):
   `batch_id, agent_id, company_id, dataset, sequence, window_from, window_to, record_count, checksum, schema_version, agent_version, control_totals, status, received_at, loaded_at, processed_at, error`
5. **Start or queue** the BigQuery load job (GCS → `stg_{dataset}`), then the staging→warehouse MERGE (§9.1), advancing `ingestion_control.status` through the lifecycle.

Batch lifecycle statuses in `ingestion_control`:

| Status | Meaning |
|---|---|
| `accepted` | raw file safely stored in Cloud Storage (this — and only this — is what the 200 to the agent asserts) |
| `loaded` | BigQuery load job completed into the staging table |
| `processed` | staging data merged into warehouse tables |
| `failed` | any step failed — `error` populated, retried/alerted server-side; the raw GCS file is retained for replay |

The agent's responsibility ends at `accepted`; `loaded`/`processed`/`failed` are server-side concerns surfaced on the monitoring dashboard, and stalled batches (`accepted` but not `processed` beyond SLA) alert the developer/admin.

**Duplicate-proofing chain:** deterministic persisted `batch_id` → API/`ingestion_control` dedupe on `batch_id` → GCS object keyed by batch_id (overwrite-idempotent) → staging→warehouse `MERGE` on the stable source keys of §9.1. Any single layer failing still cannot produce duplicate warehouse rows.

---

## 10. Error & Retry Strategy

Fixed category taxonomy (string enum used in SQLite, heartbeat, alerts):
`TallyNotRunning, TallyPortUnavailable, TallyCompanyNotOpen, TallyInvalidXml, TallyTimeout, InternetUnavailable, CloudApiUnavailable, AuthenticationFailure, UploadFailure, LocalDatabaseFailure, DiskSpaceLow, SchemaMismatch, ServiceStopped, UnexpectedException`

| Concern | Policy |
|---|---|
| Tally fetch | 3 attempts, backoff 10 s / 30 s / 60 s; per-dataset isolation (one dataset failing never stops others) |
| Upload | infinite retries, exponential 1→2→4→…→cap 30 min + ±20 % jitter; `429`/`Retry-After` honoured; `401/403` **pauses** the upload pump (auth alerts, no hammering); `400` → failed-final (needs human) |
| Severity | `critical` (auth failure, DB corruption, disk full, schema mismatch, service-stop) → immediate dispatch, cooldown 30 min per group; `error`/`warning` → grouped by `group_key` and summarised hourly ("Tally timeout ×14 in last hour") |
| Notification path | cloud `/v1/errors` primary; direct webhooks/SMTP fallback when the cloud API itself is the failing component |
| Watchdog | heartbeat absence >15 min triggers the *server-side* "agent down / ServiceStopped" alert — covers kill-switch cases the agent can't self-report |

---

## 11. Security Design

* **Secrets:** DPAPI (`ProtectedData`, LocalMachine scope + app-specific entropy) for API token and webhook URLs; never plaintext on disk, never logged (`SecretMasker` scrubs all log sinks), masked (`••••last4`) in UI.
* **No GCP credentials on the box** — only the revocable per-agent bearer token, scoped to one company's ingestion path, rate-limited server-side.
* **Transport:** outbound HTTPS only; TLS ≥1.2; full chain validation (no `ServerCertificateCustomValidationCallback` outside Development builds). Tally traffic stays on localhost/LAN; nothing listens on any inbound port.
* **Filesystem ACLs:** `ProgramData\TallyBigQueryAgent` restricted to SYSTEM/Administrators/LocalService; config unreadable to standard users.
* **Least privilege:** service account LocalService (not SYSTEM); management app requests elevation only for service start/stop.
* **Supply chain:** Authenticode signing of Setup.exe + all EXEs (sign.ps1); update packages verified by SHA-256 + signature before install; no auto-deploy of non-approved builds (server controls channel per environment).
* **Audit:** every config change, service lifecycle event, batch ack and alert lands in `error_log`/`sync_runs`/Event Log — a complete local audit trail; diagnostic ZIP export is sanitised (no tokens, no voucher payloads).

---

## 12. Implementation Phases

| Phase | Scope | Exit criteria |
|---|---|---|
| **P1 — Foundation** | Solution skeleton, Core config + DPAPI + SQLite migrations, Serilog, CLI `save-config` | config round-trips encrypted; db self-creates |
| **P2 — Tally layer + VALIDATION GATE** | TallyClient + sanitizer, master extractors (incl. MASTERID/GUID/ALTERID), Day Book fan-out, report extractors, `test-tally`, `capture-xml` fixtures | **the full §8.4 real-voucher matrix (10 cases) reconciles Tally screen ↔ raw XML ↔ parsed output field-for-field; extraction schema frozen only then** |
| **P3 — Sync + queue** | SyncEngine (full windows + incremental lookback), BatchBuilder with §6.1 crash-safe ordering, deterministic persisted batch IDs, queue repo, checkpoints, startup recovery sweep | kill-anytime crash test passes at every step boundary of §6.1; batches accumulate offline; retried uploads reuse identical batch_id |
| **P4 — Cloud path** | IngestionApiClient, UploadWorker, retry matrix, heartbeat, error reporter + notifiers; server side: `ingestion_control`, GCS layout, load jobs, §9.1 MERGEs, accepted→loaded→processed lifecycle | end-to-end rows visible in warehouse; duplicate re-upload proves idempotent at all four layers; a killed load job resumes without loss |
| **P5 — Service host** | Windows Service host, 4 workers + maintenance scheduler (§8.3 cadences), Event Log, recovery settings, `sync-now` trigger | reboot test: service auto-starts, resumes queue; nightly refresh + monthly recon fire on schedule |
| **P6 — Manager app** | WPF dashboard, tests, service control, retry-failed, diagnostics ZIP | all 10 buttons functional; closing app leaves service running |
| **P7 — Installer** | Inno script, config wizard pages, connection tests, upgrade/uninstall(+keep-data), signing | all 16 acceptance criteria pass on a clean VM |
| **P8 — Reconciliation + updates + hardening** | §13 reconciliation checks + monitoring tables + exception alerting, update check/verify/rollback, disk guards, soak test, pen-check of ACLs | recon detects a seeded edit/cancel/delete within one cadence cycle; 72 h soak: zero leaks, zero duplicate rows, update rollback proven |

---

## 13. Financial Reconciliation

A successful upload proves delivery, not correctness. Reconciliation continuously proves that the warehouse **matches Tally**.

### 13.1 Agent-side control totals

Every batch carries `control_totals` (JSON, also in the `X-Control-Totals` metadata): record count and, per dataset where meaningful, debit total, credit total, absolute amount total, and cancelled-voucher count for the window. Computed from parsed rows at extraction time — the same numbers Tally showed.

### 13.2 Reconciliation checks (cloud side, per company)

Run after each nightly refresh / monthly FY reconciliation (§8.3), comparing fresh Tally-derived figures against warehouse aggregates:

| Check | Comparison |
|---|---|
| Voucher counts | count by `voucher_date` × `voucher_type`: manifest vs warehouse |
| Double-entry integrity | Σ debit vs Σ credit per voucher and per day (must net to zero) |
| Sales register total | Tally sales register window total vs warehouse `tally_sales_register` |
| Purchase register total | same for purchases |
| Ledger balances | Tally closing balances (ledger master snapshot) vs warehouse-computed balances from voucher lines |
| GST totals | CGST/SGST/IGST sums per window vs registers |
| Stock quantity & value | Tally stock summary vs warehouse `tally_stock_summary` / inventory movements |
| Receivables / payables | outstanding snapshots vs warehouse bill-allocation aging |
| Cancelled voucher counts | cancelled per window: manifest vs warehouse `source_status='cancelled'` |
| Deleted voucher sweep | §8.2 manifest diff ⇒ `source_status='deleted'` updates (never physical deletes) |

### 13.3 Monitoring tables (BigQuery)

* `recon_runs(run_id, company_id, cadence, window_from, window_to, started_at, finished_at, status, checks_total, checks_passed)`
* `recon_results(run_id, check_name, dataset, expected_value, actual_value, difference, status)` — one row per check, kept for trend analysis
* `recon_exceptions(run_id, check_name, company_id, entity_key, detail, severity, resolved, resolved_at)` — actionable drill-down rows (e.g. the specific GUIDs whose totals disagree)

Failed checks raise the standard error-notification path (dashboard + email/Chat) with severity by materiality (any debit≠credit ⇒ critical; small snapshot drift ⇒ warning pending next cadence).
