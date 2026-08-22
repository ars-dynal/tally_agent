# Code review — Tally slow / stuck while agent v2.0.4 runs

> **Status:** all A-items and B1–B3/B6/B7 implemented in **v2.0.5** (see CHANGELOG.md). A6 part 2 (AlterID `<FILTER>` on master collections) was deliberately NOT done — it would change the warehouse contract from replace-all to upsert-by-guid; the company-wide master gate + single fetch gives most of the benefit. B5 (agent-side memory) left for a later release.

Scope: `TallyClient`, `TallyEnvelopes`, `SyncEngine`, `SyncPlanner`, `SyncWorker`,
`MasterExtractor`, `ReportExtractor`, `VoucherExtractor`, `AgentConfig`, workers.

Short version: v2.0.4 fixed *concurrency* (one request at a time) but not
*load*. Every 15-minute cycle still fires ~25–30 heavy requests back-to-back,
several of which force Tally to compute full-financial-year reports and
closing balances for every ledger/stock item. On top of that, a client-side
timeout does not stop Tally — it keeps computing the abandoned request while
the agent immediately sends the next one, so requests pile up inside Tally's
single XML thread. That pile-up is what operators see as "stuck".

---

## A. Findings that directly cause the slowness / freeze

### A1. Timeout → Tally keeps working, agent immediately sends more (stuck)
`TallyClient.PostOnceAsync` cancels *our* socket on timeout. Tally does not
abort; it finishes the abandoned export anyway. The comment in the class
header says this, and the reconnect path handles it (TCP probe first), but the
**timeout path does not**:

* `PostAsync` retry ladder: after a timeout it waits only 10 s and re-sends
  the same request. Tally is still busy with the first one → the second queues
  behind it → also times out → 30 s → third… Up to 4 copies of a heavy report
  can be queued inside Tally from one dataset.
* `SyncEngine` voucher split: on `TallyTimeout` the window is split and the
  next half is sent immediately — again while Tally is still serving the
  abandoned 5-minute request.
* A TCP probe does **not** detect this: Tally's listener accepts the
  connection even while busy, so `TryTcpProbeAsync` returns true.

Fix: after any timeout, before sending anything else, send a tiny request
(`CompanyList`) with a long timeout (≥ the timeout that just expired) and wait
for it to answer. Because Tally serves requests serially, that call returns
only once Tally has drained the abandoned work. Don't debit the retry budget
for this "drain" call; treat it as the cooldown.

### A2. Cycles run back-to-back when one cycle takes longer than the interval
`SyncWorker.ExecuteAsync` sets `nextRun = now + interval` **before** the cycle
starts. With ~30 requests at 120–300 s timeouts a cycle can easily exceed
15 min, after which the next cycle starts immediately. Tally is then under
continuous load with no idle gap, all day.

Fix: set `nextRun` after `RunCycleAsync` returns (`finished + interval`), and
consider a minimum quiet gap (e.g. 5 min) regardless of interval.

### A3. Snapshot reports are full-FY computations on every cycle
`ExtractMasterOrSnapshot` exports **Trial Balance, Balance Sheet, P&L and
Stock Summary from 1 April to today** every cycle (whenever any master *or*
voucher changed — i.e. practically always during business hours). Full-year
Stock Summary on an inventory-heavy company is one of the heaviest things
Tally can be asked to do; it locks the UI for the whole duration and at 120 s
it routinely times out → A1 cascade. There is also a fallback chain: an empty
report triggers a second full-company collection.

Fix: run snapshots once a day (off-hours, e.g. piggyback on
`DailyHealthWorker` hour) or only on Force Full Sync; give them their own
timeout; never retry them at the same size within a cycle (pass
`maxTimeoutRetries: 0` like vouchers).

### A4. Master collections force Tally to compute balances/valuations
* `Ledgers()` fetches `OPENINGBALANCE`, `CLOSINGBALANCE` → Tally computes the
  closing balance of every ledger (a full ledger scan).
* `StockItems()` fetches `CLOSINGBALANCE`, `CLOSINGVALUE`, `CLOSINGRATE` →
  full stock valuation for every item (very heavy with FIFO/Avg costing and
  many godowns).
* `Outstanding()` (×2) and `TrialBalanceFromLedgers` fetch all ledgers with
  `CLOSINGBALANCE` again.

These are "master" datasets but behave like reports. Fix: drop computed
fields from master collections (balances belong to trial_balance / stock
summary snapshots), or move them to the daily snapshot schedule.

### A5. The same collection is pulled 4–5 times per cycle
Per cycle Tally serves: Ledger collection **5×** (ledgers, opening_bills,
outstanding_payables, outstanding_receivables, BankLedgerNames, +1 for TB
fallback), StockItem collection **4×** (stock_items, gst_rates,
stock_standard_costs, stock_standard_prices). Each is a separate full
serialization inside Tally.

Fix: fetch Ledger once and StockItem once per cycle with the union of fields,
derive the dependent datasets in memory (exactly what `VoucherExtractor`
already does for vouchers). That alone removes ~7 Tally requests per cycle.

### A6. Masters are re-exported in full every cycle — no AlterID filtering
The AlterID gate only skips when *nothing* in the company changed. Any single
voucher entry during the day flips `vouchersUnchanged` → the `if
(mastersUnchanged && vouchersUnchanged)` condition fails → **all 15 master
collections + 6 snapshots re-run** even though only vouchers changed.

Fix (two parts):
1. Gate masters on `mastersUnchanged` alone, vouchers on `vouchersUnchanged`
   alone (they are independent watermarks — the combined condition wastes
   the whole point of the gate).
2. For masters, add `<FILTER>` on `$AlterID > {lastAlterId}` so Tally only
   serializes changed masters (the standard tally-database-loader technique).
   Note: this is a master-table filter, not the voucher full-scan filter that
   2.0.2 removed — master collections are small enough for it.

### A7. No breathing gap between master/snapshot requests
`windowPauseSeconds` is applied only between voucher windows. The 20+
master/snapshot requests are sent with zero gap. Move the pause into
`TallyClient.PostOnceAsync` (after release) so *every* request is followed by
the gap, or apply it in the master/snapshot loop too.

### A8. Voucher envelope requests both ALLLEDGERENTRIES.* and LEDGERENTRIES.*
`VoucherCollection` FETCHes every dotted field twice — once under
`ALLLEDGERENTRIES` and once under `LEDGERENTRIES` (same for inventory). Tally
serializes both lists, so the response (and Tally's work) is roughly doubled;
the extractor then discards one of them. Pick `ALLLEDGERENTRIES` /
`ALLINVENTORYENTRIES` only (they are a superset on all TallyPrime builds the
loader project supports). If an old build needs the fallback, make it a
config switch, not a double fetch.

### A9. 300 s voucher timeout × 31-day windows
During a full history walk each window is allowed to hold Tally's UI for up
to 5 minutes; on a timeout the split halves follow (A1). For a busy company
31 days is too big to begin with. Suggest `fullSyncChunkDays` 7 (default)
and `voucherTimeoutSeconds` 120, and let a *successful* window that took
> 60 s shrink the next chunk adaptively (adaptive-down only, never up, during
business hours).

---

## B. Smaller correctness / robustness points

* **B1.** `maxConcurrentTallyRequests` allows 2. The comment itself says 2
  "is known to stall TallyPrime". Clamp to 1 and remove the option, or at
  least make 2 require an explicit `iKnowWhatImDoing` flag.
* **B2.** `PostAsync` retries `TallyPortUnavailable` (any non-2xx HTTP) as a
  reconnect for up to 30 min. Tally returns HTTP errors for bad TDL too —
  that will loop for 30 min on a permanent error. Only retry 5xx / connection
  refused; fail fast on 4xx.
* **B3.** Run-level retry budget (`maxRetriesPerRun` 20) is high: 20 × up to
  5-minute abandoned requests is worst-case 100 min of Tally stuck inside one
  cycle. 5 is plenty once A1 is fixed.
* **B4.** `AgentDatabase`/`RecordWindowCoverage` open a new SQLite
  connection per call — fine for volume, but SQLite writes happen while the
  Tally gate is **not** held, good. No change needed; noting it was checked.
* **B5.** `VoucherExtractor` materialises every ledger line three times
  (`Vouchers`, `DayBook`, `VoucherLines`) plus `Manifest` — agent-side memory
  only, not Tally's problem, but a 31-day window on a large company can be
  several hundred MB of `Dictionary<string,object>`. Consider building
  `day_book` and `vouchers` in `BatchBuilder` from `voucher_lines` instead.
* **B6.** `HttpClient` is created with default keep-alive. Tally's HTTP
  server is happier with `Connection: close`; a lingering keep-alive socket
  has been seen to keep Tally's request slot "busy". Set
  `request.Headers.ConnectionClose = true`.
* **B7.** `TryTcpProbeAsync` success is used as "Tally recovered" — see A1,
  it only proves the listener is up, not that Tally is idle.
* **B8.** Manager `RefreshStatus` every 10 s does not hit Tally (checked) —
  OK. Heartbeat/DailyHealth never touch Tally — OK.

---

## C. Recommended order of work

1. **A1 drain-after-timeout** + **A2 nextRun after completion** — small
   changes in `TallyClient.PostAsync` / `SyncWorker`, removes the "stuck"
   pile-up. Do these first.
2. **A6 split the gate** (masters vs vouchers independent) — one-line
   condition change, immediately stops re-exporting masters on every voucher
   edit.
3. **A3 + A4** move FY snapshots and balance/valuation fields to a once-daily
   off-hours schedule.
4. **A5 + A8** dedupe Ledger/StockItem fetches and the double voucher FETCH.
5. **A7 + A9** per-request pause, smaller default chunk, lower voucher
   timeout.
6. B1–B3 config hardening.

Expected effect: a normal business-hours incremental cycle drops from
~25–30 heavy requests (several full-FY) to ~3–5 light ones (AlterID check,
one small voucher window, changed masters only), with Tally guaranteed idle
between cycles.

---

## D. Config to apply now (no code change) to reduce pain immediately

```json
"tally": {
  "syncFrequencyMinutes": 30,
  "requestTimeoutSeconds": 120,
  "voucherTimeoutSeconds": 120,
  "fullSyncChunkDays": 7,
  "windowPauseSeconds": 10,
  "maxConcurrentTallyRequests": 1,
  "maxRetriesPerRun": 5,
  "incrementalLookbackDays": 3
}
```
And run Force Full Sync only after office hours until A1–A3 are in.
