# Tally BigQuery Agent — working notes

A Windows service + WPF Manager that extracts data from TallyPrime and uploads it
to GCP (GCS → BigQuery) for Dynalektric Equipment Private Limited. Part of
Dynalektric Enterprise AI, Domain 1 (Finance & Accounts).

Current version: **2.2.0**. Installed on the Tally server.

## Build, test, release

```powershell
dotnet build TallyBigQueryAgent.sln -c Release
dotnet test
.\build\build.ps1 -Version 2.2.0        # ALWAYS pass -Version
```

- **`build.ps1` no longer has a literal default version** (v2.2.0). It reads
  `<Version>` from `Directory.Build.props` and FAILS the build when
  `AgentInfo.Version` disagrees. Before that it defaulted to `-Version "1.0.0"`,
  so omitting the flag stamped 1.0.0 onto every assembly and the installer via
  `-p:Version=`, overriding `Directory.Build.props`, while the Manager title bar
  still read the real version from the `AgentInfo.Version` constant — two
  different version numbers for one build. Still pass `-Version` explicitly.
- **PowerShell, never cmd.** cmd exits silently on a `.ps1` and produces no
  output and no installer. A stale installer shipped under a new tag this way.
- Version lives in **two** places and both must be bumped:
  `src/TallyAgent.Core/AgentVersion.cs` and `Directory.Build.props`.

## Release discipline

Two mistakes have each cost a release:

1. **Merge the PR before tagging.** A tag was created while `main` still held
   the previous version's code.
2. **Verify the effect, never the exit code.** Before `git tag`:
   ```powershell
   git show HEAD:src/TallyAgent.Core/AgentVersion.cs | Select-String 'Version ='
   ```
   It must print the new version. A stale `.git/index.lock` once blocked
   `git add`/`git commit` while `git push` and `git tag` succeeded — every
   command reported success and the release was wrong.

After building, check the installer's timestamp and size actually changed.
"Build complete" is not evidence.

## Things about Tally that are not obvious

- **The active period governs what can be exported.** A company's active period
  (Alt+F2) bounds every voucher export. `SVFROMDATE`/`SVTODATE` in the request
  do NOT override it — a request outside the period returns an empty, valid
  response with no error. Six years looked empty for three weeks because of
  this. Year-by-year backfill means moving Tally's period, not just the config.
- **Reports are computed, not stored.** Trial Balance, Balance Sheet, P&L, Stock
  Summary and the outstandings are calculated live by walking vouchers.
- **`balance_sheet`, `profit_loss` and `stock_summary` hang tally.exe.** Observed
  2026-09-02: a run reached `balance_sheet` at dataset 16 of 34 and Tally had to
  be force-closed. Keep them OFF; derive them in BigQuery. **A timeout cannot fix
  this** — `snapshotTimeoutSeconds` already exists and abandoning the request does
  not stop Tally computing (see `TallyClient.NeedsDrain`).
- **Tally serialises user-defined fields with an undeclared namespace prefix**
  (`<UDF:FIELD>`), which invalidates the whole response. `TallyXml.Sanitize`
  declares undeclared prefixes. Do not remove it.
- **Do not reintroduce a `<FILTER>` on `$Date` in `VoucherCollection`.** Tried;
  made things worse.

## Config

`C:\ProgramData\TallyBigQueryAgent\config.json`. Secrets are DPAPI-encrypted.

- **The service reads config at startup and never re-reads it.** Saving from the
  Manager now restarts the service automatically (v2.1.0). Anything else that
  changes config must restart the service or the change silently does nothing.
- `snapshotDatasets` — per-report flags. An absent entry falls back to
  `enableSnapshots`, so existing installs are unchanged.
- `extractionStartDate` is **dead config once the full-sync checkpoint latches**
  — it is only read inside the `!FullSyncDone` branch. Only Force Full Sync
  (`ResetVoucherCheckpoint`) re-walks history.
- `emitLegacyVouchersDataset` is false: the `vouchers` dataset was a
  byte-identical copy of `day_book` (`result.Vouchers.Add(flat);
  result.DayBook.Add(new Row(flat));`).

## Data rules

- **Raw is append-only evidence; staging is current truth.** Dedup on the
  business key, newest `ALTERID` wins.
- **A wrong business key deletes data silently and nothing looks broken.**
  Inventory was grouped without unit of measure, merging KGS with ROL rows and
  understating value by ₹20.86 crore. No error, no blank cells, a plausible
  total. Before writing any staging view, ask what makes a row unique and check
  every one of those columns is in the key.
- **`SAFE_CAST('1' AS BOOL)` returns NULL in BigQuery** — it accepts only
  `'true'`/`'false'`. TRICO emits `0`/`1`. This silently nulled 49 columns.
  Encoding varies by endpoint, not by field name.
- All real data lives in `*_dev` datasets (`tally_raw_dev`, `tally_control_dev`).
  The unsuffixed names exist but are empty.

## Operating constraints

- No production GCP resource creation or modification without the user's
  explicit approval. No production data or real accounting payloads in tests.
- Never print or paste secret values — API tokens, app passwords, webhook URLs.
  Read them into env vars with `read -rsp` or Secret Manager.
- Extraction and human use compete for Tally's single application thread. Heavy
  work belongs outside 9am–8pm.
