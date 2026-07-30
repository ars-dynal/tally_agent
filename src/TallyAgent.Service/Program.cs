using Microsoft.Extensions.Logging.EventLog;
using Serilog;
using Serilog.Events;
using TallyAgent.Core;
using TallyAgent.Core.Cloud;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Data;
using TallyAgent.Core.Diagnostics;
using TallyAgent.Core.Notifications;
using TallyAgent.Core.Security;
using TallyAgent.Core.Sync;
using TallyAgent.Core.Tally;
using TallyAgent.Core.Tally.Extractors;
using TallyAgent.Service.Workers;

// ─────────────────────────────────────────────────────────────────
// Tally BigQuery Data Sync Agent — Windows Service host
// Service name: TallyBigQueryAgent
// ─────────────────────────────────────────────────────────────────

AgentInfo.EnsureDirectories();

// Serilog: rolling files under C:\ProgramData\TallyBigQueryAgent\Logs\
// Every event message is scrubbed of secrets before it is written.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(AgentInfo.LogsDir, "agent-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 31,
        fileSizeLimitBytes: 10 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        shared: true,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    Log.Information("Tally BigQuery Agent v{Version} starting (service host)", AgentInfo.Version);

    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddWindowsService(options =>
        options.ServiceName = AgentInfo.ServiceName);

    builder.Logging.ClearProviders();
    builder.Logging.AddSerilog();
    if (OperatingSystem.IsWindows())
    {
        builder.Logging.AddEventLog(new EventLogSettings
        {
            SourceName = AgentInfo.EventLogSource,
            LogName = "Application",
            Filter = (_, level) => level >= LogLevel.Warning,
        });
    }

    // ── configuration (fail-soft: service runs in "unconfigured" mode and
    //    reports via Event Log until config exists, instead of crash-looping) ──
    var configStore = new ConfigStore();
    AgentConfig config;
    try
    {
        config = configStore.Load();
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Configuration missing/invalid — service will idle and re-check every 60s");
        config = null!;
    }

    if (config is null)
    {
        builder.Services.AddHostedService<UnconfiguredWorker>();
    }
    else
    {
        ApplyLogLevel(config.Advanced.LogLevel);

        // Core singletons
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton<AgentDatabase>();
        builder.Services.AddSingleton<BatchQueueRepository>();
        builder.Services.AddSingleton<CheckpointRepository>();
        builder.Services.AddSingleton<ErrorLogRepository>();
        builder.Services.AddSingleton<HeartbeatRepository>();
        builder.Services.AddSingleton(sp => new TallyClient(config.Tally,
            sp.GetRequiredService<ILogger<TallyClient>>()));
        builder.Services.AddSingleton<MasterExtractor>();
        builder.Services.AddSingleton<VoucherExtractor>();
        builder.Services.AddSingleton<ReportExtractor>();
        builder.Services.AddSingleton<BatchBuilder>();
        builder.Services.AddSingleton<SyncEngine>();
        builder.Services.AddSingleton(sp => new IngestionApiClient(config,
            sp.GetRequiredService<ILogger<IngestionApiClient>>()));
        builder.Services.AddSingleton<WebhookNotifier>();
        builder.Services.AddSingleton<ErrorReporter>();
        builder.Services.AddSingleton<DiagnosticsExporter>();
        builder.Services.AddSingleton<AgentState>();

        // Workers
        builder.Services.AddHostedService<SyncWorker>();
        builder.Services.AddHostedService<UploadWorker>();
        builder.Services.AddHostedService<HeartbeatWorker>();
        builder.Services.AddHostedService<ErrorSummaryWorker>();
    }

    var host = builder.Build();

    // Crash-recovery: batches stuck 'uploading' from a previous crash → pending
    if (config is not null)
    {
        var queue = host.Services.GetRequiredService<BatchQueueRepository>();
        var recovered = queue.RecoverStuckUploads();
        if (recovered > 0)
            Log.Warning("Recovered {N} batches stuck in 'uploading' after previous shutdown", recovered);
        BatchBuilder.SweepOrphans(queue);
    }

    await host.RunAsync();
    Log.Information("Tally BigQuery Agent stopped (exit code {Code})", Environment.ExitCode);
    return Environment.ExitCode; // 0 normally; 1 when UnconfiguredWorker requests a restart
}
catch (Exception ex)
{
    Log.Fatal(ex, "Tally BigQuery Agent terminated unexpectedly");
    return 1; // non-zero → SCM failure actions restart us (sc failureflag = 1)
}
finally
{
    Log.CloseAndFlush();
}

static void ApplyLogLevel(string level)
{
    // Rebuild the static logger if a non-default level is configured.
    var parsed = level.ToLowerInvariant() switch
    {
        "debug" => LogEventLevel.Debug,
        "warning" => LogEventLevel.Warning,
        "error" => LogEventLevel.Error,
        _ => LogEventLevel.Information,
    };
    if (parsed == LogEventLevel.Information) return;
    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Is(parsed)
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File(
            Path.Combine(AgentInfo.LogsDir, "agent-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 31,
            fileSizeLimitBytes: 10 * 1024 * 1024,
            rollOnFileSizeLimit: true,
            shared: true)
        .CreateLogger();
}

/// <summary>Shared mutable state surfaced to heartbeats and the manager app.</summary>
public sealed class AgentState
{
    public volatile bool TallyConnected;
    public volatile bool TallyCompanyOpen;
    public volatile bool InternetConnected = true;
    public string? LastAttemptedSyncUtc;
    public string CurrentOperation = "starting";
}

/// <summary>Runs when no valid config exists: logs a reminder, exits idle loop
/// once config appears (SCM restart picks it up), never crash-loops.</summary>
public sealed class UnconfiguredWorker(ILogger<UnconfiguredWorker> log,
    IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (File.Exists(AgentInfo.ConfigPath))
            {
                log.LogInformation("Configuration detected — restarting service to load it");
                // Exit non-zero so SCM failure actions restart us with the new config
                // (a clean exit would leave the service stopped forever).
                Environment.ExitCode = 1;
                lifetime.StopApplication();
                return;
            }
            log.LogWarning(
                "No configuration at {Path}. Run the installer or 'TallyAgent.Cli save-config'. Re-checking in 60s.",
                AgentInfo.ConfigPath);
            await Task.Delay(TimeSpan.FromSeconds(60), ct);
        }
    }
}
