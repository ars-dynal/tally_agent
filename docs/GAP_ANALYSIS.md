# Tally BigQuery Agent — Gap-Analysis Report

**Date:** 2026-07-30 · **Prepared against:** ARCHITECTURE.md v1.1 + Dynalektric production brief
**Scope:** Tally-to-BigQuery only. TRICO staging layer explicitly out of scope.

Classification legend: **COMPLETE** · **PARTIAL** · **MISSING** · **NEEDS VERIFICATION** · **CHANGE REQUIRED**

Evidence basis — clearly separated throughout:

* **Confirmed (inspected in this session):** the legacy Python agent at
  `C:\Users\imranimmu\Downloads\Dynalektric\dynalektric-finance\tally-bq-agent`
  (source read file-by-file) and the new C# solution `tally-bigquery-agent.zip`
  (authored this session, not yet compiled — NuGet unreachable from this sandbox).
* **Inferred:** anything about what is deployed in GCP. This sandbox has no GCP
  access; nothing cloud-side could be listed or counted.
* **Recommended:** target names from the brief (tally_raw/…, dynalektric-tally-raw-prod,
  sa-tally-ingestion@…) — adopted as the target state, pending confirmation that no
  conflicting resources already exist.

---

## 1. Current implementation discovered — CONFIRMED

Two generations exist side by side:

### 1a. Legacy Python agent (`tally-bq-agent`, PyInstaller EXE + Tkinter GUI)

| Property | Finding |
|---|---|
| Language / shape | Python 3.11, PyInstaller one-folder EXE, Tkinter GUI, pywin32 Windows service wrapper, Inno Setup script |
| Upload path | **Direct to BigQuery** from the Windows machine (`google-cloud-bigquery` client) — no Cloud Run, no GCS raw layer, no ingestion API |
| Auth | `service_account` JSON file path **on the Windows machine** or OAuth refresh token (`agent/bigquery/auth.py`) — **violates the security rule**; must be retired |
| Target dataset | Single dataset `tally_data`, **shared with TRICO tables** (confirmed in `config.py` and the schema workbook legend) — violates the Tally/TRICO separation rule |
| Datasets extracted | All 33 Tally datasets of the schema workbook + TRICO REST datasets mixed into the same sync (`sync_engine.py` runs both) |
| Queueing / offline | None. Fetch → upload in one pass; a failed upload loses nothing but is simply retried next scheduled run; no durable batch queue |
| Checkpoints | `sync_history` table in SQLite (run log only); date-mode = auto/full/custom — **no windowed resume, no per-dataset checkpoints** |
| Idempotency | None at warehouse level: APPEND-mode tables get duplicate rows on re-extraction of the same window; snapshots are WRITE_TRUNCATE |
| Heartbeat | Local `heartbeat.json` file only — nothing reaches any cloud monitoring |
| Alerts | None (no email/webhook path) |

### 1b. New C# solution (this repo, authored to ARCHITECTURE v1.0, doc since revised to v1.1)

.NET 8 self-contained: `TallyAgent.Core` (config+DPAPI, SQLite queue/checkpoints/errors, Tally client + 33 extractors, sync engine, cloud API client, error reporter), `TallyAgent.Service` (SyncWorker / UploadWorker / HeartbeatWorker / ErrorSummaryWorker), `TallyAgent.Cli`, `TallyAgent.Manager` (WPF), Inno Setup script, build/sign scripts, docs. **Not yet compiled** (sandbox cannot reach NuGet); one adversarial static review completed, 3 defects found and fixed.

**The code was written before the v1.1 corrections, so several v1.1 rules are documented but not yet implemented in code — each is itemised below.**

---

## 2. Existing BigQuery datasets and tables — NEEDS VERIFICATION

* **Inferred (from legacy code + workbook):** dataset `tally_data` in project
  `dynalektric-enterprise-ai` likely exists with some/all of the 30 `tally_*` tables and
  TRICO tables mixed in, populated by past runs of the Python agent. Row counts,
  freshness, and duplicate contamination (expected in APPEND tables — see §8) are unknown.
* **Confirmed:** nothing — no GCP access from this environment.
* **Action:** run the verification queries in §11 before any cloud work; decide whether
  `tally_data` is migrated or abandoned in favour of the clean five-dataset layout.

## 3. Existing GCS buckets and paths — NEEDS VERIFICATION

* Legacy agent used **no GCS at all** (direct BQ load from memory). It is likely no
  Tally raw bucket exists. `dynalektric-tally-raw-prod` with the brief's
  hive-partitioned layout (`company_id=/dataset=/year=/month=/day=/batch_id=/`) is the
  recommended target; the C# agent and CLOUD_API_CONTRACT.md must adopt this layout
  (contract currently shows a flatter path — CHANGE REQUIRED, doc-only).

## 4. Existing Cloud Run services / jobs — NEEDS VERIFICATION (expected MISSING)

Legacy agent bypassed Cloud Run entirely, so `tally-ingestion-api`,
`tally-batch-processor`, `tally-reconciliation-job`, `tally-health-monitor` almost
certainly do not exist yet. **None of the cloud-side components exist in this repo
either** — the repo contains only the API contract they must implement. This is the
single largest missing block (§10).

## 5. Current Windows agent components — CONFIRMED

| Component | Status |
|---|---|
| Config model + DPAPI encryption + validation | COMPLETE (agent-side) |
| SQLite schema + migrations (queue, checkpoints, errors, heartbeats) | COMPLETE, minus v1.1 additions (control_totals column, maintenance_schedule table) — PARTIAL |
| Tally client (sanitizer, categorized failures, retries) | COMPLETE |
| 33 dataset extractors | PARTIAL — see the 16-point assessment below |
| Sync engine (full chunked + incremental lookback) | PARTIAL — missing v1.1 cadences and crash-safe tx ordering |
| Durable upload queue + backoff/retry matrix | COMPLETE, minus deterministic batch IDs and pre-upload checksum verify — PARTIAL |
| Heartbeat worker (5-min, offline-buffered, server commands) | COMPLETE (agent side) |
| Error reporter (taxonomy, critical-immediate + grouped digests) | COMPLETE (agent side) |
| WPF Manager (all 10 required actions) | COMPLETE (uncompiled) |
| CLI (test-tally / test-cloud / save-config / sync-now / retry-failed / export-diag / status / protect) | COMPLETE, missing `capture-xml` — PARTIAL |
| Inno Setup installer | PARTIAL — see §9 |

## 6. Architecture requirements already satisfied — CONFIRMED (agent side)

* Windows Service shape: auto-start, LocalService, SCM recovery 1/5/15 min, Event Log +
  rolling file logs, closing installer/manager never stops the service — COMPLETE (code + installer script).
* DPAPI-encrypted secrets, secret masking in all logs/diagnostics, HTTPS-only with
  mandatory TLS validation, no SA JSON on the agent (bearer token only) — COMPLETE in the C# design.
* Offline queueing with ack-before-delete payload lifecycle — COMPLETE.
* Sanitised diagnostic ZIP — COMPLETE.
* Upgrade preserves ProgramData; uninstall offers keep/purge — COMPLETE (installer script logic).

## 7. Missing requirements — MISSING

1. **Entire cloud side**: ingestion API, batch processor, reconciliation job, health
   monitor, `tally_control.ingestion_batches`, `tally_monitoring.*` tables, the five
   datasets, the raw bucket, `sa-tally-ingestion` service account + IAM.
2. **GUID-manifest dataset** (`voucher_guid_manifest`) and deleted-voucher detection.
3. **Layered cadences**: nightly 7-day refresh, monthly FY reconciliation, periodic full
   GUID sweep (`maintenance_schedule` table + scheduler in the service).
4. **Control totals** (agent-side computation + `X-Control-Totals`).
5. **`capture-xml` CLI verb** and the §8.4 extraction-validation fixture workflow.
6. **Financial reconciliation** implementation (checks + monitoring tables are designed, not built).
7. **Pre-upload checksum verification** and the missing-payload/stale-tmp startup sweeps
   (orphan sweep and stuck-`uploading` reset exist; the other three do not).
8. **Automatic update pipeline** (contract endpoint designed; agent-side updater not implemented — acceptable post-v1).

## 8. Incorrect or risky implementation choices — CHANGE REQUIRED

| # | Item | Where | Risk | Required change |
|---|---|---|---|---|
| R1 | **Batch IDs contain a wall-clock timestamp** (`{dataset}-{yyyyMMddHHmmss}-{seq}`) | `BatchBuilder.cs` | violates determinism rule; retry-after-recreate could mint a new ID | deterministic formula, persisted once (§9.2 of arch) |
| R2 | **Checkpoint advance is a separate write from batch insert** | `SyncEngine.EnqueueAndCheckpoint` → `BatchBuilder` then `CheckpointRepository` | crash between the two double-extracts a window (harmless only once MERGE keys are right, but violates §6.1 ordering) | single SQLite transaction covering enqueue + checkpoint |
| R3 | **Masters don't fetch MASTERID / GUID / ALTERID** | `MasterExtractor.cs` | master MERGE key would fall back to names; renames create duplicates | add the three fields to every master FETCH list + row output |
| R4 | **Voucher rows lack `is_deleted` / `source_status` / `source_last_seen_at`** (`is_cancelled` present) | `VoucherExtractor.cs` | no deletion lifecycle | add columns per §8.1 |
| R5 | **Child records have no line identity at all** | `VoucherExtractor.cs` | cannot key child rows; brief also warns ordinals alone are unstable across edits | emit `line_index` per entry type **and** design the warehouse MERGE as delete-and-replace at the voucher-GUID boundary (child set replaced wholesale per voucher per batch — ordinal only disambiguates within one extraction, never across edits) |
| R6 | **Legacy Python agent still installed/runnable with SA JSON + direct BQ writes into a TRICO-shared dataset** | user's machine | credential exposure + duplicate/mixed data if it runs concurrently with the new agent | decommission plan: stop/uninstall legacy service before the C# agent goes live; rotate/delete its SA key |
| R7 | **Legacy APPEND tables in `tally_data` likely already contain duplicates** (no MERGE ever ran) | BigQuery | analytics correctness | verify (§11) and either backfill-clean into `tally_warehouse` or start warehouse fresh from a full C# sync |
| R8 | **Placeholder API URL** `https://ingest.example.com` appears in docs/examples | docs, config examples | accidental production misconfig | keep URL as a required installer input with validation (option 2 of the brief) until `tally-ingestion-api` is deployed; no placeholder defaults in shipped config |
| R9 | **GCS path in contract doc is flat**, brief requires hive-partitioned layout | CLOUD_API_CONTRACT.md | BQ external/table partition ergonomics | doc + processor to use `company_id=/dataset=/year=/month=/day=/batch_id=/` |
| R10 | Snapshot datasets `_sync_id`-free recency — already fixed in docs v1.1; code emits `_sync_id` only as audit column | verified | none | no action |

## 9. Required installer changes — PARTIAL / CHANGE REQUIRED

Existing wizard covers: Tally (host/port/company/start date/frequency), dataset toggles
+ auto-discover, Cloud (URL/agent ID/company ID/token masked), environment radio,
notifications (email + 3 webhooks + email-alert toggle), Tally + cloud tests,
service registration with recovery settings, upgrade skip-config, keep-data uninstall.

Changes needed:

1. Add missing fields: **incremental lookback days** (default 7), **full-sync chunk
   size** (default 31), **upload batch size** (default 5000), **heartbeat frequency**
   (default 5), **critical-alert cooldown** (default 30), **summary interval** (default 60).
   (Wizard for the first two; the rest may stay config-file/Manager-editable to keep the wizard short — decision point.)
2. Production defaults: `companyId=dynel-electric`, `environment=Production`,
   `adminEmail=ars@dynalektric.com` pre-filled.
3. Additional validations (§ brief): ProgramData writable by LocalService (icacls result
   check), SQLite creation probe (`TallyAgent.Cli status` exit code after install),
   post-start service health check (query SCM state + recent Event Log), token-accepted
   check already covered by `test-cloud`.
4. Upgrade path: add post-restart health verification step.
5. HTTPS enforcement for Production already validated in `ConfigValidator` — surface the
   same rule in the wizard (reject http:// unless environment=Development).

## 10. Required cloud-side changes — MISSING (build from zero)

Target (recommended names from the brief, project `dynalektric-enterprise-ai`, region `asia-south1`):

| Component | Purpose | Notes |
|---|---|---|
| `tally-ingestion-api` (Cloud Run service) | /v1/ping, /v1/batches, /v1/heartbeat, /v1/errors, /v1/updates/check | authenticates per-agent bearer tokens bound to agent_id+company_id+environment (tokens in Secret Manager); writes GCS; registers `ingestion_batches`; enqueues load |
| `tally-batch-processor` (Cloud Run job/worker) | GCS → `tally_staging` load jobs → MERGE into `tally_warehouse` (§9.1 keys, child delete-and-replace per voucher GUID) → status `loaded`→`processed` | idempotent per batch_id; failed → `failed` + alert |
| `tally-reconciliation-job` (Cloud Run job) | §13 checks incl. GUID-manifest deletion sweep | writes `tally_monitoring.recon_*` |
| `tally-health-monitor` (Cloud Run job) | SLA watchdogs + alerts + daily summary to **ars@dynalektric.com**; structured so a future combined Dynalektric Data Platform Health Report can join TRICO + Tally views | alert conditions per brief (12 listed) |
| Datasets | `tally_raw` (external/backup), `tally_staging`, `tally_warehouse`, `tally_control`, `tally_monitoring` | fully separate from `trico_raw`/`trico_control` |
| Bucket | `dynalektric-tally-raw-prod`, hive layout | immutable raw, lifecycle policy TBD |
| Service account | `sa-tally-ingestion@dynalektric-enterprise-ai.iam.gserviceaccount.com` | storage.objectAdmin (bucket-scoped), bigquery.dataEditor (tally_* only), bigquery.jobUser, secretmanager.secretAccessor, logging.logWriter — least privilege, no key files |

## 11. Required BigQuery schema changes — CHANGE REQUIRED / NEEDS VERIFICATION

1. New control/monitoring tables: `tally_control.ingestion_batches` (brief's 18 fields
   incl. `gcs_uri`), `tally_monitoring.recon_runs` / `recon_results` / `recon_exceptions`.
2. Warehouse tables: add `source_company_id`, `is_deleted`, `source_status`,
   `source_last_seen_at` to voucher tables; `master_id`, `master_guid`, `alter_id` to
   master tables; `line_index`+`entry_type` to child tables; `_sync_id`/`_sync_timestamp`
   retained audit-only.
3. Verification queries to run first (against `dynalektric-enterprise-ai`):
   `bq ls` datasets; per-table row counts; duplicate probe on `tally_vouchers`
   (`GROUP BY guid, ledger_name, amount HAVING COUNT(*)>1`); last `_sync_timestamp` per table.

## 12. Required monitoring changes — PARTIAL (agent) / MISSING (cloud)

Agent side already emits everything the health monitor needs (full heartbeat field set,
error taxonomy, queue stats). Missing: control totals in batch metadata; cloud side:
all of it (§10). Design the `tally_monitoring` views so the future combined report can
UNION Tally and TRICO health rows (shared columns: source_system, last_heartbeat,
last_accepted_batch, last_processed_batch, queue_depth, failed_batches, recon_status,
last_success, last_error).

## 13. Required security changes — mostly COMPLETE, two actions

| Item | Status |
|---|---|
| DPAPI secrets, masked logs, HTTPS-only, TLS validation, LocalService least-privilege, ACL'd ProgramData, signed binaries (script ready) | COMPLETE (C# design) |
| No SA JSON on Windows; bearer token bound to agent/company/environment | COMPLETE in new agent; **CHANGE REQUIRED: retire legacy Python agent + rotate/delete its service-account key** (R6) |
| Token issuance/revocation process (Secret Manager-backed registry in ingestion API) | MISSING (cloud side) |

## 14. Recommended implementation sequence (smallest safe change set first)

**Phase A — agent correctness (small, reviewable commits; no behaviour rewrites):**
1. Deterministic persisted batch IDs (R1) + reuse test.
2. Single-transaction enqueue+checkpoint (R2) + kill-point test.
3. MASTERID/GUID/ALTERID in master extractors (R3).
4. Voucher lifecycle columns + `line_index`/`entry_type` on child rows (R4, R5).
5. Startup sweeps: stale tmp, missing-payload flagging, checksum verify pre-upload.
6. Control-totals computation + `X-Control-Totals`.
7. `capture-xml` CLI verb.
8. Compile on Windows, fix build errors, run unit tests (each change lands with tests).

**Phase B — extraction validation gate (blocks schema freeze):**
9. Run the §8.4 ten-voucher matrix against the live Dynel Electric company; freeze schema v1.0 only when Tally screen ↔ raw XML ↔ parsed output reconcile.

**Phase C — minimal cloud path (enables "Tally data visible in BigQuery"):**
10. Provision datasets/bucket/SA (Terraform or gcloud script, committed).
11. `tally-ingestion-api` (ping/batches/heartbeat/errors + token registry).
12. `tally-batch-processor` (load → MERGE with §9.1 keys).
13. End-to-end test: full sync of a small window → rows in `tally_warehouse`; duplicate re-upload test; row-count validation vs Tally.

**Phase D — installer + defaults:**
14. Installer field additions, Dynalektric defaults, extra validations, upgrade health check.

**Phase E — lifecycle + reconciliation:**
15. Maintenance cadences + GUID manifest (agent) and deletion sweep (processor).
16. `tally-reconciliation-job` + monitoring tables.
17. `tally-health-monitor` alerts to ars@dynalektric.com.

**Explicitly deferred:** TRICO staging layer (per brief), automatic agent self-update.

---

## Appendix — the 16-point connector assessment (brief §"Existing connector assessment")

| # | Question | Legacy Python agent | New C# agent |
|---|---|---|---|
| 1 | Datasets extracted | all 33 (plus TRICO mixed in) | all 33 (TRICO excluded by design) |
| 2 | BQ datasets/tables exist | `tally_data` (shared w/ TRICO) — NEEDS VERIFICATION in GCP | none created yet |
| 3 | Row counts | unknown — no GCP access | n/a |
| 4 | Raw data in GCS | **no** | designed, cloud side not built |
| 5 | Uses Cloud Run | **no** — direct BQ | designed, not built |
| 6 | Full + incremental sync | full/FY/custom date modes; **no true incremental, no resume** | full chunked + 7-day lookback; cadences missing |
| 7 | Captures GUID/MASTERID/ALTERID/date/type/cancel/modification | voucher GUID ✓, date ✓, type ✓, ISCANCELLED ✓; **MASTERID ✗, master GUID ✗, ALTERID ✗**, modification info ✗ | same today (R3/R4 close this) |
| 8 | Line/allocation identity | none (flat rows, no keys) | none yet (R5) |
| 9 | Edited-voucher replacement | none — APPEND duplicates | designed (lookback + MERGE), MERGE not built |
| 10 | Deleted-voucher detection | none | designed (manifest), not built |
| 11 | Idempotent retries | no | yes at queue level; warehouse level pending processor |
| 12 | Crash-safe checkpoints | no | partial (R2 closes the tx gap) |
| 13 | Checksums verified | no checksums | computed + sent; pre-upload re-verify missing |
| 14 | Heartbeats | local file only | full cloud heartbeat implemented (agent side) |
| 15 | Health alerts | none | agent-side reporter done; cloud fan-out/monitor missing |
| 16 | ARCHITECTURE.md coverage | n/a (predates it) | detailed per §§5–13 above |

---

**Status: awaiting approval.** No code has been changed for this report. On approval,
work starts with Phase A items 1–2 (deterministic batch IDs, transactional
checkpointing) as the first two commits, each with tests, followed in order.
