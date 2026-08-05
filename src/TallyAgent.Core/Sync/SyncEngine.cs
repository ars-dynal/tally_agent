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
    public string CurrentOperation { get; private set; } = "idle";

    public async Task<SyncResult> RunCycleAsync(string mode, CancellationToken ct)
    {
        var syncId = Guid.NewGuid().ToString("N")[..12];
        var started = DateTime.UtcNow;
        RecordRunStart(syncId, mode);
        var errorList = new List<string>();
        int ok = 0, failed = 0;
        long totalRows = 0;

        try
        {
            // ── Preflight ──────────────────────────────────────────
            CurrentOperation = "preflight";
            CheckDiskSpace();

            var probe = await tally.ProbeAsync(ct);
            if (!probe.Ok)
            {
                var category = probe.Category ?? ErrorCategory.TallyNotRunning;
                // Routed through ErrorReporter (C6): repeated Tally-down cycles are
                // grouped into digests; the reporter also owns critical dispatch.
                await reporter.ReportAsync(category, ErrorSeverity.Error,
                    probe.Error ?? "Tally probe failed",
                    operation: "preflight", ct: CancellationToken.None);
                RecordRunFinish(syncId, "failed", 0, probe.Error);
                return new SyncResult(syncId, "failed", 0, 0, 0, [probe.Error ?? "Tally unavailable"]);
            }

            var company = ResolveCompany(probe.Companies);
            var enabled = DatasetRegistry.Enabled(config.Tally);
            log.LogInformation("Sync {SyncId} ({Mode}) starting: company='{Company}', {N} datasets",
                syncId, mode, company, enabled.Count);

            // Force Full Sync: reset the voucher checkpoint so the planner
            // re-walks the entire history from extractionStartDate.
            if (mode == "full-forced")
            {
                checkpoints.Upsert(new SyncCheckpoint("_vouchers_window", company,
                    null, null, null, null, FullSyncDone: false));
                log.LogWarning("Force Full Sync requested — voucher checkpoint reset for '{Company}'", company);
            }

            // ── AlterID change gate (best-effort; null ⇒ always extract) ──
            // ALTMSTID/ALTVCHID are company-wide watermarks bumped on any master/
            // voucher create-edit-delete. When unchanged since the last successful
            // cycle, the corresponding phase is skipped entirely — this is the
            // single biggest idle-load reduction for quiet companies. Gating only
            // applies to steady-state incremental cycles.
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

            // ── Masters & snapshots (cheap full re-extract each cycle) ──
            if (mastersUnchanged && vouchersUnchanged)
                log.LogInformation("AlterID gate: no master or voucher changes — skipping masters/snapshots");
            else
            foreach (var ds in enabled.Where(d => d.Kind is DatasetKind.Master or DatasetKind.Snapshot))
            {
                ct.ThrowIfCancellationRequested();
                CurrentOperation = $"extract:{ds.Name}";
                try
                {
                    var rows = await ExtractMasterOrSnapshot(ds.Name, ct);
                    totalRows += rows.Count;
                    EnqueueAndCheckpoint(ds.Name, company, syncId, rows, null, null, fullDone: true);
                    ok++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    await HandleDatasetErrorAsync(ds.Name, ex, errorList);
                }
            }

            // ── Vouchers (windowed Day Book fan-out) ───────────────
            if (enabled.Any(d => d.Kind == DatasetKind.Voucher))
            {
                var plan = PlanVouchers(company);
                if (plan.RecoveredGapDays > 0)
                {
                    // An outage longer than the lookback happened; the planner is
                    // re-extracting the missed days. Surface it — silent gaps were
                    // the old (data-losing) behaviour.
                    log.LogWarning(
                        "Recovering {Days}-day extraction gap beyond the {Lookback}-day lookback " +
                        "(agent or Tally was unavailable). Missed days are being re-extracted.",
                        plan.RecoveredGapDays, config.Tally.IncrementalLookbackDays);
                    await reporter.ReportAsync(ErrorCategory.ServiceStopped, ErrorSeverity.Warning,
                        $"Extraction gap of {plan.RecoveredGapDays} day(s) beyond the lookback window " +
                        "detected and recovered — verify the agent/Tally uptime.",
                        operation: "plan:vouchers", ct: CancellationToken.None);
                }
                var skipVouchers = vouchersUnchanged && !plan.IsFullSync && plan.RecoveredGapDays == 0;
                if (skipVouchers)
                    log.LogInformation("AlterID gate: no voucher changes — skipping voucher extraction");

                if (!skipVouchers && plan.Windows.Count > 0)
                {
                    HashSet<string> bankLedgers;
                    try { bankLedgers = await reports.BankLedgerNames(ct); }
                    catch { bankLedgers = []; }

                    // Adaptive windowing: a window that times out is split in half
                    // and retried (down to single days) instead of being retried
                    // at the same size forever — large companies/heavy months no
                    // longer livelock the sync.
                    var pending = new Queue<(DateOnly From, DateOnly To)>(plan.Windows);
                    while (pending.Count > 0)
                    {
                        var (from, to) = pending.Dequeue();
                        ct.ThrowIfCancellationRequested();
                        CurrentOperation = $"extract:vouchers {from:yyyy-MM-dd}..{to:yyyy-MM-dd}";
                        try
                        {
                            var result = await vouchers.ExtractWindow(from, to, bankLedgers, ct);
                            var wf = from.ToString("yyyy-MM-dd");
                            var wt = to.ToString("yyyy-MM-dd");

                            foreach (var (name, rows) in FanOut(result))
                            {
                                if (!enabled.Any(d => d.Name == name)) continue;
                                totalRows += rows.Count;
                                EnqueueAndCheckpoint(name, company, syncId, rows, wf, wt,
                                    fullDone: false); // full flag advanced after ALL windows
                            }
                            AdvanceVoucherWindowCheckpoint(company, from, to);
                            ok++;
                        }
                        catch (TallyException tex) when (
                            tex.Category == ErrorCategory.TallyTimeout && to > from)
                        {
                            var mid = from.AddDays((to.DayNumber - from.DayNumber) / 2);
                            log.LogWarning(
                                "Window {From}..{To} timed out — splitting into {From}..{Mid} and {MidNext}..{To}",
                                from, to, mid, mid.AddDays(1));
                            var rest = pending.ToList();
                            pending.Clear();
                            pending.Enqueue((from, mid));
                            pending.Enqueue((mid.AddDays(1), to));
                            foreach (var w in rest) pending.Enqueue(w);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            failed++;
                            await HandleDatasetErrorAsync($"vouchers[{from:yyyy-MM-dd}..{to:yyyy-MM-dd}]", ex, errorList);
                            break; // windows are sequential; resume from checkpoint next cycle
                        }
                    }
                }
            }

            // Advance the AlterID gate watermarks only after a fully successful cycle
            // (any failure leaves them unchanged so the next cycle re-extracts).
            if (alterIds is { } finalIds && failed == 0)
            {
                checkpoints.Upsert(new SyncCheckpoint("_alter_gate_masters", company,
                    null, null, finalIds.Masters, DateTime.UtcNow.ToString("O"), true));
                checkpoints.Upsert(new SyncCheckpoint("_alter_gate_vouchers", company,
                    null, null, finalIds.Vouchers, DateTime.UtcNow.ToString("O"), true));
            }

            var status = failed == 0 ? "success" : ok > 0 ? "partial" : "failed";
            RecordRunFinish(syncId, status, totalRows,
                errorList.Count > 0 ? string.Join("; ", errorList) : null);
            log.LogInformation("Sync {SyncId} {Status}: {Rows} rows, {Ok} ok, {Failed} failed ({Elapsed:F0}s)",
                syncId, status, totalRows, ok, failed, (DateTime.UtcNow - started).TotalSeconds);
            return new SyncResult(syncId, status, ok, failed, totalRows, errorList);
        }
        catch (OperationCanceledException)
        {
            RecordRunFinish(syncId, "cancelled", totalRows, "service stopping");
            throw;
        }
        catch (Exception ex)
        {
            await reporter.ReportAsync(ErrorCategory.UnexpectedException, ErrorSeverity.Critical,
                ex.Message, ex.StackTrace, operation: CurrentOperation, ct: CancellationToken.None);
            RecordRunFinish(syncId, "failed", totalRows, ex.Message);
            return new SyncResult(syncId, "failed", ok, failed + 1, totalRows, [ex.Message]);
        }
        finally
        {
            CurrentOperation = "idle";
        }
    }

    // ── planning ──────────────────────────────────────────────────

    /// <summary>Voucher date windows for this cycle:
    ///  • full-history chunks (≤ FullSyncChunkDays) resuming from checkpoint on first run
    ///  • single lookback window (default 7 days) once full sync completed.</summary>
    public List<(DateOnly From, DateOnly To)> PlanVoucherWindows(string company)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var cp = checkpoints.Get("_vouchers_window", company);
        return SyncPlanner.PlanVoucherWindows(config.Tally, cp, today).Windows;
    }

    /// <summary>Checkpoint-aware voucher planning (SyncPlanner is the pure core).</summary>
    public VoucherPlan PlanVouchers(string company) =>
        SyncPlanner.PlanVoucherWindows(config.Tally,
            checkpoints.Get("_vouchers_window", company),
            DateOnly.FromDateTime(DateTime.Today));

    private void AdvanceVoucherWindowCheckpoint(string company, DateOnly from, DateOnly to)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var existing = checkpoints.Get("_vouchers_window", company);
        var fullDone = (existing?.FullSyncDone ?? false) || to >= today;
        checkpoints.Upsert(new SyncCheckpoint(
            "_vouchers_window", company,
            existing?.LastFromDate ?? from.ToString("yyyy-MM-dd"),
            to.ToString("yyyy-MM-dd"),
            null, DateTime.UtcNow.ToString("O"), fullDone));
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
            "outstanding_payables" => await reports.Outstanding("Sundry Creditors", ct),
            "outstanding_receivables" => await reports.Outstanding("Sundry Debtors", ct),
            _ => throw new InvalidOperationException($"Unknown dataset '{dataset}'"),
        };
    }

    private static IEnumerable<(string Name, List<Row> Rows)> FanOut(VoucherExtractor.DayBookResult r)
    {
        yield return ("vouchers", r.Vouchers);
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
        // ErrorReporter logs locally AND dispatches criticals immediately (with
        // per-group cooldown); non-criticals join the periodic digest. Previously
        // these rows went straight to error_log and criticals were never alerted.
        await reporter.ReportAsync(category, severity, ex.Message, ex.StackTrace,
            operation: CurrentOperation, dataset: dataset, ct: CancellationToken.None);
        errorList.Add($"{dataset}: {ex.Message}");
        log.LogError(ex, "Dataset {Dataset} failed", dataset);
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
