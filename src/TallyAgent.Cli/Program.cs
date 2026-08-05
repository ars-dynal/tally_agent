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

static int RetryFailed(bool json)
{
    var db = new AgentDatabase(NullLogger<AgentDatabase>.Instance);
    var n = new BatchQueueRepository(db).RetryAllFailed();
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
