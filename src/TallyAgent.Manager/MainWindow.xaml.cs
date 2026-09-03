using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using TallyAgent.Core;
using TallyAgent.Core.Cloud;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Data;
using TallyAgent.Core.Diagnostics;
using TallyAgent.Core.Sync;
using TallyAgent.Core.Tally;

namespace TallyAgent.Manager;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _refreshTimer;
    private AgentConfig? _config;
    private AgentDatabase? _db;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = $"v{AgentInfo.Version}";
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _refreshTimer.Tick += (_, _) => RefreshStatus();
        Loaded += (_, _) => { LoadBackend(); RefreshStatus(); _refreshTimer.Start(); };
        Closed += (_, _) => _refreshTimer.Stop();
    }

    private void LoadBackend()
    {
        try
        {
            _config = new ConfigStore().Load();
            _db = new AgentDatabase(NullLogger<AgentDatabase>.Instance);
        }
        catch (Exception ex)
        {
            _config = null;
            FooterText.Text = $"Configuration problem: {ex.Message}";
        }
    }

    // ── status refresh ────────────────────────────────────────────

    /// <summary>Renders progress.json, written by the service as it works, so
    /// this window can show what is happening right now instead of only a list
    /// of past errors. A snapshot that still says "running" but has not been
    /// touched for five minutes is shown as stalled, not as healthy progress.</summary>
    private void RefreshProgress()
    {
        var p = SyncProgressStore.Read();
        if (p is null)
        {
            RunStatusText.Text = "idle";
            RunStatusText.Foreground = System.Windows.Media.Brushes.Gray;
            RunOperationText.Text = "Nothing running";
            RunProgressBar.IsIndeterminate = false;
            RunProgressBar.Value = 0;
            RunCountsText.Text = "";
            RunRangeText.Text = "";
            return;
        }

        var fresh = DateTime.TryParse(p.UpdatedUtc, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var updated)
            && (DateTime.UtcNow - updated.ToUniversalTime()) < TimeSpan.FromMinutes(5);
        var isRunning = p.Status == "running" && fresh;
        var stalled = p.Status == "running" && !fresh;

        RunStatusText.Text = stalled ? "stalled (no update for 5 min)" : p.Status;
        RunStatusText.Foreground = stalled
            ? System.Windows.Media.Brushes.OrangeRed
            : p.Status switch
            {
                "running" => System.Windows.Media.Brushes.DodgerBlue,
                "success" => System.Windows.Media.Brushes.Green,
                "partial" => System.Windows.Media.Brushes.DarkOrange,
                "failed" or "cancelled" => System.Windows.Media.Brushes.Red,
                _ => System.Windows.Media.Brushes.Gray,
            };

        RunOperationText.Text = isRunning || stalled
            ? DescribeOperation(p.Operation)
            : "Nothing running";

        var pct = p.WindowsTotal > 0
            ? 100.0 * p.WindowsDone / p.WindowsTotal
            : p.DatasetsTotal > 0 ? 100.0 * p.DatasetsDone / p.DatasetsTotal
            : 0.0;
        RunProgressBar.IsIndeterminate =
            isRunning && p.WindowsTotal == 0 && p.DatasetsTotal == 0;
        RunProgressBar.Value = Math.Clamp(pct, 0, 100);

        var parts = new List<string>();
        if (p.DatasetsTotal > 0) parts.Add($"datasets {p.DatasetsDone}/{p.DatasetsTotal}");
        if (p.WindowsTotal > 0) parts.Add($"date windows {p.WindowsDone}/{p.WindowsTotal}");
        parts.Add($"{p.Rows:N0} records this run");
        if (DateTime.TryParse(p.StartedUtc, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var startedAt))
        {
            var elapsed = DateTime.UtcNow - startedAt.ToUniversalTime();
            if (elapsed > TimeSpan.Zero)
                parts.Add($"{elapsed:hh\\:mm\\:ss} elapsed");
        }
        RunCountsText.Text = string.Join("   \u00b7   ", parts);

        var rangeFrom = string.IsNullOrWhiteSpace(p.RangeFrom)
            ? "start of this financial year" : p.RangeFrom;
        var range = $"Mode: {p.Mode}   \u00b7   Extracting {rangeFrom} to {p.RangeTo}";
        if (!string.IsNullOrWhiteSpace(p.Message)) range += $"   \u00b7   {p.Message}";
        RunRangeText.Text = range;
    }

    /// <summary>Turns an internal operation string into something readable.</summary>
    private static string DescribeOperation(string op)
    {
        if (op.StartsWith("extract:vouchers", StringComparison.Ordinal))
        {
            var w = op["extract:vouchers".Length..].Trim();
            return w.Length == 0
                ? "Reading vouchers from Tally"
                : $"Reading vouchers from Tally for {w.Replace("..", " to ")}";
        }
        if (op.StartsWith("extract:", StringComparison.Ordinal))
            return $"Reading {op["extract:".Length..].Replace('_', ' ')} from Tally";
        return op switch
        {
            "preflight" => "Checking that Tally is reachable",
            "idle" or "" => "Nothing running",
            _ => op,
        };
    }

    private void RefreshStatus()
    {
        RefreshProgress();
        ServiceStatusText.Text = GetServiceStatus(out var running);
        ServiceStatusText.Foreground = running
            ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Red;

        if (_config is null || _db is null)
        {
            CompanyText.Text = "(not configured)";
            return;
        }

        CompanyText.Text = string.IsNullOrWhiteSpace(_config.Tally.Company)
            ? "(auto-discover)" : _config.Tally.Company;
        EnvironmentText.Text = _config.Cloud.Environment;

        try
        {
            var queue = new BatchQueueRepository(_db);
            var stats = queue.GetStats();
            PendingText.Text = stats.Pending.ToString();
            FailedText.Text = stats.Failed.ToString();
            AckedText.Text = stats.AckedToday.ToString();
            RetryBtn.IsEnabled = stats.Failed > 0;

            var checkpoints = new CheckpointRepository(_db);
            var lastSync = checkpoints.GetLastSuccessfulSyncUtc();
            LastSyncText.Text = lastSync is null ? "never"
                : DateTime.TryParse(lastSync, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                    ? dt.ToLocalTime().ToString("dd MMM yyyy, h:mm tt") : lastSync;

            var errors = new ErrorLogRepository(_db);
            ErrorsGrid.ItemsSource = errors.Recent(30);

            var runs = LatestRunOperation();
            ActivityText.Text = runs ?? "idle";
        }
        catch (Exception ex)
        {
            FooterText.Text = $"Status read error: {ex.Message}";
        }
    }

    private string? LatestRunOperation()
    {
        try
        {
            using var conn = _db!.Open();
            using var cmd = conn.CreateCommand();
            // mode + status + sync id, e.g. "incremental sync (success) · id a1b2c3d4e5f6"
            cmd.CommandText = """
                SELECT mode || ' sync (' || status || ') · id ' || sync_id
                FROM sync_runs ORDER BY started_utc DESC LIMIT 1
                """;
            return cmd.ExecuteScalar() as string;
        }
        catch { return null; }
    }

    private static string GetServiceStatus(out bool running)
    {
        running = false;
        try
        {
            using var sc = new ServiceController(AgentInfo.ServiceName);
            running = sc.Status == ServiceControllerStatus.Running;
            return sc.Status.ToString();
        }
        catch
        {
            return "Not installed";
        }
    }

    // ── button handlers ───────────────────────────────────────────

    private async void TestTally_Click(object sender, RoutedEventArgs e)
    {
        if (_config is null) { Warn("Configure the agent first."); return; }
        TestTallyBtn.IsEnabled = false;
        try
        {
            var client = new TallyClient(_config.Tally, NullLogger<TallyClient>.Instance);
            var probe = await client.ProbeAsync();
            TallyStatusText.Text = probe.Ok ? "Connected" : "Failed";
            TallyStatusText.Foreground = probe.Ok
                ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Red;
            MessageBox.Show(this,
                probe.Ok
                    ? $"Tally is reachable.\n\nOpen companies:\n• {string.Join("\n• ", probe.Companies)}"
                    : $"Tally connection failed:\n\n{probe.Error}",
                "Test Tally Connection",
                MessageBoxButton.OK, probe.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally { TestTallyBtn.IsEnabled = true; }
    }

    private async void TestCloud_Click(object sender, RoutedEventArgs e)
    {
        if (_config is null) { Warn("Configure the agent first."); return; }
        TestCloudBtn.IsEnabled = false;
        try
        {
            var api = new IngestionApiClient(_config, NullLogger<IngestionApiClient>.Instance);
            var ping = await api.PingAsync();
            CloudStatusText.Text = ping.Ok ? "Connected" : "Failed";
            CloudStatusText.Foreground = ping.Ok
                ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Red;
            MessageBox.Show(this,
                ping.Ok ? $"Cloud ingestion API is reachable.\nServer time: {ping.ServerTime}"
                        : "Cloud API responded but reported not-OK.",
                "Test Cloud Connection",
                MessageBoxButton.OK, ping.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            CloudStatusText.Text = "Failed";
            CloudStatusText.Foreground = System.Windows.Media.Brushes.Red;
            MessageBox.Show(this, $"Cloud connection failed:\n\n{ex.Message}",
                "Test Cloud Connection", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { TestCloudBtn.IsEnabled = true; }
    }

    private void StartService_Click(object sender, RoutedEventArgs e) =>
        ControlService(sc => { sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)); }, "started");

    private void StopService_Click(object sender, RoutedEventArgs e) =>
        ControlService(sc => { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45)); }, "stopped");

    private void RestartService_Click(object sender, RoutedEventArgs e) =>
        ControlService(sc =>
        {
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45));
            }
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        }, "restarted");

    private void ControlService(Action<ServiceController> action, string verb)
    {
        try
        {
            using var sc = new ServiceController(AgentInfo.ServiceName);
            action(sc);
            RefreshStatus();
            FooterText.Text = $"Service {verb}.";
        }
        catch (InvalidOperationException ex) when (ex.InnerException is System.ComponentModel.Win32Exception w
            && w.NativeErrorCode == 5)
        {
            Warn("Access denied. Right-click the app and choose 'Run as administrator' to control the service.");
        }
        catch (Exception ex)
        {
            Warn($"Service control failed: {ex.Message}");
        }
    }

    private void ForceFull_Click(object sender, RoutedEventArgs e)
    {
        var officeHours = DateTime.Now.Hour is >= 9 and < 20
            && DateTime.Now.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
        var confirm = MessageBox.Show(this,
            "This re-extracts the ENTIRE voucher history from the configured start " +
            "date — every financial year, from the beginning.\n\n" +
            "It takes SEVERAL HOURS and Tally will be slow for anyone using it the " +
            "whole time.\n\n" +
            "You do not need this for normal operation: the hourly sync already keeps " +
            "everything up to date. Use it only if data is found to be missing.\n\n" +
            (officeHours
                ? "It is currently office hours. Running this now will affect people " +
                  "working in Tally. Consider running it this evening instead.\n\n"
                : "")
            + "Continue?",
            "Re-extract All History", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            AgentInfo.EnsureDirectories();
            File.WriteAllText(Path.Combine(AgentInfo.TriggerDir, "force-full.trigger"),
                DateTime.UtcNow.ToString("O"));
            FooterText.Text = "Force Full Sync requested — the service will restart the full history walk.";
        }
        catch (Exception ex) { Warn($"Could not request full sync: {ex.Message}"); }
    }

    private void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AgentInfo.EnsureDirectories();
            File.WriteAllText(Path.Combine(AgentInfo.TriggerDir, "sync-now.trigger"),
                DateTime.UtcNow.ToString("O"));
            FooterText.Text = "Sync requested — the service will start a cycle within a few seconds.";
        }
        catch (Exception ex) { Warn($"Could not request sync: {ex.Message}"); }
    }

    private void RetryFailed_Click(object sender, RoutedEventArgs e)
    {
        if (_db is null) return;
        try
        {
            // Shares the machine-wide sync exclusion (Phase C2): queue mutation
            // is refused while a sync run is active, with the active run named.
            using var coordinator = new TallyAgent.Core.Sync.SyncCoordinator();
            var lease = coordinator.TryAcquireAsync("retry-failed",
                Guid.NewGuid().ToString("N")[..12], TimeSpan.Zero, CancellationToken.None)
                .GetAwaiter().GetResult();
            if (!lease.Acquired)
            {
                Warn($"A sync run is currently active (run {lease.ActiveRun?.RunId ?? "unknown"}). " +
                     "Retry Failed Batches was not started — try again after the run completes.");
                return;
            }
            int n;
            try { n = new BatchQueueRepository(_db).RetryAllFailed(); }
            finally { coordinator.Release(); }
            FooterText.Text = $"Requeued {n} failed batch(es).";
            RefreshStatus();
        }
        catch (Exception ex) { Warn($"Retry failed: {ex.Message}"); }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AgentInfo.EnsureDirectories();
            Process.Start(new ProcessStartInfo("explorer.exe", AgentInfo.LogsDir) { UseShellExecute = true });
        }
        catch (Exception ex) { Warn(ex.Message); }
    }

    private void ExportDiag_Click(object sender, RoutedEventArgs e)
    {
        if (_config is null || _db is null) { Warn("Configure the agent first."); return; }
        try
        {
            var exporter = new DiagnosticsExporter(_config,
                new BatchQueueRepository(_db), new ErrorLogRepository(_db), new CheckpointRepository(_db));
            var path = exporter.Export();
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            FooterText.Text = $"Diagnostics exported: {path}";
        }
        catch (Exception ex) { Warn($"Export failed: {ex.Message}"); }
    }

    private void EditConfig_Click(object sender, RoutedEventArgs e)
    {
        var win = new ConfigWindow { Owner = this };
        if (win.ShowDialog() == true)
        {
            LoadBackend();

            // v2.1.0: the running service loaded config.json at startup and does
            // not re-read it. Telling the operator to "restart to apply" was a
            // trap - a saved change looked applied and silently was not, and a
            // full sync ran against the old settings. Do the restart here.
            var wasRunning = false;
            try
            {
                using var probe = new ServiceController(AgentInfo.ServiceName);
                wasRunning = probe.Status == ServiceControllerStatus.Running;
            }
            catch { /* service not installed - nothing to restart */ }

            if (wasRunning)
                RestartService_Click(sender, e);   // reports its own outcome
            else
                FooterText.Text = "Configuration saved. Start the service to apply it.";

            RefreshStatus();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshStatus();

    private void Warn(string message) =>
        MessageBox.Show(this, message, "Tally BigQuery Agent", MessageBoxButton.OK, MessageBoxImage.Warning);
}
