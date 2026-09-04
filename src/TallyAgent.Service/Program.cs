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

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton<AgentDatabase>();
        builder.Services.AddSingleton<BatchQueueRepository>();
        builder.Services.AddSingleton<CheckpointRepository>();
        builder.Services.AddSingleton<ErrorLogRepository>();
        builder.Services.AddSingleton<HeartbeatRepository>();
        builder.Services.AddSingleton<MasterBalanceRepository>();
        builder.Services.AddSingleton<MasterContentHashRepository>();
        builder.Services.AddSingleton(sp => new TallyClient(config.Tally,
            sp.GetRequiredService<ILogger<TallyClient>>()));
        builder.Services.AddSingleton<MasterExtractor>();
        builder.Services.AddSingleton<VoucherExtractor>();
        builder.Services.AddSingleton<ReportExtractor>();
        builder.Services.AddSingleton<BatchBuilder>();
        builder.Services.AddSingleton<SyncEngine>();
        builder.Services.AddSingleton<SyncCoordinator>();
        builder.Services.AddSingleton(sp => new IngestionApiClient(config,
            sp.GetRequiredService<ILogger<IngestionApiClient>>()));
        builder.Services.AddSingleton<WebhookNotifier>();
        builder.Services.AddSingleton<ErrorReporter>();
        builder.Services.AddSingleton<DiagnosticsExporter>();
        builder.Services.AddSingleton<AgentState>();

        builder.Services.AddHostedService<SyncWorker>();
        builder.Services.AddHostedService<UploadWorker>();
        builder.Services.AddHostedService<HeartbeatWorker>();
        builder.Services.AddHostedService<ErrorSummaryWorker>();
        builder.Services.AddHostedService<DailyHealthWorker>();
    }

    var host = builder.Build();

    if (config is not null)
    {
        // Startup recovery shares the sync exclusion (Phase C2): if another
        // process holds the lease (e.g. a second instance mid-sync), recovery
        // is skipped this boot rather than racing it. Bounded 10s wait.
        var startupCoordinator = host.Services.GetRequiredService<SyncCoordinator>();
        var startupLease = await startupCoordinator.TryAcquireAsync(
            "startup-recovery", Guid.NewGuid().ToString("N")[..12],
            TimeSpan.FromSeconds(10), CancellationToken.None);
        if (!startupLease.Acquired)
        {
            Log.Warning("{Status}: another agent process holds the sync lease — skipping startup recovery",
                SyncAcquireResult.AlreadyRunning);
        }
        else
        try
        {
        var queue = host.Services.GetRequiredService<BatchQueueRepository>();
        var recovered = queue.RecoverStuckUploads();
        if (recovered > 0)
            Log.Warning("Recovered {N} batches stuck in 'uploading' after previous shutdown", recovered);

        var missing = queue.MarkRowsWithMissingPayloads();
        if (missing.Count > 0)
            Log.Error("Marked {N} queue rows failed — payload files missing at startup: {Ids}",
                missing.Count, string.Join(", ", missing.Take(10)));

        var swept = BatchBuilder.SweepOrphans(queue);
        if (swept > 0)
            Log.Information("Startup sweep removed {N} orphaned temp/payload files", swept);
        }
        finally
        {
            startupCoordinator.Release();
        }
    }

    await host.RunAsync();
    Log.Information("Tally BigQuery Agent stopped (exit code {Code})", Environment.ExitCode);
    return Environment.ExitCode;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Tally BigQuery Agent terminated unexpectedly");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

static void ApplyLogLevel(string level)
{
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

/// <summary>Shared mutable health state surfaced to workers and diagnostics.</summary>
public sealed class AgentState
{
    public volatile bool TallyConnected;
    public volatile bool TallyCompanyOpen;
    public volatile bool InternetConnected = true;
    /// <summary>True when /health works but optional monitoring/notification routes do not.</summary>
    public volatile bool CloudDegraded;
    public string? LastAttemptedSyncUtc;
    public string CurrentOperation = "starting";
}

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
