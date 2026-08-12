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

            var probe = await tally.ProbeAsync(ct);
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
                RecordRunFinish(syncId, "failed", 0, probe.Error);
                return new SyncResult(syncId, "failed", 0, 0, 0, [probe.Error ?? "Tally unavailable"]);
            }

            var company = ResolveCompany(probe.Companies);
            var enabled = DatasetRegistry.Enabled(config.Tally);
            log.LogInformation("Sync {SyncId} ({Mode}) starting: company='{Company}', {N} datasets",
                syncId, mode, company, enabled.Count);

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
                    extractedCounts[ds.Name] = rows.Count;
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
            if (enabled.Any(d => d.Kind == DatasetKind.Voucher))
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
                    try { bankLedgers = await reports.BankLedgerNames(ct); }
                    catch { bankLedgers = []; }

                    // Adaptive windowing: a window that times out is split in half
                    // and retried down to single-day windows.
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
                                    fullDone: false);
                            }
                            AdvanceVoucherWindowCheckpoint(company, from, to, plan.TargetStart);
                            ok++;
                        }
                        catch (TallyException tex) when (
                            tex.Category == ErrorCategory.TallyTimeout && to > from)
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
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            failed++;
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
            "outstanding_payables" => await reports.Outstanding("Sundry Creditors", ct),
            "outstanding_receivables" => await reports.Outstanding("Sundry Debtors", ct),
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
