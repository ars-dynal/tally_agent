Read CLAUDE.md in the repo root first — it carries the rules this project
learned the hard way, and several of them will bite you if you skip it.

## Where things stand

- Branch `feat/v2.1.0-per-dataset-snapshots`, pushed to origin. Not merged, not tagged.
- Version 2.1.0 in both `AgentVersion.cs` and `Directory.Build.props`.
- `dotnet build -c Release` succeeds. `dotnet test` — 89 passed, 0 failed.
- Installer already built at `dist\Tally BigQuery Agent Setup.exe`, stamped 2.1.0.0.
- The user installs it and controls services manually. You run at standard user
  level, so do NOT attempt `Stop-Service`, `Start-Service`, or run Setup.exe.

## What v2.1.0 changes, so you understand the intent

`balance_sheet` makes Tally compute across the whole company and has hung
tally.exe until force-closed. `outstanding_payables` and
`outstanding_receivables` sit AFTER it in run order, so they have never once
been attempted — which is why they have zero rows in BigQuery for their entire
history. They were never broken; they were unreachable.

- `snapshotDatasets` — per-report flags. An absent entry falls back to
  `enableSnapshots`, so existing configs behave exactly as before.
- Zero-row reporting now keys on dataset name, not `DatasetKind`, so
  `opening_bills` (a Master) can no longer checkpoint silently on nothing.
- `emitLegacyVouchersDataset` defaults false — the `vouchers` dataset was a
  byte-identical copy of `day_book`.
- Manager: one primary "Sync Now"; Start/Stop/Restart, Retry, Logs, Diagnostics
  and "Re-extract All History…" moved behind an Advanced expander; six report
  checkboxes with the three that freeze Tally marked in red; Save now restarts
  the service instead of telling the operator to.

## Task 1 — finish the release

1. Verify the pushed branch actually carries the code:
   `git show origin/feat/v2.1.0-per-dataset-snapshots:src/TallyAgent.Core/AgentVersion.cs`
   must show 2.1.0.
2. Create the PR if `gh pr list` doesn't show one, then merge it.
3. `git checkout main && git pull`, then:
   `git show HEAD:src/TallyAgent.Core/AgentVersion.cs` — must print 2.1.0
   BEFORE you tag. A previous release tagged code from the prior version
   because a stale `.git/index.lock` blocked the commit while push and tag
   both reported success. Verify the effect, never the exit code.
4. Tag v2.1.0, create the GitHub release, attach
   `dist\Tally BigQuery Agent Setup.exe`.

## Task 2 — two small fixes that belong in this release

**`build\build.ps1` line 2:** `[string]$Version = "1.0.0"`. That default is four
minor versions stale, and omitting `-Version` stamps 1.0.0 onto every assembly
and the installer via `-p:Version=`, overriding `Directory.Build.props`, while
the Manager still displays the real version from the `AgentInfo.Version`
constant — two version numbers for one build. Make the default read
`<Version>` from `Directory.Build.props` so they cannot drift.

**`.gitignore`:** add `__pycache__/`, `dist/`, `*.patch`.

## Task 3 — the one v2.1.0 item still unwritten

Masters are re-extracted and re-uploaded every cycle even when nothing changed.
Measured: ~10,757 master rows uploaded every hour, ~247,000 redundant rows
across the historical backfill. Raw holds 2,297,450 rows against ~937,700
distinct.

Skip the upload when a dataset's content is unchanged since the last successful
upload:

- Hash the canonical serialisation of the extracted rows for a Master dataset.
- Persist the hash per (dataset, company) — `MasterBalanceRepository` is the
  precedent for a small SQLite store; a new table plus a migration in
  `AgentDatabase` is the natural shape.
- In `SyncEngine`, when the hash matches the stored one, advance the checkpoint
  but do NOT call `EnqueueAndCheckpoint` with a batch.
- Balances change daily, so expect this to reduce hourly uploads to roughly
  daily rather than eliminate them. That is still a ~24x reduction.

Be careful here: this touches the upload path, and the failure mode is masters
silently not uploading, which nothing would notice. Write the tests first —
unchanged content skips, changed content uploads, first-ever run uploads, and a
failed upload does not record the hash.

## Task 4 — after the user installs, verify the effect

The user will install, set the three heavy reports off and the three others on,
and run a sync. Then, with `bq`:

```sql
-- did the outstandings finally produce rows?
SELECT dataset_name, COUNT(*) AS rows, MIN(loaded_at), MAX(loaded_at)
FROM `dynalektric-enterprise-ai.tally_raw_dev.records`
WHERE loaded_at >= TIMESTAMP_SUB(CURRENT_TIMESTAMP(), INTERVAL 1 DAY)
  AND dataset_name IN ('outstanding_payables','outstanding_receivables',
                       'trial_balance','opening_bills')
GROUP BY dataset_name ORDER BY dataset_name
```

```sql
-- if they did not, this now says why
SELECT received_at, dataset, category, severity, SUBSTR(message,1,300) AS message
FROM `dynalektric-enterprise-ai.tally_control_dev.agent_errors`
WHERE received_at >= TIMESTAMP_SUB(CURRENT_TIMESTAMP(), INTERVAL 1 DAY)
  AND agent_id NOT LIKE 'SMOKE-TEST%'
ORDER BY received_at DESC
```

Also confirm `vouchers` stops appearing for new loads while `day_book`
continues, and that the Manager's dataset count reads out of 31 (34 minus the
three heavy reports), not 28 or 34.

`opening_bills` will probably still be empty. That is a Tally-side question
about whether bill-wise details are enabled, not a bug — but it should now
report a warning rather than checkpointing in silence.

## Constraints

- No production GCP resource creation or modification without the user's
  explicit approval.
- Never print or paste secret values.
- Do not run elevated commands; service control and installation are manual.
- Do not reintroduce a `<FILTER>` on `$Date` in `VoucherCollection` — tried,
  made things worse.
- `balance_sheet`, `profit_loss` and `stock_summary` stay OFF permanently. They
  get derived in BigQuery. A timeout cannot fix them: `snapshotTimeoutSeconds`
  already exists, and abandoning the request does not stop Tally computing.
