# Changelog

## 2.0.2 — Resilient newest-first backfill

Diagnosed from the 2026-08-12 15:07 live Force Full Sync: every voucher window
(one month down to four days) timed out identically at 120 s, which proves the
cost was company-wide, not window-bound.

### The extraction stall, fixed at the root

* **Voucher collection no longer full-scans Tally**: the explicit TDL
  `$Date >= ## AND $Date <= ##` FILTER forced Tally to materialize and walk the
  ENTIRE voucher file (all years) for every window. The collection is now
  period-bound through SVFROMDATE/SVTODATE only, so Tally serves each window
  from its date index. The extractor still validates every voucher's DATE
  client-side and skips/logs anything outside the window.
* **No same-size timeout retries for splittable windows**: a multi-day window
  that times out is split immediately instead of burning ~10 minutes retrying
  the identical request at 10/30/60 s intervals. Single-day windows (which
  cannot split) keep the full retry ladder.
* **Dedicated voucher request budget**: new `tally.voucherTimeoutSeconds`
  (default 300) gives heavy month windows a fair chance before splitting,
  without slowing down light master/report calls (still 120 s).

### Newest-first history walk

* **Full sync now walks from today BACKWARDS to the extraction start**, so the
  latest year lands in BigQuery first and every interruption resumes going
  further back — the most valuable data is always synced first.
* Timed-out windows split newer-half-first during the walk.
* The backward frontier is checkpointed (`LastFromDate` + marker); legacy
  forward checkpoints from interrupted pre-2.0.2 walks are detected and the
  walk restarts newest-first (idempotent batch IDs absorb the overlap).

### Auto-reconnect during active sync

* If Tally stops responding mid-sync (closed, restarting, machine busy), the
  agent probes at `tally.reconnectRetrySeconds` (default 30 s) for up to
  `tally.reconnectMaxMinutes` (default 30 min) and continues the same window
  exactly where it stopped — no failed cycle, no lost progress.

### Report fallbacks (0-row snapshots)

* Trial Balance falls back to a period-bound Ledger collection, Balance Sheet
  and P&L to a Group collection (revenue split via ISREVENUE), and Stock
  Summary to a StockItem collection whenever the report-form export returns
  0 rows — closing figures honour SVTODATE, so period semantics are kept.

## 2.0.1 — Runtime hardening after first live Force Full Sync

Validated against the first real v2.0.0 production Force Full Sync on 2026-08-12.
This hotfix addresses runtime failures and data-quality red flags that only appeared
under the live Dynalektric Tally workload.

### Runtime fixes

* **Adaptive window split crash fixed**: the v2.0.0 timeout-split logger had six
  message-template placeholders but only four arguments, so the logger itself
  threw before the window could split. The template now has one argument per
  placeholder, so a timed-out voucher window can split and continue.
* **Force Full Sync checkpoint reset de-duplicated**: configured-company runs no
  longer reset the voucher checkpoint twice.
* **SQLite writer contention hardened**: WAL busy timeout increased to 15 seconds;
  dequeue and ack now use IMMEDIATE write transactions to avoid the deferred
  read-to-write upgrade race that can return SQLITE_BUSY while SyncWorker writes.

### Extraction fixes and validation

* **Master attribute fallback**: TallyXml scalar readers now fall back to
  same-named attributes when child elements are absent. This specifically fixes
  Group/StockItem masters where Tally emits `NAME` as an attribute — the live
  v2.0.0 run returned 0 groups/stock items while related data existed.
* **Nested report parsing**: Trial Balance, Balance Sheet, P&L and Stock Summary
  no longer assume DSP report rows are direct root children; nested layouts and
  local-name matching are supported.
* **Cross-dataset validation**: a full sync now flags contradictory counts (for
  example ledgers > 0 but groups = 0, or standard stock prices/costs > 0 but
  stock_items = 0) instead of silently certifying the baseline as successful.

### Remote health

* Missing `/heartbeat` is treated as **DEGRADED monitoring**, not cloud-offline.
  `/health` is probed to distinguish a missing optional route from a real outage;
  unsupported-heartbeat retries back off to hourly to avoid five-minute log spam.
* Added once-daily health summaries (default 08:00 server local time) containing
  agent version, Tally status, current operation, last successful sync,
  pending/failed batches, disk/memory and latest error.
* Daily summaries go to configured direct webhooks immediately and are also sent
  to the cloud notification endpoint with optional `recipient_email` for
  server-side email fan-out. SMTP credentials are never stored in the agent.
* Cloud Run still needs compatible `/heartbeat` and `/errors` routes before
  remote heartbeat history and email fan-out are fully operational.

### Release procedure

Install v2.0.1 **over** v2.0.0; do not delete `agent.db`, queue state or existing
GCS objects. After connection tests pass, trigger Force Full Sync exactly once
and verify adaptive splitting plus key dataset counts before accepting the new
GCS baseline or loading it into BigQuery.

## 2.0.0 — Production hardening release (branch `release/v2.0-production`)

Based on `main` @ `9a1b6a3` (Merge PR #12, fix/tally-voucher-full-load) — the
**latest GitHub version**, not the July snapshot. Eleven focused commits; every
change below maps to an item from the engineering review / production brief.

### Critical fixes (release blockers)

* **C1 — Idempotency restored** (`29b2847`, amended by `f7c55c5`): batch
  identity is now a *content checksum* computed over rows **without** audit
  fields (`_sync_timestamp`, `_sync_id`, `source_last_seen_at`). Previously the
  audit fields were hashed too, so every cycle minted new batch IDs for
  identical data and server-side dedupe could never fire. `checksum_sha256`
  remains the transport hash of the gzip payload; new `content_checksum`
  column via additive schema v3. Old queued batches keep their IDs.
* **C2 — No more silent data gaps** (`33fef5b`): incremental windows start at
  the earlier of (checkpoint + 1 day) and (today − lookback). An outage longer
  than the lookback is re-extracted and *reported*, not skipped. Planner
  extracted into pure `SyncPlanner` with 8 unit tests.
* **C3 — Startup can no longer destroy queued data** (`e1d8c0f`): the orphan
  sweep previously capped its "referenced files" set at 10k rows — a multi-day
  offline backlog then got its payloads *deleted at startup*. The sweep now
  reads every row (or deletes nothing if it can't); startup also flags rows
  with missing payloads, and uploads verify the payload SHA-256 before
  shipping.
* **C4 — One cloud contract** (`0af6791`): heartbeat/errors/updates moved to
  the same base + `X-API-Token` scheme as health/sync (previously half old
  `/v1/*`, half new — monitoring likely 404ed silently). The `/sync` envelope
  restores the dropped integrity metadata (sequence, sync id, record count,
  transport + content checksums, schema/agent version, window, retry count,
  Tally company). `docs/CLOUD_API_CONTRACT.md` rewritten to v2.1 matching the
  code exactly.
* **C5 — CI actually gates** (`505de25`): `TallyEnvelopeTests.cs` was missing
  `using Xunit;` (test project didn't compile); `build.ps1` never checked
  `$LASTEXITCODE` (red tests still shipped installers). Both fixed.
* **C6 — Criticals reach the admin** (`5340ce6`): all SyncEngine failures
  (Tally down, disk full, dataset errors, crashes) now route through
  `ErrorReporter` — immediate dispatch for criticals with cooldown, grouped
  digests for the rest. Previously they landed in `error_log` and nothing sent
  them.

### Production sync

* **Sync sessions**: every cycle records mode + status + sync_id in
  `sync_runs`; Manager shows `mode sync (status) · id …`.
* **Full/incremental separation & first-install behaviour**: first run (or any
  incomplete full sync) automatically plans the chunked full-history walk;
  incremental only begins after the full walk completes (`FullSyncDone`
  checkpoint) — and resumes mid-history after any interruption.
* **Force Full Sync** (`1f8d6b6`, hardened by `f7c55c5`): Manager button (with
  confirmation) + `TallyAgent.Cli force-full-sync` verb reset the voucher
  checkpoint and re-walk history; the request survives Tally being closed at
  click time; re-uploads are duplicate-safe.
* **Upload acknowledgement tracking**: unchanged ack-before-delete lifecycle,
  now with pre-upload checksum verification and correct checksums after
  crash-replay.

### Data extraction

* **Master GUID/MASTERID/ALTERID** (`f45b4ea`): all 13 master collections
  fetch and emit `master_guid`/`master_id`/`alter_id` — stable warehouse MERGE
  keys; renames can no longer create duplicates.
* **AlterID change detection** (`1f8d6b6`): company-wide ALTMSTID/ALTVCHID
  watermarks gate the masters and voucher phases independently — idle
  companies cost one tiny request per cycle. Watermarks advance only after
  fully successful cycles; gate-skipped cycles advance the window checkpoint
  (no false gap alerts).
* **Deleted voucher detection**: new `voucher_guid_manifest` dataset (guid,
  date, type, alter_id, is_cancelled per window) for warehouse anti-join →
  `is_deleted`/`source_status` updates. No physical deletes anywhere.
* **Voucher lifecycle columns** (`61192a7`): `is_optional`, `is_deleted`,
  `source_status` (active|cancelled|optional), `source_last_seen_at` on
  headers and flat rows.
* **Double-count fix** (`61192a7`): ledger/inventory entries now prefer
  `ALL*ENTRIES` and fall back to the plain variant — the previous
  Concat+Distinct was reference-equality (a no-op) and doubled every line on
  Tally builds returning both shapes.
* **Child record identity**: `entry_type` + per-voucher `line_index` on all
  child rows, with the documented contract that the warehouse replaces the
  whole child set per (company, voucher_guid, entry_type) — ordinals are never
  merged across edits.
* **Adaptive window sizing / large-company support** (`1f8d6b6`): a window
  that times out is split in half and retried down to single days — the
  same-oversized-window-forever livelock is gone.
* **Historical voucher extraction fix**: inherited from `main` (explicit
  collection date filter + window validation + header dedup, PRs #11/#12) and
  preserved.

### Reliability, UI, tooling

* Offline queue, retry matrix, resume-after-restart preserved; startup
  recovery extended (stale tmp, orphans with full reference set, missing
  payload flagging, stuck-upload reset, checksum verify).
* Manager: Force Full Sync button; activity line shows sync mode, status and
  sync id; existing Tally/cloud tests, pending/failed counts unchanged.
* CLI: `force-full-sync` and `capture-xml` (vouchers|masters|alterids →
  sanitized XML fixtures under ProgramData\fixtures) for the extraction
  validation gate.
* An adversarial review pass over this branch found and fixed 4 further
  defects before release (`f7c55c5`).

### Known issues / remaining work (tracked, not blockers for merge review)

1. **Cloud side must implement contract v2.1** — `heartbeat`, `errors`,
   `updates/check` endpoints and the restored `/sync` envelope fields
   (including `content_checksum`-based dedupe). Until then, monitoring calls
   fail gracefully (categorized, retried) but deliver nothing.
2. **`health` doesn't prove auth** — a wrong token passes the connection test
   and only surfaces at first upload. Needs an authenticated probe endpoint.
3. **Upload memory** — `/sync` inflates the NDJSON payload into a JSON body in
   memory; fine at the 5,000-record default, heavy at the 50,000 max. A
   streaming/gzip upload needs a server-side contract decision.
4. **Whole-window XML buffering** — extraction still DOM-parses each window;
   adaptive splitting caps the blast radius, but `XmlReader` streaming (or
   trimmed FETCH lists) is the long-term fix for very large companies.
5. **Voucher classification** still name-based (`Contains("sales")`); the
   `$$IsOrderVch`/parent-chain approach awaits the §8.4 validation gate
   fixtures before changing envelope semantics.
6. **Extraction validation gate (§8.4) not yet run** against live TallyPrime —
   `capture-xml` exists for exactly this; schema freeze should follow the
   10-voucher matrix.
7. Email alerting is configured in UI/installer but delivered via the cloud
   notification service only — no agent-side SMTP (by design; needs the cloud
   `errors` endpoint live).
8. Installer field gaps from the review (lookback/chunk/batch-size wizard
   inputs, dynel-electric / ars@dynalektric.com defaults) are NOT in this
   branch — they change installer behaviour and were deferred to a separate
   reviewable commit on request.
