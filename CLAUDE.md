# Tally BigQuery Agent — working notes

A Windows service + WPF Manager that extracts data from TallyPrime and uploads it
to GCP (GCS → BigQuery) for Dynalektric Equipment Private Limited. Part of
Dynalektric Enterprise AI, Domain 1 (Finance & Accounts).

Current version: **2.4.0**. Installed on the Tally server.

## Build, test, release

```powershell
dotnet build TallyBigQueryAgent.sln -c Release
dotnet test
.\build\build.ps1 -Version 2.4.0        # ALWAYS pass -Version
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
- **Tally silently ignores an unknown `<FETCH>` entry.** `.LIST` is how Tally
  SERIALISES a list-valued member, not a member you can fetch — ask for it and
  you get a valid response with the sub-object absent, zero rows and no error.
  Use the dotted form (`ALLLEDGERENTRIES.BILLALLOCATIONS.*`). **A dataset
  returning nothing with no error is the signature.** `opening_bills` fetched
  `BILLALLOCATIONS.LIST` and returned zero rows for its entire history while
  bill-wise details were enabled in Tally; nothing ever reported a problem
  because nothing had gone wrong as far as Tally was concerned.
- **The report envelope shape: `TALLYREQUEST=Export` + `TYPE=Data` +
  `ID=<report>`, static variables under `BODY/DESC`.** The
  `TALLYREQUEST=Export Data` + `EXPORTDATA`/`REQUESTDESC`/`REPORTNAME` form is
  REFUSED outright with "Unknown Request, cannot be processed". Measured
  2026-09-04: refused and accepted shapes, same server, same company, same
  dates, seconds apart. The agent sent the refused form from its first commit
  until v2.4.0, which is why `trial_balance` had never once come from the report
  route, why the bills envelope hunt never converged, and why the Day Book
  voucher path was abandoned in `aeb6dca`. One wrong element name, three
  separate investigations, several weeks.
- **Voucher COLLECTIONS ignore `SVFROMDATE`.** They serve from the
  financial-year start regardless, so a windowed walk asks Tally to serialise a
  whole year per window and throws ~99% away. Measured: a one-day collection
  request returned 4,355 vouchers, 5,640,137 bytes. Use the **Day Book report**,
  which honours the window. Reports honour the date variables; collections do
  not.
- **`##SVFromDate` does not resolve inside a TDL formula here — a filter using
  it is INERT, not slow.** Measured: the same collection with and without
  `<FILTER>$Date >= ##SVFromDate ...</FILTER>` came back BYTE-IDENTICAL
  (4,355 vouchers); with a literal `$$Date:"1-Sep-2026"` it returned 4.
  `<FILTER>` and `<FILTERS>` behave identically, so the element name was not it
  either. **`197f055` banned this filter for "forcing a full scan" — that was a
  misattribution.** The observation (4-day and 1-month windows timing out
  identically) is exactly what you see when the window never mattered, and the
  window is ignored *without* the filter too. One symptom, two candidate causes,
  and the one removed was never tested against the other. Do not re-ban it on
  that reasoning; if you need a date-scoped collection, use literal dates
  (`TallyEnvelopes.VoucherDatesForCounting`).
- **Size a voucher window from measured bytes.** One day of Day Book is 424,860
  bytes for 3 vouchers (~142 KB each — full detail, GST duty heads, address
  lists, invoice lines) at ~28 vouchers/day. 7 days ≈ 28 MB against
  `maxResponseMb` 256; 30 days ≈ 119 MB. The default is 7 because of that
  arithmetic, not by habit.
- **A guard must be able to fire under the mechanism it guards.** The v2.3.0
  period guard worked by noticing Tally OVER-serving a window — the symptom of
  the bug v2.4.0 fixes. Once the window is honoured that signal never appears
  and the guard would pass silently forever. It was re-derived with the polarity
  inverted: any out-of-window row is now an alarm, checked on every window.
  Completeness is proven separately by per-financial-year counts taken from
  Tally (`TallyAgent.Cli verify --fy-counts`).
- **Tally's exports are UTF-16LE with a BOM, and a decoded BOM breaks the
  parse.** `XDocument.Parse` rejects a leading U+FEFF as "Data at the root level
  is invalid" — on byte one, before any extractor runs. `TallyXml.Decode`
  resolves the encoding as BOM → XML declaration → HTTP charset → strict UTF-8
  → Latin-1, and strips the BOM. Decoding single-byte text as UTF-8 turns every
  byte above 0x7F into U+FFFD **silently**, which is how `SS M8×40MM` became
  `SS M8<U+FFFD>40MM`.
- **Report exports use `d-MMM-yy` dates** ("1-Nov-21"), not the `yyyyMMdd` that
  collections use. A missing format makes every date null while the rest of the
  row looks perfectly fine.
- **In the Bills report, the amount is a SIBLING of the record.**
  `<BILLFIXED>` holds BILLDATE/BILLREF/BILLPARTY; `BILLCL`, `BILLDUE` and
  `BILLOVERDUE` follow it as siblings. Searching inside the container yields
  rows with 0.00 amounts that look like a working extraction. Amounts are
  negative for credit.
- **The Trial Balance report is `DSPCLDRAMTA`/`DSPCLCRAMTA`, debits NEGATIVE.**
  Not `DSPCLDR`/`DSPCLCR`/`BSMAINAMT` — reading those gives rows of zeros.
- **Every report with a fallback must emit a `source` column.** `trial_balance`
  reconciled perfectly for weeks while coming entirely from the ledger fallback,
  because both routes derive from the same balances. A number that is right by
  accident should still say which route it came by.
- **Tally does not expose the active period (Alt+F2) over XML.**
  `STARTINGFROM`/`ENDINGAT` on the Company object is the BOOKS range — measured
  2019-04-01..2026-09-04 while the Gateway showed 1-Apr-26 to 31-Mar-27. The
  active period is INFERRED instead: Tally ignores a requested voucher window
  that starts before it and serves from the period start anyway, so the earliest
  date it volunteers is that start. A window that returns nothing while Tally
  sent vouchers from elsewhere is a FAILURE, not an empty window — that
  combination is how a silent multi-year backfill gap is created.
- **A refusal must fall back wherever a fallback exists.** v2.2.0 made a refusal
  throw centrally, which was right, but it threw past the fallback and
  `trial_balance` went from 1,038 rows to zero batches. Detecting a refusal and
  deciding what to do about it are different decisions: surface it loudly, then
  take the route you already had.
- **Do not ask Tally to compute outstandings.** `bill_allocations` carries
  bill_ref, bill_type (New Ref / Agst Ref / Advance / On Account), amount,
  ledger_name (the party), voucher_guid, voucher_date and voucher_type. Matching
  New Ref and Advance against Agst Ref by bill_ref within a party reproduces
  Bills Payable and Bills Receivable in SQL — which is what tally-database-loader
  does, and it never requests those reports either. `bills_payable` and
  `bills_receivable` were retired for this reason.
- **`balance_sheet`, `profit_loss` and `stock_summary` default to FALSE** from
  v2.3.0, with no config entry needed. Do not re-enable them.
- **Verify against Tally's own UI export, not against remembered figures.**
  `TallyAgent.Cli verify --bills <file> --trial-balance <file> [--live]` diffs
  record for record. Reference exports are live accounting data — never commit
  them.
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
