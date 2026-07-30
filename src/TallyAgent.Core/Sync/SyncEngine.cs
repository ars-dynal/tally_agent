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
    ErrorLogRepository errors,
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
                errors.Insert(category, ErrorSeverity.Error, probe.Error ?? "Tally probe failed",
                    operation: "preflight");
                RecordRunFinish(syncId, "failed", 0, probe.Error);
                return new SyncResult(syncId, "failed", 0, 0, 0, [probe.Error ?? "Tally unavailable"]);
            }

            var company = ResolveCompany(probe.Companies);
            var enabled = DatasetRegistry.Enabled(config.Tally);
            log.LogInformation("Sync {SyncId} ({Mode}) starting: company='{Company}', {N} datasets",
                syncId, mode, company, enabled.Count);

            // ── Masters & snapshots (cheap full re-extract each cycle) ──
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
                    HandleDatasetError(ds.Name, ex, ref errorList);
                }
            }

            // ── Vouchers (windowed Day Book fan-out) ───────────────
            if (enabled.Any(d => d.Kind == DatasetKind.Voucher))
            {
                var windows = PlanVoucherWindows(company);
                if (windows.Count > 0)
                {
                    HashSet<string> bankLedgers;
                    try { bankLedgers = await reports.BankLedgerNames(ct); }
                    catch { bankLedgers = []; }

                    foreach (var (from, to) in windows)
                    {
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
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            failed++;
                            HandleDatasetError($"vouchers[{from:yyyy-MM-dd}..{to:yyyy-MM-dd}]", ex, ref errorList);
                            break; // windows are sequential; resume from checkpoint next cycle
                        }
                    }
                }
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
            errors.Insert(ErrorCategory.UnexpectedException, ErrorSeverity.Critical,
                ex.Message, ex.StackTrace, operation: CurrentOperation);
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
        var windows = new List<(DateOnly, DateOnly)>();

        if (cp is not { FullSyncDone: true })
        {
            // Initial full sync (possibly resuming)
            var start = ResolveExtractionStart(cp);
            var chunk = Math.Max(1, config.Tally.FullSyncChunkDays);
            for (var from = start; from <= today; from = from.AddDays(chunk))
            {
                var to = from.AddDays(chunk - 1);
                if (to > today) to = today;
                windows.Add((from, to));
            }
        }
        else
        {
            var lookback = Math.Max(0, config.Tally.IncrementalLookbackDays);
            windows.Add((today.AddDays(-lookback), today));
        }
        return windows;
    }

    private DateOnly ResolveExtractionStart(SyncCheckpoint? cp)
    {
        // Resume after crash: continue from day after last completed window
        if (cp?.LastToDate is { } lastTo && DateOnly.TryParse(lastTo, out var resume))
            return resume.AddDays(1);
        if (DateOnly.TryParse(config.Tally.ExtractionStartDate, out var configured))
            return configured;
        // Default: start of current financial year (April 1, India)
        var today = DateOnly.FromDateTime(DateTime.Today);
        var fyYear = today.Month >= 4 ? today.Year : today.Year - 1;
        return new DateOnly(fyYear, 4, 1);
    }

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

    private void HandleDatasetError(string dataset, Exception ex, ref List<string> errorList)
    {
        var category = ex is TallyException tex ? tex.Category : ErrorCategory.UnexpectedException;
        var severity = category == ErrorCategory.DiskSpaceLow ? ErrorSeverity.Critical : ErrorSeverity.Error;
        errors.Insert(category, severity, ex.Message, ex.StackTrace,
            operation: CurrentOperation, dataset: dataset);
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
