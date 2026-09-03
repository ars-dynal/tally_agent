# Candidate envelopes

Raw Tally request envelopes, posted verbatim to find out which SHAPE Tally
accepts — before any extractor is written against one.

```powershell
TallyAgent.Cli capture-xml --envelope-dir diagnostics\envelopes `
  --from 2026-04-01 --to 2026-09-03 --dump
```

Placeholders `{{COMPANY}}`, `{{FROM}}` and `{{TO}}` are substituted by the CLI
(`FROM`/`TO` in Tally's `yyyyMMdd` form). Files run in filename order and a
refusal does **not** stop the run — a refusal is a result.

For each envelope the CLI prints ACCEPTED or REFUSED with Tally's own words, the
response size, an element histogram and the first 600 characters. `--dump` saves
each raw response under `%ProgramData%\TallyBigQueryAgent\fixtures`.

## Read `00` first

`00-control-trial-balance.xml` is a **control**, not a candidate. It is exactly
what `TallyEnvelopes.Report("Trial Balance", …)` builds today.

- **`00` accepted** → the session, company and period are fine, and any refusal
  below is about that envelope: the report name or the request shape.
- **`00` refused** → the problem is the session or the company, not the report
  name, and every variant below is wasted effort. Fix that first.

`00` also settles an open question about `trial_balance` itself. `TrialBalance()`
falls back to `TrialBalanceFromLedgers()` whenever the report yields no rows, and
both routes derive from the same ledger balances — so a fallback result
reconciles to Tally's screen exactly as the report would. If `00` is refused,
the dataset has been served by the fallback all along and nothing would have
shown it. That is why every row now carries a `source` column.

## The candidates

| File | What it tests |
|---|---|
| `00-control-trial-balance` | **Control.** A report shape known to work. |
| `01-bills-payable-no-explodeflag` | `Bills Payable` in the exact shape of the control. `EXPLODEFLAG` was added on reasoning, not evidence, and is the one thing separating the v2.2.0 envelope from `Report()`. |
| `02-bills-payable-export-type-data` | Alternative header: `TALLYREQUEST>Export`, `TYPE>Data`, `ID>Bills Payable`, static variables under `BODY/DESC`. |
| `03-bills-collection` | The `Bills` collection posted directly — the v2.2.0 fallback path, on its own. |
| `04-bills-payable-with-explodeflag` | Exactly what v2.2.0 sends today. Included so the comparison against `01` is one line. |
| `05-bills-outstanding-report-name` | Report name `Bills Outstanding`. |
| `06-ledger-outstandings-report` | Report name `Ledger Outstandings`. |
| `07-bills-receivable-no-explodeflag` | The receivable mirror of `01`, to confirm whatever works is not payable-only. |

## What to send back

The whole console output. If something is ACCEPTED, its element histogram is
what the parser gets written against — the tag names in `ReportExtractor` are
currently candidates, not confirmed.
