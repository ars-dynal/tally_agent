# Tally BigQuery Agent

Production Windows agent that extracts data from **TallyPrime** (XML API on port
9000) and uploads it securely to a **Google Cloud ingestion API** feeding
BigQuery. Runs as a Windows Service (`TallyBigQueryAgent`), survives reboots,
queues data locally while offline, and reports health + errors to the
developer/admin.

```
TallyPrime ──XML──► Agent (Windows Service) ──HTTPS──► Ingestion API ──► Cloud Run
                     │  SQLite queue/checkpoints            │
                     └── WPF Manager (Start Menu)           └─► GCS raw ─► BQ staging ─► BQ warehouse
```

## Repository layout

| Path | What |
|---|---|
| `src/TallyAgent.Core` | All business logic: config + DPAPI, SQLite queue/checkpoints, Tally XML client + 33 dataset extractors, sync engine (full + incremental w/ 7-day lookback), cloud API client, error reporting |
| `src/TallyAgent.Service` | Windows Service host — SyncWorker, UploadWorker, HeartbeatWorker, ErrorSummaryWorker |
| `src/TallyAgent.Cli` | Installer/admin verbs: test-tally, test-cloud, save-config, sync-now, retry-failed, export-diag, status |
| `src/TallyAgent.Manager` | WPF management console (service control, tests, sync-now, diagnostics) |
| `installer/` | Inno Setup 6 script → `Tally BigQuery Agent Setup.exe` |
| `build/` | `build.ps1` (publish + installer), `sign.ps1` (Authenticode) |
| `docs/` | `ARCHITECTURE.md`, `CLOUD_API_CONTRACT.md`, `INSTALL.md` |

## Quick start (build on Windows)

```powershell
.\build\build.ps1        # → dist\Tally BigQuery Agent Setup.exe
```

See `docs/INSTALL.md` for the full runbook and `docs/ARCHITECTURE.md` for the
complete design (SQLite schema, sync windows, retry matrix, security model,
acceptance criteria mapping).

## Key guarantees

* **No data loss offline** — extraction continues, batches queue in
  `C:\ProgramData\TallyBigQueryAgent\queue`, payloads are deleted only after the
  cloud API acknowledges them.
* **No duplicates** — deterministic batch IDs + server-side dedupe + BigQuery
  MERGE on natural keys; re-uploading a batch is a no-op.
* **No plaintext secrets** — API token and webhook URLs are DPAPI-encrypted
  (LocalMachine scope); secrets are masked in every log line and diagnostic ZIP.
* **Self-healing** — SCM recovery (1 m / 5 m / 15 m restarts), categorized error
  taxonomy, immediate critical alerts + grouped summaries, 5-minute heartbeats
  with server-side dead-agent watchdog.
* **Email notifications** are delivered by the cloud notification service (using
  the registered admin email) so no SMTP credentials ever live on the Tally
  machine; Google Chat/Slack/generic webhooks fire directly from the agent as a
  fallback when the cloud API itself is unreachable.

## Configuration

`C:\ProgramData\TallyBigQueryAgent\config.json` — created by the installer,
editable via the Manager app or `TallyAgent.Cli save-config`. Field reference in
`docs/ARCHITECTURE.md` §5.
