# Tally BigQuery Agent

A Windows service and desktop console that extracts accounting data from
TallyPrime and hands it to a cloud ingestion API, which loads it into BigQuery.

Built for Dynalektric Equipment Private Limited. Part of Dynalektric Enterprise
AI, Domain 1 (Finance & Accounts).

---

## What it does

Every 15 minutes (configurable) the agent:

1. Checks TallyPrime is running and the right company is open.
2. Reads **masters** (ledgers, groups, stock items, …), **vouchers** for a date
   window, and **reports** (trial balance, outstandings).
3. Writes each dataset to a gzipped NDJSON file in a local queue on disk.
4. POSTs those files to the ingestion API, retrying until each is acknowledged.

Extraction and upload are **separate**. Extraction never waits on the network,
so a broken internet connection delays delivery but never loses data — the
queue sits on disk until it drains.

### What it is not

The agent **holds no cloud credentials** and never writes to BigQuery, GCS, or
any Google service. It POSTs to one HTTPS endpoint with an API token. Everything
past that endpoint — loading, deduplication, partitioning, retention — belongs to
the **tally-ingestion-api**, a separate service in a separate repository.

This boundary matters when something looks wrong:

| Symptom | Whose problem |
|---|---|
| Data missing from BigQuery, but the console shows it accepted | ingestion API |
| Duplicate rows in BigQuery | ingestion API (see `docs/idempotency-contract.md`) |
| Data never left this machine — queue growing | the agent, or the network |
| Figures disagree with Tally | the agent |

---

## Architecture

```
   TallyPrime  ──HTTP/XML──▶  Agent (Windows service)
   (port 9000)                  │
                                ├─▶ SQLite   %ProgramData%\TallyBigQueryAgent\agent.db
                                │            checkpoints, upload queue, run history, errors
                                │
                                ├─▶ queue\   *.ndjson.gz — one file per batch
                                │
                                └──HTTPS──▶  tally-ingestion-api  ──▶  BigQuery
```

Three executables, one install:

| Component | What it is |
|---|---|
| `TallyAgent.Service.exe` | The Windows service. Extracts and uploads. |
| `TallyAgent.Manager.exe` | The console. Status, history, settings. |
| `TallyAgent.Cli.exe` | Admin and verification commands. |

**Tally's XML server is single-threaded and shares the application thread with
the UI.** Every request the agent makes is felt by whoever is using Tally. The
agent therefore serialises all requests behind a cross-process lock, pauses
between them, and confines heavy work to a nightly slot.

---

## Install

1. Run `Tally BigQuery Agent Setup.exe` as Administrator.
2. Fill in the wizard: Tally host/port/company, ingestion API URL, agent id,
   company id, API token, and notification settings.
3. The installer tests both connections, writes the config, and starts the
   service.

Upgrading over an existing install keeps the configuration, the queue, the
checkpoints and the run history. It does **not** reset the sync position.

### Requirements

- Windows with .NET 8 (bundled — self-contained build)
- TallyPrime reachable over HTTP, with **F1 ▸ Settings ▸ Connectivity** set so
  it acts as **Both** or **Server**
- Outbound HTTPS to the ingestion API

---

## Configuration

`C:\ProgramData\TallyBigQueryAgent\config.json`. Secrets are DPAPI-encrypted at
rest and are readable only by the account that wrote them. Edit through the
console (**Settings…**) rather than by hand; saving there restarts the service,
which is required because **the service reads config only at startup**.

### `tally`

| Key | Meaning |
|---|---|
| `host`, `port` | Where Tally's XML server listens. |
| `company` | Exact company name. Blank means auto-discover the first open one. |
| `extractionStartDate` | How far back the first full walk goes. **Only read while the full walk is outstanding** — once it completes, changing this does nothing until a full re-extract. |
| `extractionEndDate` | Stops a backfill at a date. Blank on the live machine. |
| `syncFrequencyMinutes` | How often to run. Default 15. |
| `incrementalLookbackDays` | How far back each routine run re-reads. Default 7. |
| `fullSyncChunkDays` | Days per checkpoint window in a full walk. Default 7. |
| `snapshotHourLocal` | Hour after which the daily reports and balance capture run. Default 20 (after office hours). |
| `enableSnapshots`, `snapshotDatasets` | Report toggles. `balance_sheet`, `profit_loss` and `stock_summary` default **off** — they make Tally compute across the whole company and have hung it. |
| `requestPauseSeconds`, `windowPauseSeconds` | Idle gaps that give Tally's UI room to breathe. Set both to 0 for an overnight backfill. |
| `maxResponseMb` | Hard cap on a single Tally response. Default 256. |

### `cloud`

| Key | Meaning |
|---|---|
| `ingestionApiUrl` | Base URL of the ingestion API. |
| `agentId`, `companyId` | Identify this agent to the API. |
| `apiToken` | Bearer token. DPAPI-encrypted. |
| `uploadBatchMaxRecords` | Rows per batch. Default 5000. |

### `notifications`

At least one channel should be set, or **nobody is told when a sync fails**.
The service logs a warning at startup if none is configured.

| Key | Meaning |
|---|---|
| `errorWebhookUrl`, `googleChatWebhookUrl`, `slackWebhookUrl` | Webhook targets. Encrypted. |
| `smtpHost`, `smtpPort`, `smtpUser`, `smtpPassword`, `smtpFrom`, `smtpUseTls` | Email. `smtpPassword` encrypted. |
| `adminEmail`, `enableEmailAlerts` | Where email alerts go. |
| `stalledAfterMinutes` | Minutes without progress before a running sync is reported stalled. Default 20. |
| `criticalAlertCooldownMinutes` | Minimum gap between repeats of the same alert. Default 30. |

---

## Running a sync

**From the console** — *Sync now (incremental)* states the window it will cover
and confirms before starting. *Full re-extract* is a separate button; it reads
2019 to date, takes roughly two hours, and loads Tally heavily throughout.

**From the command line:**

```
TallyAgent.Cli sync-now          # routine catch-up
TallyAgent.Cli force-full-sync   # re-walk the entire history
```

Both write a trigger file the service picks up within seconds.

---

## Verifying against Tally

This is how you prove the agent is right, and it is the part to run before
trusting anything.

```
TallyAgent.Cli master-balances
```
Total closing stock value and one ledger's closing balance, straight from Tally,
with no voucher walk and no upload. Compare against Tally's own Balance Sheet.

```
TallyAgent.Cli verify --fy-counts --from 2019-04-01 --to 2027-03-31
```
Asks Tally how many vouchers it holds **per financial year**, so truncation is
visible rather than assumed. Compare against Tally's own Day Book counts.

```
TallyAgent.Cli verify --bills Bills.xml --trial-balance TrialBal.xml
```
Runs export files produced by Tally's own UI through the agent's parsers and
reports what they yield — proving the parser against real Tally output. Add
`--live` to also fetch from Tally and diff record for record.

```
TallyAgent.Cli test-tally      # is Tally reachable, which companies are open
TallyAgent.Cli test-cloud      # is the ingestion API reachable
TallyAgent.Cli status          # queue depth, last sync, disk
TallyAgent.Cli export-diag     # a zip with config (masked), checkpoints, errors
TallyAgent.Cli capture-xml --envelope-dir <dir> --dump    # post raw envelopes
```

**"How do I know the data got there?"** — the console's **Sent to cloud** tab
shows, per dataset, how many records the ingestion API has accepted, how many
are waiting, how many are stuck, and when the last one was accepted.

---

## Runbook

Every failure the console can show, what it means, and what to do.

### `TallyActivePeriodTooNarrow`
**Means:** Tally's books do not cover the dates asked for, so there is nothing
to read. Tally bounds every export by the period set with **Alt+F2**, and
returns an empty — not failed — response outside it.
**Do:** In Tally press **Alt+F2** and widen the period to cover the range being
extracted. Then run again.
**Note:** A window that merely runs a day or two past the last voucher entered
is *trimmed automatically*, not rejected — that is normal on a morning before
anyone has posted anything.

### `TallyWindowNotHonoured`
**Means:** Tally returned records dated outside the single day requested. Under
normal operation this cannot happen.
**Do:** Restart TallyPrime — a diagnostic probe that injected TDL into the
session is the usual cause, and its settings outlive the request. If it persists
after a restart, report it: the date scoping has regressed.

### `TallyRequestRejected`
**Means:** Tally refused the request outright (`Unknown Request, cannot be
processed`) rather than returning data.
**Do:** Nothing on the server — this needs a code change. Report which dataset.
A report with a fallback will have used the fallback and still produced rows;
check the `source` column to see which route the data came by.

### Tally unreachable — `TallyNotRunning`, `TallyPortUnavailable`
**Means:** Nothing is answering on the configured host and port.
**Do:** Open TallyPrime. Check **F1 ▸ Settings ▸ Connectivity** is set to
**Both** or **Server**, and that the port matches the agent's settings. The
agent retries by itself; no data is lost.

### `TallyCompanyNotOpen` / `TallyCompanyMismatch`
**Means:** No company is loaded, or a different one is.
**Do:** Open the configured company in Tally, or change the company name in the
agent's settings to match exactly.

### `TallyTimeout`
**Means:** Tally took too long and the request was abandoned. Tally keeps
working on it, so the agent waits for it to drain before asking anything else.
**Do:** Usually nothing — it retries and shrinks its window automatically. If it
repeats, run heavy work outside office hours.

### Ingestion API unreachable — `CloudApiUnavailable`, `InternetUnavailable`
**Means:** Extraction is fine; delivery is not.
**Do:** Nothing urgent. Batches queue on disk and upload themselves when the
connection returns. If it lasts hours, tell whoever runs the ingestion API.

### `AuthenticationFailure`
**Means:** The cloud rejected the agent's token. Uploads pause rather than
hammer the endpoint.
**Do:** Update the API token in **Settings…**.

### Queue backlog
**Means:** Batches are accumulating instead of draining. An alert fires.
**Do:** Check `test-cloud`, then the token. **Retry failed uploads** in the
console requeues anything marked stuck. If the queue exceeds
`queueDiskLimitMb`, extraction pauses deliberately to protect the machine —
drain it before extracting more.

### A run keeps choosing "full"
**Means:** The full-history walk has not been recorded as complete, so the
planner replans it every time.
**Do:** The startup log now names the reason — look for
`Sync mode 'full' … FullSyncDone=…, frontier=…`. If it says there is no
checkpoint row, the company name in config does not match the one the walk was
recorded under.

### Stale master balances
**Means:** Ledger closing balances lag behind the vouchers.
**Do:** Balances are captured once a day after `snapshotHourLocal`, and served
from cache in between — `balance_as_of` on each row says when. If they are
wrong rather than merely old, check Tally's active period: master balances are
computed as of a date, and a narrowed Alt+F2 period bounds them.

---

## Development

```powershell
dotnet build TallyBigQueryAgent.sln -c Release
dotnet test
.\build\build.ps1 -Version 2.4.0     # ALWAYS pass -Version
```

The build fails if `-Version` disagrees with `AgentInfo.Version`; both it and
`Directory.Build.props` must be bumped together.

`CLAUDE.md` carries the things about Tally that are not obvious and have each
cost this project time. Read it before changing extraction.
`docs/idempotency-contract.md` is the agreement with the ingestion API about
record identity — read it before changing what any dataset emits.
