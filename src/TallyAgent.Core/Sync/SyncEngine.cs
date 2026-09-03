using Microsoft.Extensions.Logging;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Data;
using TallyAgent.Core.Notifications;
using TallyAgent.Core.Tally;
using TallyAgent.Core.Tally.Extractors;

namespace TallyAgent.Core.Sync;

using Row = Dictionary<string, object?>;

public sealed record SyncResult(string SyncId, string Status, int DatasetsOk,
    int DatasetsFailed, long RowsExtracted, List<string> Errors);

/// <summary>
/// Orchestrates one sync cycle:
///   • preflight (Tally reachable, company open, disk space)
///   • plan (full-history month chunks on first run; lookback window afterwards)
///   • extract per dataset with per-dataset error isolation
///   • enqueue batches + advance checkpoints atomically per dataset
/// Extraction NEVER waits on the network — upload is a separate worker.
/// </summary>
public sealed class SyncEngine(
    AgentConfig config,
    TallyClient tally,
    MasterExtractor masters,
    VoucherExtractor vouchers,
    ReportExtractor reports,
    BatchBuilder batchBuilder,
    CheckpointRepository checkpoints,
    BatchQueueRepository queue,
    ErrorReporter reporter,
    AgentDatabase db,
    ILogger<SyncEngine> log)
{
    private readonly SyncProgressSnapshot _progress = new();

    /// <summary>What the engine is doing right now. Assigning also publishes the
    /// progress snapshot to disk, so the Management Console can display live
    /// progress without reaching into this process.</summary>
    public string CurrentOperation
    {
        get => _progress.Operation;
        private set
        {
            _progress.Operation = value;
            SyncProgressStore.Write(_progress);
        }
    }

    /// <summary>Bank ledger names reused across cycles while masters are
    /// unchanged (saves a Ledger fetch on every incremental cycle).</summary>
    private HashSet<string>? _bankLedgerCache;

    /// <summary>Checkpoint row that records the last daily master-balance capture.</summary>
    private const string MasterBalancesCheckpoint = "_master_balances";

    public Task<SyncResult> RunCycleAsync(string mode, CancellationToken ct) =>
        RunCycleAsync(mode, null, ct);

    /// <summary><paramref name="preflight"/>: a probe the caller just performed
    /// (SyncWorker probes for AgentState/mode anyway) — passing it avoids the
    /// historical double-probe of Tally at the start of every cycle.</summary>
    public async Task<SyncResult> RunCycleAsync(string mode, TallyProbeResult? preflight,
        CancellationToken ct)
    {
        var syncId = Guid.NewGuid().ToString("N")[..12];
        var started = DateTime.UtcNow;
        // A crash mid-run leaves sync_runs.status='running' forever; mark such
        // rows abandoned so the console never shows a phantom active run and
        // nothing can key decisions off a stale 'running' row.
        MarkAbandonedRuns();
        // Arm the per-run Tally retry budget (Phase F8): timeouts/reconnects
        // across ALL datasets and windows share one bounded pool this cycle.
        tally.ResetRunBudget(config.Tally.MaxRetriesPerRun);
        reports.BeginCycle();
        RecordRunStart(syncId, mode);
        _progress.SyncId = syncId;
        _progress.Mode = mode;
        _progress.Status = "running";
        _progress.Message = "";
        _progress.StartedUtc = DateTime.UtcNow.ToString("O");
        _progress.DatasetsDone = 0;
        _progress.DatasetsTotal = 0;
        _progress.WindowsDone = 0;
        _progress.WindowsTotal = 0;
        _progress.Rows = 0;
        _progress.RangeFrom = config.Tally.ExtractionStartDate;
        _progress.RangeTo = string.IsNullOrWhiteSpace(config.Tally.ExtractionEndDate)
            ? "today" : config.Tally.ExtractionEndDate;
        var errorList = new List<string>();
        var extractedCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int ok = 0, failed = 0;
        long totalRows = 0;

        try
        {
            // ── Preflight ──────────────────────────────────────────
            CurrentOperation = "preflight";
            CheckDiskSpace();

            // Force Full Sync with a configured company: reset the checkpoint
            // BEFORE the probe, so the request survives even if Tally happens to
            // be closed right now. Auto-discovery resets once, after the probe.
            if (mode == "full-forced" && !string.IsNullOrWhiteSpace(config.Tally.Company))
                ResetVoucherCheckpoint(config.Tally.Company);

            var probe = preflight ?? await tally.ProbeAsync(ct);
            if (!probe.Ok)
            {
                if (mode == "full-forced" && string.IsNullOrWhiteSpace(config.Tally.Company))
                {
                    // Auto-discover company: the reset hasn't happened yet — re-arm
                    // the trigger so the request isn't silently lost.
                    try
                    {
                        File.WriteAllText(Path.Combine(AgentInfo.TriggerDir, "force-full.trigger"),
                            DateTime.UtcNow.ToString("O"));
                    }
                    catch { /* best effort */ }
                }
                var category = probe.Category ?? ErrorCategory.TallyNotRunning;
                await reporter.ReportAsync(category, ErrorSeverity.Error,
                    probe.Error ?? "Tally probe failed",
                    operation: "preflight", ct: CancellationToken.None);
                _progress.Status = "failed";
                _progress.Message = probe.Error ?? "Tally unavailable";
                RecordRunFinish(syncId, "failed", 0, probe.Error);
                return new SyncResult(syncId, "failed", 0, 0, 0, [probe.Error ?? "Tally unavailable"]);
            }

            var company = ResolveCompany(probe.Companies);
            var enabled = DatasetRegistry.Enabled(config.Tally);
            log.LogInformation("Sync {SyncId} ({Mode}) starting: company='{Company}', {N} datasets",
                syncId, mode, company, enabled.Count);
            _progress.Company = company;
            _progress.DatasetsTotal = enabled.Count;

            // Configured-company force-full already reset above. Only the
            // auto-discovery path still needs a reset here.
            if (mode == "full-forced" && string.IsNullOrWhiteSpace(config.Tally.Company))
                ResetVoucherCheckpoint(company);

            // ── AlterID change gate (best-effort; null ⇒ always extract) ──
            (long Masters, long Vouchers)? alterIds = null;
            if (mode == "incremental")
            {
                try { alterIds = await tally.GetCompanyAlterIdsAsync(ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogDebug("AlterID gate unavailable ({Msg}) — extracting unconditionally", ex.Message);
                }
            }
            var mastersUnchanged = alterIds is { } a1 &&
                checkpoints.Get("_alter_gate_masters", company)?.LastAlterId == a1.Masters;
            var vouchersUnchanged = alterIds is { } a2 &&
                checkpoints.Get("_alter_gate_vouchers", company)?.LastAlterId == a2.Vouchers;

            // ── Masters & snapshots ────────────────────────────────
            // v2.0.5: the two gates are INDEPENDENT. v2.0.4 skipped masters only
            // when masters AND vouchers were both unchanged — so one voucher
            // entry during the day re-exported all 15 master tables plus the
            // full-FY snapshot reports every cycle. Masters now run only when a
            // master changed; snapshots run on their own daily slot.
            // Computed balances (ledger opening/closing, stock closing qty/value/
            // rate) are asked from Tally on the same daily slot as the snapshots
            // and cached per GUID; other cycles fill them from the cache.
            var balancesDue = ShouldRunSnapshot(MasterBalancesCheckpoint, company, mode, out var balWhy);
            masters.BeginCycle(company, fetchBalances: balancesDue);
            var runMasters = !mastersUnchanged;
            if (!runMasters)
                log.LogInformation("AlterID gate: no master changes — skipping master collections" +
                    (balancesDue ? " (ledgers/stock_items still run for the daily balance capture)" : ""));
            else
                log.LogDebug("Master balances this cycle: {Mode} ({Why})", balancesDue ? "fetch" : "cache", balWhy);
            var balanceDatasetsOk = 0;
            var runEnded = false;
            foreach (var ds in enabled.Where(d => d.Kind is DatasetKind.Master or DatasetKind.Snapshot))
            {
                ct.ThrowIfCancellationRequested();
                var isBalanceDataset = ds.Name is "ledgers" or "stock_items";
                if (ds.Kind == DatasetKind.Master && !runMasters && !(balancesDue && isBalanceDataset)) continue;
                if (ds.Kind == DatasetKind.Snapshot && !ShouldRunSnapshot(ds.Name, company, mode, out var why))
                {
                    log.LogDebug("Snapshot {Dataset} skipped: {Why}", ds.Name, why);
                    continue;
                }
                _progress.DatasetsDone++;
                _progress.Rows = totalRows;
                CurrentOperation = $"extract:{ds.Name}";
                try
                {
                    var rows = await ExtractMasterOrSnapshot(ds.Name, ct);
                    extractedCounts[ds.Name] = rows.Count;
                    totalRows += rows.Count;

                    // Dataset-aware empty-result handling (§G/§7): an empty
                    // SNAPSHOT report (trial_balance, balance_sheet, …) is a
                    // suspicious outcome, not a success — do NOT advance its
                    // checkpoint (it retries next cycle) and surface a grouped
                    // warning. Most Masters may legitimately be empty, but a
                    // few may not: opening_bills checkpointed silently on zero
                    // rows for months because the guard was keyed on Kind alone.
                    if (rows.Count == 0 && DatasetRegistry.ExpectsRows(ds))
                    {
                        log.LogWarning("Dataset {Dataset} returned 0 rows where rows were expected — " +
                            "checkpoint NOT advanced; will retry next cycle", ds.Name);
                        await reporter.ReportAsync(ErrorCategory.UnexpectedException,
                            ErrorSeverity.Warning,
                            $"Dataset '{ds.Name}' returned 0 rows where rows were expected " +
                            "(report and fallback both empty).",
                            operation: CurrentOperation, dataset: ds.Name, ct: CancellationToken.None);
                        continue;
                    }

                    EnqueueAndCheckpoint(ds.Name, company, syncId, rows, null, null, fullDone: true);
                    ok++;
                    if (balancesDue && isBalanceDataset) balanceDatasetsOk++;
                }
                catch (TallyException tex) when (tex.IsRunEnding)
                {
                    // Tally is busy/exhausted: stop asking it for anything this cycle.
                    failed++;
                    await HandleDatasetErrorAsync(ds.Name, tex, errorList);
                    runEnded = true;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    await HandleDatasetErrorAsync(ds.Name, ex, errorList);
                }
            }

            // Mark the daily balance capture done only when both balance-bearing
            // datasets succeeded (or are disabled) so a failed one is retried.
            if (balancesDue && balanceDatasetsOk >= enabled.Count(d => d.Name is "ledgers" or "stock_items"))
                checkpoints.Upsert(new SyncCheckpoint(MasterBalancesCheckpoint, company,
                    null, null, null, DateTime.UtcNow.ToString("O"), true));

            // Do not silently certify a full baseline when strongly related
            // datasets contradict each other. These checks are deliberately
            // conservative and only flag impossible/suspicious combinations.
            if (!mastersUnchanged)
            {
                var validationErrors = ValidateExtractionCounts(extractedCounts);
                foreach (var warning in validationErrors)
                {
                    failed++;
                    errorList.Add(warning);
                    log.LogWarning("Data-quality validation: {Message}", warning);
                    await reporter.ReportAsync(ErrorCategory.UnexpectedException, ErrorSeverity.Error,
                        warning, operation: "validate:master-snapshots", ct: CancellationToken.None);
                }
            }

            // ── Vouchers (windowed Day Book fan-out) ───────────────
            if (!runEnded && enabled.Any(d => d.Kind == DatasetKind.Voucher))
            {
                var plan = PlanVouchers(company);
                var skipVouchers = vouchersUnchanged && !plan.IsFullSync;
                if (skipVouchers)
                {
                    log.LogInformation("AlterID gate: no voucher changes — skipping voucher extraction");
                    var todayD = DateOnly.FromDateTime(DateTime.Today);
                    AdvanceVoucherWindowCheckpoint(company, todayD, todayD);
                }
                else if (plan.RecoveredGapDays > 0)
                {
                    log.LogWarning(
                        "Recovering {Days}-day extraction gap beyond the {Lookback}-day lookback " +
                        "(agent or Tally was unavailable). Missed days are being re-extracted.",
                        plan.RecoveredGapDays, config.Tally.IncrementalLookbackDays);
                    await reporter.ReportAsync(ErrorCategory.ServiceStopped, ErrorSeverity.Warning,
                        $"Extraction gap of {plan.RecoveredGapDays} day(s) beyond the lookback window " +
                        "detected and recovered — verify the agent/Tally uptime.",
                        operation: "plan:vouchers", ct: CancellationToken.None);
                }

                if (!skipVouchers && plan.Windows.Count > 0)
                {
                    HashSet<string> bankLedgers;
                    if (_bankLedgerCache is { } cachedBanks && !runMasters)
                        bankLedgers = cachedBanks;
                    else
                    {
                        try { bankLedgers = await masters.BankLedgerNames(ct); _bankLedgerCache = bankLedgers; }
                        catch (TallyException tex) when (tex.IsRunEnding) { throw; }
                        catch { bankLedgers = _bankLedgerCache ?? []; }
                    }
                    var voucherBudget = tally.VoucherRequestTimeout;

                    // Adaptive windowing: a window that times out is split in half
                    // and retried down to single-day windows.
                    _progress.WindowsDone = 0;
                    _progress.WindowsTotal = plan.Windows.Count;
                    var pending = new Queue<(DateOnly From, DateOnly To)>(plan.Windows);
                    while (pending.Count > 0)
                    {
                        var (from, to) = pending.Dequeue();
                        ct.ThrowIfCancellationRequested();
                        _progress.WindowsDone++;
                        // Adaptive splitting can add windows mid-run, so the
                        // total is recomputed rather than fixed up front.
                        _progress.WindowsTotal = _progress.WindowsDone + pending.Count;
                        _progress.Rows = totalRows;
                        CurrentOperation = $"extract:vouchers {from:yyyy-MM-dd}..{to:yyyy-MM-dd}";
                        try
                        {
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            var result = await vouchers.ExtractWindow(from, to, bankLedgers, ct);
                            sw.Stop();
                            var wf = from.ToString("yyyy-MM-dd");
                            var wt = to.ToString("yyyy-MM-dd");

                            foreach (var (name, rows) in FanOut(result, config.Tally.EmitLegacyVouchersDataset))
                            {
                                if (!enabled.Any(d => d.Name == name)) continue;
                                totalRows += rows.Count;
                                EnqueueAndCheckpoint(name, company, syncId, rows, wf, wt,
                                    fullDone: false);
                            }
                            AdvanceVoucherWindowCheckpoint(company, from, to, plan.TargetStart);
                            RecordWindowCoverage(syncId, "vouchers", from, to,
                                result.VoucherHeaders.Count, result.MinVoucherDate,
                                result.MaxVoucherDate, "completed");
                            ok++;

                            // Adaptive-down windowing: a window that succeeded but
                            // used more than 60% of its budget is a timeout waiting
                            // to happen on the next (often busier) month. Halve the
                            // remaining windows now instead of discovering it the
                            // expensive way. Windows never grow within a run.
                            var days = to.DayNumber - from.DayNumber + 1;
                            if (days > 1 && pending.Count > 0 &&
                                sw.Elapsed > voucherBudget * 0.6)
                            {
                                var newChunk = Math.Max(1, days / 2);
                                log.LogWarning(
                                    "Window {From}..{To} took {Secs:F0}s (>60% of the {Budget:F0}s budget) — " +
                                    "shrinking remaining windows to {Chunk} day(s)",
                                    from, to, sw.Elapsed.TotalSeconds, voucherBudget.TotalSeconds, newChunk);
                                ReChunk(pending, newChunk, newestFirst: plan.TargetStart is not null);
                            }

                            // Low-impact mode: give the Tally UI room to breathe
                            // between windows (its XML server shares the app
                            // thread — the gap is when operators' screens catch up).
                            if (pending.Count > 0 && config.Tally.WindowPauseSeconds > 0)
                                await Task.Delay(
                                    TimeSpan.FromSeconds(config.Tally.WindowPauseSeconds), ct);
                        }
                        catch (TallyException tex) when (
                            tex.Category == ErrorCategory.TallyTimeout && !tex.IsRunEnding && to > from)
                        {
                            var mid = from.AddDays((to.DayNumber - from.DayNumber) / 2);
                            var secondFrom = mid.AddDays(1);

                            // IMPORTANT: every MEL template placeholder has a
                            // corresponding argument. v2.0.0 repeated {From}/{To}
                            // without arguments and the logger itself crashed here.
                            log.LogWarning(
                                "Window {OriginalFrom}..{OriginalTo} timed out — splitting into " +
                                "{FirstFrom}..{FirstTo} and {SecondFrom}..{SecondTo}",
                                from, to, from, mid, secondFrom, to);

                            var rest = pending.ToList();
                            pending.Clear();
                            if (plan.TargetStart is not null)
                            {
                                // Newest-first walk: extract the newer half first.
                                pending.Enqueue((secondFrom, to));
                                pending.Enqueue((from, mid));
                            }
                            else
                            {
                                // Forward incremental: keep chronological order.
                                pending.Enqueue((from, mid));
                                pending.Enqueue((secondFrom, to));
                            }
                            foreach (var w in rest) pending.Enqueue(w);
                            // Every later window of the old size would time out the
                            // same way — shrink them all to the new half size now.
                            ReChunk(pending, Math.Max(1, (mid.DayNumber - from.DayNumber + 1)),
                                newestFirst: plan.TargetStart is not null);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            failed++;
                            RecordWindowCoverage(syncId, "vouchers", from, to, 0, null, null, "failed");
                            await HandleDatasetErrorAsync($"vouchers[{from:yyyy-MM-dd}..{to:yyyy-MM-dd}]", ex, errorList);
                            break; // resume from the last successful checkpoint next cycle
                        }
                    }
                }
            }

            // Advance AlterID gate watermarks only after a fully successful cycle.
            if (alterIds is { } finalIds && failed == 0)
            {
                checkpoints.Upsert(new SyncCheckpoint("_alter_gate_masters", company,
                    null, null, finalIds.Masters, DateTime.UtcNow.ToString("O"), true));
                checkpoints.Upsert(new SyncCheckpoint("_alter_gate_vouchers", company,
                    null, null, finalIds.Vouchers, DateTime.UtcNow.ToString("O"), true));
            }

            var status = failed == 0 ? "success" : ok > 0 ? "partial" : "failed";
            _progress.Status = status;
            _progress.Rows = totalRows;
            _progress.Message = errorList.Count > 0 ? string.Join("; ", errorList) : "";
            RecordRunFinish(syncId, status, totalRows,
                errorList.Count > 0 ? string.Join("; ", errorList) : null);
            log.LogInformation("Sync {SyncId} {Status}: {Rows} rows, {Ok} ok, {Failed} failed ({Elapsed:F0}s)",
                syncId, status, totalRows, ok, failed, (DateTime.UtcNow - started).TotalSeconds);
            return new SyncResult(syncId, status, ok, failed, totalRows, errorList);
        }
        catch (OperationCanceledException)
        {
            _progress.Status = "cancelled";
            _progress.Rows = totalRows;
            _progress.Message = "service stopping";
            RecordRunFinish(syncId, "cancelled", totalRows, "service stopping");
            throw;
        }
        catch (Exception ex)
        {
            await reporter.ReportAsync(ErrorCategory.UnexpectedException, ErrorSeverity.Critical,
                ex.Message, ex.StackTrace, operation: CurrentOperation, ct: CancellationToken.None);
            _progress.Status = "failed";
            _progress.Rows = totalRows;
            _progress.Message = ex.Message;
            RecordRunFinish(syncId, "failed", totalRows, ex.Message);
            return new SyncResult(syncId, "failed", ok, failed + 1, totalRows, [ex.Message]);
        }
        finally
        {
            masters.EndCycle();   // release cached Ledger/StockItem documents
            reports.EndCycle();
            CurrentOperation = "idle";
        }
    }

    // ── planning ──────────────────────────────────────────────────

    /// <summary>Snapshot reports (full-FY TB/BS/P&amp;L/Stock Summary and the
    /// outstanding balances) are the heaviest requests the agent makes. They
    /// run: every cycle only if tally.snapshotEveryCycle; otherwise on Force
    /// Full Sync, when the dataset has never been extracted, or once per
    /// server-local day at/after tally.snapshotHourLocal. Per-dataset, so a
    /// snapshot that timed out is retried next cycle without re-running the
    /// ones that already succeeded today.</summary>
    private bool ShouldRunSnapshot(string dataset, string company, string mode, out string reason)
    {
        if (config.Tally.SnapshotEveryCycle) { reason = "snapshotEveryCycle"; return true; }
        if (mode == "full-forced") { reason = "force full sync"; return true; }
        var cp = checkpoints.Get(dataset, company);
        if (cp?.LastSuccessUtc is null) { reason = "never extracted"; return true; }

        var now = DateTime.Now;
        var lastLocal = DateTime.TryParse(cp.LastSuccessUtc, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var lastUtc)
            ? lastUtc.ToLocalTime() : DateTime.MinValue;
        if (lastLocal.Date == now.Date) { reason = "already extracted today"; return false; }
        if (now.Hour < Math.Clamp(config.Tally.SnapshotHourLocal, 0, 23))
        { reason = $"daily slot starts at {config.Tally.SnapshotHourLocal:00}:00"; return false; }
        reason = "daily slot";
        return true;
    }

    /// <summary>Re-split every pending window wider than <paramref name="chunkDays"/>
    /// into chunks of that size, preserving walk direction and order.</summary>
    internal static void ReChunk(Queue<(DateOnly From, DateOnly To)> pending, int chunkDays, bool newestFirst)
    {
        chunkDays = Math.Max(1, chunkDays);
        var rest = pending.ToList();
        pending.Clear();
        foreach (var (from, to) in rest)
        {
            if (to.DayNumber - from.DayNumber + 1 <= chunkDays) { pending.Enqueue((from, to)); continue; }
            var pieces = new List<(DateOnly, DateOnly)>();
            for (var f = from; f <= to; f = f.AddDays(chunkDays))
            {
                var t = f.AddDays(chunkDays - 1);
                if (t > to) t = to;
                pieces.Add((f, t));
            }
            if (newestFirst) pieces.Reverse();
            foreach (var pc in pieces) pending.Enqueue(pc);
        }
    }

    public List<(DateOnly From, DateOnly To)> PlanVoucherWindows(string company)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var cp = checkpoints.Get("_vouchers_window", company);
        return SyncPlanner.PlanVoucherWindows(config.Tally, cp, today).Windows;
    }

    public VoucherPlan PlanVouchers(string company) =>
        SyncPlanner.PlanVoucherWindows(config.Tally,
            checkpoints.Get("_vouchers_window", company),
            DateOnly.FromDateTime(DateTime.Today));

    private void ResetVoucherCheckpoint(string company)
    {
        checkpoints.Upsert(new SyncCheckpoint("_vouchers_window", company,
            null, null, null, null, FullSyncDone: false));
        log.LogWarning("Force Full Sync — voucher checkpoint reset for '{Company}'", company);
    }

    private void AdvanceVoucherWindowCheckpoint(string company, DateOnly from, DateOnly to,
        DateOnly? fullSyncTarget = null)
    {
        var existing = checkpoints.Get("_vouchers_window", company);

        if (fullSyncTarget is { } target)
        {
            // Newest-first history walk: LastFromDate is the backward frontier
            // (oldest date extracted so far), LastToDate the newest covered date.
            // The walk is complete when the frontier reaches the target start.
            var existingFrom = SyncPlanner.TryParseIsoDate(existing?.LastFromDate);
            var existingTo = SyncPlanner.TryParseIsoDate(existing?.LastToDate);
            var frontier = existingFrom is { } ef && ef < from ? ef : from;
            var top = existingTo is { } et && et > to ? et : to;
            checkpoints.Upsert(new SyncCheckpoint(
                "_vouchers_window", company,
                frontier.ToString("yyyy-MM-dd"),
                top.ToString("yyyy-MM-dd"),
                SyncPlanner.NewestFirstCheckpointMarker,
                DateTime.UtcNow.ToString("O"),
                FullSyncDone: frontier <= target));
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        var fullDone = (existing?.FullSyncDone ?? false) || to >= today;
        checkpoints.Upsert(new SyncCheckpoint(
            "_vouchers_window", company,
            existing?.LastFromDate ?? from.ToString("yyyy-MM-dd"),
            to.ToString("yyyy-MM-dd"),
            existing?.LastAlterId, DateTime.UtcNow.ToString("O"), fullDone));
    }

    // ── extraction dispatch ───────────────────────────────────────

    private async Task<List<Row>> ExtractMasterOrSnapshot(string dataset, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var fyStart = new DateOnly(today.Month >= 4 ? today.Year : today.Year - 1, 4, 1);

        return dataset switch
        {
            "companies" => await masters.Companies(ct),
            "groups" => await masters.Groups(ct),
            "ledgers" => await masters.Ledgers(ct),
            "voucher_types" => await masters.VoucherTypes(ct),
            "cost_centres" => await masters.CostCentres(ct),
            "cost_categories" => await masters.CostCategories(ct),
            "currencies" => await masters.Currencies(ct),
            "uom" => await masters.Units(ct),
            "gst_rates" => await masters.GstRates(ct),
            "opening_bills" => await masters.OpeningBills(ct),
            "stock_groups" => await masters.StockGroups(ct),
            "stock_items" => await masters.StockItems(ct),
            "godowns" => await masters.Godowns(ct),
            "stock_standard_costs" => await masters.StockStandardCosts(ct),
            "stock_standard_prices" => await masters.StockStandardPrices(ct),
            "trial_balance" => await reports.TrialBalance(fyStart, today, ct),
            "balance_sheet" => await reports.BalanceSheet(fyStart, today, ct),
            "profit_loss" => await reports.ProfitLoss(fyStart, today, ct),
            "stock_summary" => await reports.StockSummary(fyStart, today, ct),
            "outstanding_payables" => await reports.Outstanding("Sundry Creditors", today, ct),
            "outstanding_receivables" => await reports.Outstanding("Sundry Debtors", today, ct),
            _ => throw new InvalidOperationException($"Unknown dataset '{dataset}'"),
        };
    }

    private static List<string> ValidateExtractionCounts(IReadOnlyDictionary<string, int> counts)
    {
        var errors = new List<string>();
        int Count(string name) => counts.TryGetValue(name, out var n) ? n : -1;

        var ledgers = Count("ledgers");
        var groups = Count("groups");
        if (ledgers > 0 && groups == 0)
            errors.Add($"groups returned 0 rows while ledgers returned {ledgers}; group extraction is likely incomplete");

        var stockItems = Count("stock_items");
        var stockCosts = Count("stock_standard_costs");
        var stockPrices = Count("stock_standard_prices");
        if (stockItems == 0 && (stockCosts > 0 || stockPrices > 0))
            errors.Add($"stock_items returned 0 rows while standard costs/prices returned {Math.Max(stockCosts, 0)}/{Math.Max(stockPrices, 0)} rows");

        if (ledgers > 0)
        {
            foreach (var report in new[] { "trial_balance", "balance_sheet", "profit_loss" })
                if (Count(report) == 0)
                    errors.Add($"{report} returned 0 rows while ledgers returned {ledgers}; report extraction requires review");
        }

        if (stockItems > 0 && Count("stock_summary") == 0)
            errors.Add($"stock_summary returned 0 rows while stock_items returned {stockItems}");

        return errors;
    }

    /// <summary>Split one Day Book response into its datasets.
    ///
    /// <paramref name="emitLegacyVouchers"/> (v2.1.0): VoucherExtractor adds the
    /// SAME row object to both <c>Vouchers</c> and <c>DayBook</c>
    /// (<c>result.Vouchers.Add(flat); result.DayBook.Add(new Row(flat))</c>), so
    /// the two datasets are byte-identical by construction — measured at 170,073
    /// and 170,056 rows in raw. Off by default: <c>day_book</c> is the Tally
    /// report this data actually is, and <c>vouchers</c> is a second copy of it
    /// under an older name. Set <c>emitLegacyVouchersDataset: true</c> to keep
    /// producing it while anything downstream still reads it.</summary>
    private static IEnumerable<(string Name, List<Row> Rows)> FanOut(
        VoucherExtractor.DayBookResult r, bool emitLegacyVouchers)
    {
        if (emitLegacyVouchers) yield return ("vouchers", r.Vouchers);
        yield return ("voucher_headers", r.VoucherHeaders);
        yield return ("voucher_lines", r.VoucherLines);
        yield return ("bill_allocations", r.BillAllocations);
        yield return ("bank_allocations", r.BankAllocations);
        yield return ("cost_centre_allocations", r.CostCentreAllocations);
        yield return ("inventory_entries", r.InventoryEntries);
        yield return ("day_book", r.DayBook);
        yield return ("bank_book", r.BankBook);
        yield return ("sales_register", r.SalesRegister);
        yield return ("purchase_register", r.PurchaseRegister);
        yield return ("sales_invoice_lines", r.SalesInvoiceLines);
        yield return ("voucher_guid_manifest", r.Manifest);
    }

    // ── persistence helpers ───────────────────────────────────────

    private void EnqueueAndCheckpoint(string dataset, string company, string syncId,
        List<Row> rows, string? windowFrom, string? windowTo, bool fullDone)
    {
        var now = DateTime.UtcNow;
        batchBuilder.BuildAndEnqueue(dataset, company, syncId, rows, now, now,
            windowFrom, windowTo, config.Cloud.UploadBatchMaxRecords);
        checkpoints.Upsert(new SyncCheckpoint(dataset, company, windowFrom, windowTo,
            null, now.ToString("O"), fullDone));
    }

    private string ResolveCompany(IReadOnlyList<string> openCompanies)
    {
        if (!string.IsNullOrWhiteSpace(config.Tally.Company)) return config.Tally.Company;
        if (config.Tally.AutoDiscoverCompanies && openCompanies.Count > 0) return openCompanies[0];
        throw new TallyException(ErrorCategory.TallyCompanyNotOpen,
            "No Tally company configured and auto-discovery found none open.");
    }

    private void CheckDiskSpace()
    {
        var drive = new DriveInfo(Path.GetPathRoot(AgentInfo.DataDir)!);
        var freeMb = drive.AvailableFreeSpace / (1024 * 1024);
        if (freeMb < config.Advanced.MinFreeDiskMb)
            throw new TallyException(ErrorCategory.DiskSpaceLow,
                $"Only {freeMb} MB free on {drive.Name}; minimum is {config.Advanced.MinFreeDiskMb} MB. " +
                "Extraction paused to protect the machine.");

        var stats = queue.GetStats();
        if (stats.TotalQueueBytes > (long)config.Advanced.QueueDiskLimitMb * 1024 * 1024)
            throw new TallyException(ErrorCategory.DiskSpaceLow,
                $"Local queue is {stats.TotalQueueBytes / (1024 * 1024)} MB " +
                $"(limit {config.Advanced.QueueDiskLimitMb} MB). Uploads must drain before extracting more.");
    }

    private async Task HandleDatasetErrorAsync(string dataset, Exception ex, List<string> errorList)
    {
        var category = ex is TallyException tex ? tex.Category : ErrorCategory.UnexpectedException;
        var severity = category == ErrorCategory.DiskSpaceLow ? ErrorSeverity.Critical : ErrorSeverity.Error;
        await reporter.ReportAsync(category, severity, ex.Message, ex.StackTrace,
            operation: CurrentOperation, dataset: dataset, ct: CancellationToken.None);
        errorList.Add($"{dataset}: {ex.Message}");
        log.LogError(ex, "Dataset {Dataset} failed", dataset);
    }

    /// <summary>Durable per-window coverage evidence (§G12): requested window,
    /// actual min/max voucher dates seen, record count, run id and status.
    /// Historical completeness is judged from THIS table — never from
    /// aggregate record counts (§G13).</summary>
    private void RecordWindowCoverage(string syncId, string dataset, DateOnly from, DateOnly to,
        int records, string? minDate, string? maxDate, string status)
    {
        try
        {
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO window_coverage
                  (run_id, dataset, window_from, window_to, records, min_date, max_date,
                   status, completed_utc)
                VALUES ($r,$d,$wf,$wt,$n,$min,$max,$s,$ts)
                """;
            cmd.Parameters.AddWithValue("$r", syncId);
            cmd.Parameters.AddWithValue("$d", dataset);
            cmd.Parameters.AddWithValue("$wf", from.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$wt", to.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$n", records);
            cmd.Parameters.AddWithValue("$min", (object?)minDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$max", (object?)maxDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$s", status);
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            log.LogWarning("Window coverage not recorded ({Msg})", ex.Message);
        }
    }

    /// <summary>A crashed process leaves sync_runs.status='running' forever.
    /// Called at cycle start (under the sync lease) so stale rows become
    /// 'abandoned' — visible, but never mistaken for an active run.</summary>
    private void MarkAbandonedRuns()
    {
        try
        {
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE sync_runs SET status='abandoned',
                       error_message=COALESCE(error_message,'process terminated mid-run')
                WHERE status='running'
                """;
            var n = cmd.ExecuteNonQuery();
            if (n > 0) log.LogWarning("Marked {N} stale 'running' sync run(s) as abandoned", n);
        }
        catch (Exception ex)
        {
            log.LogWarning("Stale-run cleanup failed ({Msg})", ex.Message);
        }
    }

    private void RecordRunStart(string syncId, string mode)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sync_runs (sync_id, mode, started_utc, status)
            VALUES ($id,$m,$ts,'running')
            """;
        cmd.Parameters.AddWithValue("$id", syncId);
        cmd.Parameters.AddWithValue("$m", mode);
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private void RecordRunFinish(string syncId, string status, long rows, string? error)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE sync_runs SET finished_utc=$ts, status=$st, rows_total=$rows, error_message=$err
            WHERE sync_id=$id
            """;
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$st", status);
        cmd.Parameters.AddWithValue("$rows", rows);
        cmd.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", syncId);
        cmd.ExecuteNonQuery();
    }
}
