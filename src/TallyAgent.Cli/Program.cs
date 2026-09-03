using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TallyAgent.Core;
using TallyAgent.Core.Cloud;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Data;
using TallyAgent.Core.Diagnostics;
using TallyAgent.Core.Security;
using TallyAgent.Core.Tally;
using TallyAgent.Core.Tally.Extractors;

// ─────────────────────────────────────────────────────────────────
// TallyAgent.Cli — admin & installer verbs.
// Exit codes: 0 = success, 1 = failure, 2 = bad usage.
// Machine-readable JSON on stdout with --json (used by the installer).
// ─────────────────────────────────────────────────────────────────

var argv = args.ToList();
var json = argv.Remove("--json");

try
{
    return argv.FirstOrDefault()?.ToLowerInvariant() switch
    {
        "test-tally" => await TestTally(argv.Skip(1).ToList(), json),
        "test-cloud" => await TestCloud(argv.Skip(1).ToList(), json),
        "save-config" => SaveConfig(argv.Skip(1).ToList(), json),
        "show-config" => ShowConfig(json),
        "sync-now" => WriteTrigger("sync-now"),
        "force-full-sync" => WriteTrigger("force-full"),
        "capture-xml" => await CaptureXml(argv.Skip(1).ToList()),
        "verify-bills" => await VerifyBills(argv.Skip(1).ToList(), json),
        "diagnose-opening-bills" => await DiagnoseOpeningBills(argv.Skip(1).ToList(), json),
        "retry-failed" => RetryFailed(json),
        "export-diag" => ExportDiag(json),
        "status" => Status(json),
        "protect" => Protect(argv.Skip(1).ToList()),
        _ => Usage(),
    };
}
catch (Exception ex)
{
    Emit(json, new { ok = false, error = SecretMasker.Scrub(ex.Message) },
        $"ERROR: {SecretMasker.Scrub(ex.Message)}");
    return 1;
}

// ── verbs ─────────────────────────────────────────────────────────

static async Task<int> TestTally(List<string> a, bool json)
{
    // usage: test-tally [--host H] [--port P] [--company C]  (defaults from saved config if present)
    var settings = TryLoadConfig()?.Tally ?? new TallySettings();
    for (var i = 0; i < a.Count - 1; i++)
    {
        switch (a[i])
        {
            case "--host": settings.Host = a[++i]; break;
            case "--port": settings.Port = int.Parse(a[++i]); break;
            case "--company": settings.Company = a[++i]; break;
        }
    }

    var client = new TallyClient(settings, NullLogger<TallyClient>.Instance);
    var probe = await client.ProbeAsync();

    Emit(json, new { ok = probe.Ok, companies = probe.Companies, error = probe.Error },
        probe.Ok
            ? $"OK — Tally reachable on {settings.Host}:{settings.Port}. Open companies: {string.Join(", ", probe.Companies)}"
            : $"FAILED — {probe.Error}");
    return probe.Ok ? 0 : 1;
}

static async Task<int> TestCloud(List<string> a, bool json)
{
    // usage: test-cloud [--url U] [--token T] [--agent-id A] [--environment E]
    var cfg = TryLoadConfig() ?? new AgentConfig();
    for (var i = 0; i < a.Count - 1; i++)
    {
        switch (a[i])
        {
            case "--url": cfg.Cloud.IngestionApiUrl = a[++i]; break;
            case "--token": cfg.Cloud.ApiToken = a[++i]; break;
            case "--agent-id": cfg.Cloud.AgentId = a[++i]; break;
            case "--company-id": cfg.Cloud.CompanyId = a[++i]; break;
            case "--environment": cfg.Cloud.Environment = a[++i]; break;
        }
    }
    if (string.IsNullOrWhiteSpace(cfg.Cloud.IngestionApiUrl))
    {
        Emit(json, new { ok = false, error = "No ingestion API URL supplied" },
            "FAILED — no ingestion API URL supplied");
        return 1;
    }

    try
    {
        var api = new IngestionApiClient(cfg, NullLogger<IngestionApiClient>.Instance);
        var ping = await api.PingAsync();
        Emit(json, new { ok = ping.Ok, server_time = ping.ServerTime },
            ping.Ok ? $"OK — ingestion API reachable (server time {ping.ServerTime})"
                    : "FAILED — API responded but ok=false");
        return ping.Ok ? 0 : 1;
    }
    catch (CloudApiException ex)
    {
        Emit(json, new { ok = false, category = ex.Category.ToString(), error = ex.Message },
            $"FAILED — {ex.Category}: {ex.Message}");
        return 1;
    }
}

static int SaveConfig(List<string> a, bool json)
{
    // usage: save-config --file <path-to-plaintext-json>   (installer writes a temp
    //        file, we validate + encrypt + persist + delete the temp file)
    //   or:  save-config --set tally.host=127.0.0.1 --set cloud.agentId=X ...
    var store = new ConfigStore();
    AgentConfig cfg;

    var fileIdx = a.IndexOf("--file");
    if (fileIdx >= 0 && fileIdx + 1 < a.Count)
    {
        var path = a[fileIdx + 1];
        cfg = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path))
              ?? throw new InvalidDataException("Config file is empty/invalid.");
        store.Save(cfg);
        try { File.Delete(path); } catch { /* best effort: temp plaintext removed */ }
    }
    else
    {
        cfg = store.LoadOrDefault();
        for (var i = 0; i < a.Count - 1; i++)
        {
            if (a[i] != "--set") continue;
            var kv = a[++i].Split('=', 2);
            if (kv.Length != 2) throw new ArgumentException($"Bad --set '{a[i]}' (want key=value)");
            ApplySet(cfg, kv[0], kv[1]);
        }
        store.Save(cfg);
    }

    Emit(json, new { ok = true, path = AgentInfo.ConfigPath },
        $"Configuration saved (secrets DPAPI-encrypted) at {AgentInfo.ConfigPath}");
    return 0;
}

static int ShowConfig(bool json)
{
    var cfg = new ConfigStore().Load();
    cfg.Cloud.ApiToken = SecretMasker.MaskSecret(ConfigStore.GetApiToken(cfg));
    cfg.Notifications.ErrorWebhookUrl = string.IsNullOrEmpty(ConfigStore.GetErrorWebhook(cfg)) ? "" : "(set)";
    cfg.Notifications.GoogleChatWebhookUrl = string.IsNullOrEmpty(ConfigStore.GetGoogleChatWebhook(cfg)) ? "" : "(set)";
    cfg.Notifications.SlackWebhookUrl = string.IsNullOrEmpty(ConfigStore.GetSlackWebhook(cfg)) ? "" : "(set)";
    Console.WriteLine(JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

static int WriteTrigger(string name)
{
    AgentInfo.EnsureDirectories();
    File.WriteAllText(Path.Combine(AgentInfo.TriggerDir, $"{name}.trigger"),
        DateTime.UtcNow.ToString("O"));
    Console.WriteLine($"Trigger '{name}' written — the service will act within a few seconds.");
    return 0;
}

/// <summary>capture-xml — dump raw (sanitized) Tally responses as validation
/// fixtures for the ARCHITECTURE §8.4 extraction-validation gate.
/// usage: capture-xml --kind vouchers --from 2026-07-01 --to 2026-07-31
///        capture-xml --kind masters --collection Ledger</summary>
static async Task<int> CaptureXml(List<string> a)
{
    var cfg = new ConfigStore().Load();
    var client = new TallyClient(cfg.Tally, NullLogger<TallyClient>.Instance);

    string kind = "vouchers", collection = "Ledger";
    DateOnly from = DateOnly.FromDateTime(DateTime.Today).AddDays(-7);
    DateOnly to = DateOnly.FromDateTime(DateTime.Today);
    for (var i = 0; i < a.Count - 1; i++)
    {
        switch (a[i])
        {
            case "--kind": kind = a[++i]; break;
            case "--collection": collection = a[++i]; break;
            case "--from": from = DateOnly.Parse(a[++i]); break;
            case "--to": to = DateOnly.Parse(a[++i]); break;
        }
    }

    var envelope = kind switch
    {
        "vouchers" => TallyEnvelopes.VoucherCollection(from, to, cfg.Tally.Company),
        "masters" => TallyEnvelopes.Collection(collection,
            ["GUID", "MASTERID", "ALTERID", "NAME", "PARENT"], cfg.Tally.Company),
        "alterids" => TallyEnvelopes.CompanyAlterIds(cfg.Tally.Company),
        _ => throw new ArgumentException($"Unknown --kind '{kind}' (vouchers|masters|alterids)"),
    };

    var xml = await client.PostRawAsync(envelope);
    var dir = Path.Combine(AgentInfo.DataDir, "fixtures");
    Directory.CreateDirectory(dir);
    var file = Path.Combine(dir,
        $"{kind}-{(kind == "masters" ? collection + "-" : "")}{DateTime.Now:yyyyMMdd-HHmmss}.xml");
    File.WriteAllText(file, xml);
    Console.WriteLine($"Captured {xml.Length:N0} chars of sanitized Tally XML to:\n  {file}");
    return 0;
}

/// <summary>verify-bills — run the two NEW bill-level datasets against a live
/// Tally and report exactly what came back, so the extraction envelope can be
/// proven right (or wrong) before anything is shipped or loaded.
///
/// usage: verify-bills [--from 2026-04-01] [--to 2026-09-03]
///                     [--expect-rows-payable N] [--expect-total-payable X]
///                     [--expect-rows-receivable N] [--expect-total-receivable X]
///                     [--dump] [--json]
///
/// Exit code 1 when an --expect value is supplied and does not match, so this
/// can gate a release.</summary>
static async Task<int> VerifyBills(List<string> a, bool json)
{
    var cfg = new ConfigStore().Load();
    var today = DateOnly.FromDateTime(DateTime.Today);
    var from = new DateOnly(today.Month >= 4 ? today.Year : today.Year - 1, 4, 1);
    var to = today;
    var dump = a.Remove("--dump");
    var expect = new Dictionary<string, (int? Rows, double? Total)>
    {
        ["bills_payable"] = (null, null),
        ["bills_receivable"] = (null, null),
    };
    for (var i = 0; i < a.Count - 1; i++)
    {
        switch (a[i])
        {
            case "--from": from = DateOnly.Parse(a[++i]); break;
            case "--to": to = DateOnly.Parse(a[++i]); break;
            case "--expect-rows-payable":
                expect["bills_payable"] = (int.Parse(a[++i]), expect["bills_payable"].Total); break;
            case "--expect-total-payable":
                expect["bills_payable"] = (expect["bills_payable"].Rows, ParseAmount(a[++i])); break;
            case "--expect-rows-receivable":
                expect["bills_receivable"] = (int.Parse(a[++i]), expect["bills_receivable"].Total); break;
            case "--expect-total-receivable":
                expect["bills_receivable"] = (expect["bills_receivable"].Rows, ParseAmount(a[++i])); break;
        }
    }

    using var client = new TallyClient(cfg.Tally, NullLogger<TallyClient>.Instance);
    if (await FailFastIfTallyUnreachable(client, cfg, json) is { } unreachable) return unreachable;

    var results = new List<object>();
    var ok = true;

    foreach (var (dataset, report) in new[]
             { ("bills_payable", "Bills Payable"), ("bills_receivable", "Bills Receivable") })
    {
        // ONE request per report: the row parse and the diagnostic histogram are
        // both taken from the same response, because these reports are not cheap
        // and Tally serves them on the thread operators are using.
        var doc = await client.PostAsync(
            TallyEnvelopes.BillsReport(report, from, to, cfg.Tally.Company),
            client.SnapshotRequestTimeout, maxTimeoutRetries: 0, CancellationToken.None);

        var rows = ReportExtractor.ParseBillsReport(doc, to);
        var total = rows.Sum(r => Convert.ToDouble(r["pending_amount"] ?? 0d));
        var (expRows, expTotal) = expect[dataset];
        var rowsOk = expRows is null || expRows == rows.Count;
        // Money compared to the paisa, not by eye.
        var totalOk = expTotal is null || Math.Abs(expTotal.Value - total) < 0.005;
        if (!rowsOk || !totalOk) ok = false;

        if (dump)
        {
            var dir = Path.Combine(AgentInfo.DataDir, "fixtures");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"{dataset}-{DateTime.Now:yyyyMMdd-HHmmss}.xml");
            File.WriteAllText(file, doc.ToString());
            Console.WriteLine($"  raw response saved: {file}");
        }

        results.Add(new
        {
            dataset,
            report,
            rows = rows.Count,
            total = Math.Round(total, 2),
            source = rows.Count > 0 ? rows[0]["source"] : null,
            with_overdue_days = rows.Count(r => r["overdue_days"] is not null),
            with_due_date = rows.Count(r => r["due_date"] is not null),
            distinct_parties = rows.Select(r => (string?)r["party_name"] ?? "").Distinct().Count(),
            expected_rows = expRows,
            expected_total = expTotal,
            rows_match = rowsOk,
            total_match = totalOk,
            // When nothing parsed, the element names in the response are the
            // whole diagnosis — the report layout differs from what was expected.
            element_histogram = rows.Count > 0 ? null : ElementHistogram(doc),
            sample = rows.Take(3).ToList(),
        });
    }

    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { ok, from = from.ToString("yyyy-MM-dd"),
            to = to.ToString("yyyy-MM-dd"), datasets = results },
            new JsonSerializerOptions { WriteIndented = true }));
    }
    else
    {
        Console.WriteLine($"Bills verification for {from:yyyy-MM-dd} .. {to:yyyy-MM-dd}");
        Console.WriteLine($"Company: {cfg.Tally.Company}\n");
        Console.WriteLine(JsonSerializer.Serialize(results,
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(ok
            ? "\nOK — every supplied expectation matched."
            : "\nMISMATCH — the extraction envelope or the parser needs fixing. " +
              "Re-run with --dump and send the saved XML.");
    }
    return ok ? 0 : 1;
}

/// <summary>
/// Probe Tally once before a diagnostic starts, and give up immediately if it
/// does not answer. Without this the first real request enters the normal
/// auto-reconnect loop and a diagnostic run against the wrong network sits there
/// for reconnectMaxMinutes (default 30) saying nothing. That loop is right for
/// the service, which must survive Tally restarting; it is wrong for a command a
/// person is watching. Returns null when Tally answered.
/// </summary>
static async Task<int?> FailFastIfTallyUnreachable(TallyClient client, AgentConfig cfg, bool json)
{
    var probe = await client.ProbeAsync();
    if (probe.Ok) return null;
    Emit(json,
        new { ok = false, error = probe.Error, host = cfg.Tally.Host, port = cfg.Tally.Port },
        $"FAILED — Tally did not answer on {cfg.Tally.Host}:{cfg.Tally.Port}: {probe.Error}\n" +
        "Run this from a machine that can reach the Tally server, with Tally open " +
        "and the company loaded.");
    return 1;
}

/// <summary>Accepts 26351475.28 and Indian-grouped 2,63,51,475.28 alike.</summary>
static double ParseAmount(string s) =>
    double.Parse(s.Replace(",", "").Trim(), System.Globalization.CultureInfo.InvariantCulture);

static Dictionary<string, int> ElementHistogram(System.Xml.Linq.XDocument doc) =>
    doc.Descendants()
       .GroupBy(e => e.Name.LocalName)
       .OrderByDescending(g => g.Count())
       .Take(25)
       .ToDictionary(g => g.Key, g => g.Count());

/// <summary>diagnose-opening-bills — answer, from live Tally, WHY opening_bills
/// returns zero rows. Sends the Ledger collection twice: once with the field
/// list v2.1.0 used ("BILLALLOCATIONS.LIST", a serialisation name rather than a
/// fetchable member) and once with the dotted sub-field list, and reports how
/// many bill elements each actually returns alongside how many ledgers have
/// bill-wise tracking switched on.
///
/// usage: diagnose-opening-bills [--dump] [--json]</summary>
static async Task<int> DiagnoseOpeningBills(List<string> a, bool json)
{
    var cfg = new ConfigStore().Load();
    var dump = a.Remove("--dump");
    using var client = new TallyClient(cfg.Tally, NullLogger<TallyClient>.Instance);
    if (await FailFastIfTallyUnreachable(client, cfg, json) is { } unreachable) return unreachable;

    string[] baseFields = ["GUID", "NAME", "PARENT", "ISBILLWISEON"];
    var variants = new (string Name, string[] Fields)[]
    {
        ("v2.1.0 (BILLALLOCATIONS.LIST only)", [.. baseFields, "BILLALLOCATIONS.LIST"]),
        ("v2.2.0 (dotted sub-fields)", [.. baseFields,
            "BILLALLOCATIONS.NAME", "BILLALLOCATIONS.BILLDATE", "BILLALLOCATIONS.BILLCREDITPERIOD",
            "BILLALLOCATIONS.OPENINGBALANCE", "BILLALLOCATIONS.CLOSINGBALANCE",
            "BILLALLOCATIONS.BILLTYPE", "BILLALLOCATIONS.ISADVANCE"]),
        ("both (what v2.2.0 actually sends)", [.. baseFields,
            "BILLALLOCATIONS.NAME", "BILLALLOCATIONS.BILLDATE", "BILLALLOCATIONS.BILLCREDITPERIOD",
            "BILLALLOCATIONS.OPENINGBALANCE", "BILLALLOCATIONS.CLOSINGBALANCE",
            "BILLALLOCATIONS.BILLTYPE", "BILLALLOCATIONS.ISADVANCE", "BILLALLOCATIONS.LIST"]),
    };

    var findings = new List<object>();
    foreach (var (name, fields) in variants)
    {
        var doc = await client.PostAsync(
            TallyEnvelopes.Collection("Ledger", fields, cfg.Tally.Company),
            client.SnapshotRequestTimeout, maxTimeoutRetries: 0, CancellationToken.None);

        var ledgers = doc.Descendants("LEDGER").ToList();
        var billElements = ledgers.Sum(l => MasterExtractor.BillAllocationElements(l).Count());
        var named = ledgers.Sum(l => MasterExtractor.BillAllocationElements(l)
            .Count(b => TallyXml.Text(b, "NAME").Length > 0));

        if (dump)
        {
            var dir = Path.Combine(AgentInfo.DataDir, "fixtures");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir,
                $"opening-bills-{variants.ToList().FindIndex(v => v.Name == name)}-{DateTime.Now:yyyyMMdd-HHmmss}.xml");
            File.WriteAllText(file, doc.ToString());
            Console.WriteLine($"  raw response saved: {file}");
        }

        findings.Add(new
        {
            variant = name,
            ledgers = ledgers.Count,
            ledgers_with_billwise_on = ledgers.Count(l => TallyXml.Bool(l, "ISBILLWISEON")),
            bill_elements = billElements,
            // What OpeningBills would actually emit: a bill needs a reference.
            rows_opening_bills_would_emit = named,
        });
    }

    var payload = new { ok = true, company = cfg.Tally.Company, variants = findings };
    if (json) Console.WriteLine(JsonSerializer.Serialize(payload,
        new JsonSerializerOptions { WriteIndented = true }));
    else
    {
        Console.WriteLine($"opening_bills diagnosis for '{cfg.Tally.Company}'\n");
        Console.WriteLine(JsonSerializer.Serialize(findings,
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(
            "\nHow to read this:\n" +
            "  • ledgers_with_billwise_on = 0  ⇒ bill-wise tracking is off in Tally; not a code bug.\n" +
            "  • v2.1.0 returns 0 bill_elements and v2.2.0 returns some ⇒ the old FETCH was the bug.\n" +
            "  • BOTH return 0 while bill-wise is on ⇒ the Ledger collection does not carry opening\n" +
            "    bills on this build; opening bills must come from a different request. Send this\n" +
            "    output (and --dump XML) rather than guessing.");
    }
    return 0;
}

static int RetryFailed(bool json)
{
    using var coordinator = new TallyAgent.Core.Sync.SyncCoordinator();
    var lease = coordinator.TryAcquireAsync("retry-failed",
        Guid.NewGuid().ToString("N")[..12], TimeSpan.Zero, CancellationToken.None)
        .GetAwaiter().GetResult();
    if (!lease.Acquired)
    {
        Emit(json, new { ok = false, status = TallyAgent.Core.Sync.SyncAcquireResult.AlreadyRunning,
                         active_run = lease.ActiveRun?.RunId },
            $"sync_already_running (run {lease.ActiveRun?.RunId ?? "unknown"}) — retry not started.");
        return 1;
    }
    var db = new AgentDatabase(NullLogger<AgentDatabase>.Instance);
    int n;
    try { n = new BatchQueueRepository(db).RetryAllFailed(); }
    finally { coordinator.Release(); }
    Emit(json, new { ok = true, requeued = n }, $"Requeued {n} failed batch(es).");
    return 0;
}

static int ExportDiag(bool json)
{
    var cfg = new ConfigStore().Load();
    var db = new AgentDatabase(NullLogger<AgentDatabase>.Instance);
    var exporter = new DiagnosticsExporter(cfg,
        new BatchQueueRepository(db), new ErrorLogRepository(db), new CheckpointRepository(db));
    var path = exporter.Export();
    Emit(json, new { ok = true, path }, $"Diagnostic bundle: {path}");
    return 0;
}

static int Status(bool json)
{
    var db = new AgentDatabase(NullLogger<AgentDatabase>.Instance);
    var queue = new BatchQueueRepository(db);
    var errors = new ErrorLogRepository(db);
    var checkpoints = new CheckpointRepository(db);
    var stats = queue.GetStats();
    var payload = new
    {
        agent_version = AgentInfo.Version,
        pending_batches = stats.Pending,
        failed_batches = stats.Failed,
        acked_today = stats.AckedToday,
        queue_bytes = stats.TotalQueueBytes,
        last_successful_sync_utc = checkpoints.GetLastSuccessfulSyncUtc(),
        last_error = errors.LastErrorMessage(),
        disk_free_mb = SystemInfo.DiskFreeMb(),
    };
    Emit(json, payload, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

static int Protect(List<string> a)
{
    if (a.Count == 0) { Console.Error.WriteLine("usage: protect <value>"); return 2; }
    Console.WriteLine(DpapiProtector.Protect(a[0]));
    return 0;
}

static int Usage()
{
    Console.WriteLine("""
        TallyAgent.Cli — Tally BigQuery Agent administration

        Verbs:
          test-tally  [--host H] [--port P] [--company C] [--json]
          test-cloud  [--url U] [--token T] [--agent-id A] [--company-id C]
                      [--environment E] [--json]
          save-config --file <plaintext.json> | --set section.key=value ... [--json]
          show-config
          sync-now
          force-full-sync          (reset checkpoints; re-extract full history)
          capture-xml --kind vouchers|masters|alterids [--from d] [--to d]
                      [--collection Ledger]   (save raw Tally XML fixtures)
          verify-bills [--from d] [--to d] [--dump] [--json]
                      [--expect-rows-payable N]    [--expect-total-payable X]
                      [--expect-rows-receivable N] [--expect-total-receivable X]
                      (prove bills_payable/bills_receivable against live Tally;
                       exit 1 on a mismatch)
          diagnose-opening-bills [--dump] [--json]
                      (why opening_bills returns zero rows)
          retry-failed [--json]
          export-diag  [--json]
          status       [--json]
          protect <value>          (print DPAPI-encrypted form)
        """);
    return 2;
}

// ── helpers ──────────────────────────────────────────────────────

static AgentConfig? TryLoadConfig()
{
    try { return new ConfigStore().Load(); } catch { return null; }
}

static void ApplySet(AgentConfig cfg, string key, string value)
{
    switch (key.ToLowerInvariant())
    {
        case "tally.host": cfg.Tally.Host = value; break;
        case "tally.port": cfg.Tally.Port = int.Parse(value); break;
        case "tally.company": cfg.Tally.Company = value; break;
        case "tally.extractionstartdate": cfg.Tally.ExtractionStartDate = value; break;
        case "tally.syncfrequencyminutes": cfg.Tally.SyncFrequencyMinutes = int.Parse(value); break;
        case "tally.autodiscovercompanies": cfg.Tally.AutoDiscoverCompanies = bool.Parse(value); break;
        case "tally.enablemasters": cfg.Tally.EnableMasters = bool.Parse(value); break;
        case "tally.enablevouchers": cfg.Tally.EnableVouchers = bool.Parse(value); break;
        case "tally.enableinventory": cfg.Tally.EnableInventory = bool.Parse(value); break;
        case "tally.enablegst": cfg.Tally.EnableGst = bool.Parse(value); break;
        case "tally.enablecostcentres": cfg.Tally.EnableCostCentres = bool.Parse(value); break;
        case "tally.incrementallookbackdays": cfg.Tally.IncrementalLookbackDays = int.Parse(value); break;
        case "cloud.ingestionapiurl": cfg.Cloud.IngestionApiUrl = value; break;
        case "cloud.agentid": cfg.Cloud.AgentId = value; break;
        case "cloud.companyid": cfg.Cloud.CompanyId = value; break;
        case "cloud.apitoken": cfg.Cloud.ApiToken = value; break;
        case "cloud.environment": cfg.Cloud.Environment = value; break;
        case "notifications.adminemail": cfg.Notifications.AdminEmail = value; break;
        case "notifications.enableemailalerts": cfg.Notifications.EnableEmailAlerts = bool.Parse(value); break;
        case "notifications.errorwebhookurl": cfg.Notifications.ErrorWebhookUrl = value; break;
        case "notifications.googlechatwebhookurl": cfg.Notifications.GoogleChatWebhookUrl = value; break;
        case "notifications.slackwebhookurl": cfg.Notifications.SlackWebhookUrl = value; break;
        default: throw new ArgumentException($"Unknown config key '{key}'");
    }
}

static void Emit(bool json, object payload, string text)
{
    Console.WriteLine(json ? JsonSerializer.Serialize(payload) : text);
}
