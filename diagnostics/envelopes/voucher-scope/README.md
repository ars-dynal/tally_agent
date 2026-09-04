# What does this Tally actually scope vouchers by?

Five probes that settle it in one pass. **All five fetch `DATE` only or are a
single day/month**, so together they cost Tally far less than one of the 85
windows the stalled full sync was issuing.

```powershell
TallyAgent.Cli capture-xml --envelope-dir diagnostics\envelopes\voucher-scope --dump
```

Dates are **baked into each file** (not `{{FROM}}`/`{{TO}}`), because the probes
deliberately use different ranges in one run. Only `{{COMPANY}}` is substituted.
Edit the dates in the files if 1-Sep-2026 has no vouchers.

Read the `<VOUCHER>` count in the element histogram and the response size. That
count is the whole answer.

| # | Probe | If the window IS honoured | If it is NOT |
|---|---|---|---|
| 10 | Voucher collection, 1 day, **no filter** — what v2.3.0 sends | a handful of vouchers | thousands, dated back to 2019 |
| 11 | Same, **with the banned `$Date` TDL filter** | a handful | thousands |
| 12 | **Day Book report**, same 1 day — what the agent used before 2026-08-05 | a handful | thousands |
| 13 | Day Book report, 1 month | one month's worth | thousands |
| 14 | Voucher collection, full range, `DATE` only | — | measures the true voucher count and a size floor |

## What each outcome means

**10 returns thousands** — confirms the diagnosis. `SVFROMDATE`/`SVTODATE` do
not scope a Voucher *collection*, so all 85 windows are asking Tally to
serialise the entire voucher file and the agent is discarding ~99% client-side.

**11 returns few while 10 returns thousands** — the `$Date` TDL filter works and
the standing ban in CLAUDE.md was a misattribution (see below). This is the
tally-database-loader technique.

**12/13 return the right range** — report exports honour the date variables and
the pre-2026-08-05 Day Book path was correct all along. Reports are known to
honour them: the Trial Balance and Bills exports both did.

**11 and 12 both work** — prefer whichever is faster at probe 13's size; the
report path is a revert rather than a new mechanism.

**Nothing scopes** — then the active period is the only scope control, and
backfill means moving Alt+F2 per financial year with the agent following it.

**14** gives the voucher count for 2019–2027 and the byte size of a
minimal voucher. Multiply by the full fetch's field count to decide whether a
single whole-range request is survivable against `maxResponseMb` (256) and
`voucherTimeoutSeconds` (180). Run it last.

## Why the `$Date` FILTER ban deserves re-examination

`aeb6dca` (2026-08-05) added it. `197f055` (2026-08-12) removed it:

> The explicit TDL FILTER ($Date >= ## AND $Date <= ##) forced Tally to
> materialize and scan the ENTIRE voucher file on every window, which is why
> 1-month and 4-day windows timed out identically at 120s.

The **observation** was real: 1-month and 4-day windows timed out identically.
The **inference** — that the filter caused it — does not follow, because we now
have independent proof that the window is ignored *without* the filter too
(a request for 2026-08-05..2026-09-04 returned vouchers dated 2026-04-01).

Identical timings across window sizes are exactly what you would see if the
window never mattered. That is one symptom with two candidate causes, and the
one that was removed was not tested against the other. Probes 10 and 11 separate
them for the first time.

Note what removing it actually changed: with the filter, Tally scans the period
and returns the matching subset; without it, Tally scans the period and returns
**everything**. If the scan is the cost either way, the removal made the
transfer 85× worse without fixing the scan — which is what the stall looks like.
