using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using TallyAgent.Core;
using TallyAgent.Core.Cloud;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Data;
using TallyAgent.Core.Diagnostics;
using TallyAgent.Core.Notifications;
using TallyAgent.Core.Sync;
using TallyAgent.Core.Tally;

namespace TallyAgent.Manager;

/// <summary>
/// The console answers four questions, in this order:
///   1. Is it healthy?              — one colour, one sentence
///   2. What did the last run do?   — including which datasets did NOT load
///   3. What has been sent?         — "how do I know the data got there?"
///   4. What happened before?       — successes included, not only errors
///
/// The version it replaces showed "datasets 14/30" without saying which 16
/// failed, "Uploaded today: 9" without saying nine of what, and an activity id
/// like 8dc3a1ee5328 that means nothing to a person.
/// </summary>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _refreshTimer;
    private AgentConfig? _config;
    private AgentDatabase? _db;

    public MainWindow()
    {
        InitializeComponent();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _refreshTimer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { LoadBackend(); Refresh(); _refreshTimer.Start(); };
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
            SetHealth("Not configured", ex.Message, Warn);
        }
    }

    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xA4, 0x26, 0x2C));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xB7, 0x6E, 0x00));
    private static readonly Brush Busy = new SolidColorBrush(Color.FromRgb(0x00, 0x5A, 0x9E));

    private void SetHealth(string headline, string detail, Brush colour)
    {
        HealthText.Text = headline;
        HealthText.Foreground = colour;
        HealthDetail.Text = detail;
    }

    private static string Local(string? utc) =>
        DateTime.TryParse(utc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
            ? t.ToLocalTime().ToString("dd MMM HH:mm") : "—";

    private void Refresh()
    {
        if (_db is null) return;
        try
        {
            var runs = new RunHistoryRepository(_db);
            var progress = SyncProgressStore.Read();
            var last = runs.Latest();

            RefreshHealth(progress, last);
            RefreshLastRun(progress, last);
            RefreshDelivery(runs);
            RefreshHistory(runs);
            RefreshProblems();
            RefreshButtons();
        }
        catch (Exception ex)
        {
            SetHealth("Cannot read the agent's status", ex.Message, Warn);
        }
    }

    // ── 1. Is it healthy? ────────────────────────────────────────────────
    private void RefreshHealth(SyncProgressSnapshot? p, RunRecord? last)
    {
        if (p is { Status: "running" })
        {
            SetHealth($"Running — {Describe(p.Operation)}",
                $"{p.Rows:N0} records so far, {p.DatasetsDone} of {p.DatasetsTotal} datasets" +
                (p.WindowsTotal > 0 ? $", window {p.WindowsDone} of {p.WindowsTotal}" : "") +
                $". Started {Local(p.StartedUtc)}.", Busy);
            return;
        }

        if (last is null)
        {
            SetHealth("No sync has run yet",
                "Press “Sync now” once Tally is open and the settings are filled in.", Warn);
            return;
        }

        var missing = last.DatasetsAttempted - last.DatasetsSucceeded;
        switch (last.Status)
        {
            case "success":
                SetHealth($"Healthy — last sync completed {Local(last.FinishedUtc)}, " +
                          $"all {last.DatasetsSucceeded} datasets",
                    $"{last.RecordsQueued:N0} records, {last.Mode} run" +
                    (last.WindowFrom is null ? "" : $", {last.WindowFrom} to {last.WindowTo}") + ".", Good);
                break;

            case "partial":
                SetHealth($"Incomplete — last sync at {Local(last.FinishedUtc)} loaded " +
                          $"{last.DatasetsSucceeded} of {last.DatasetsAttempted} datasets, " +
                          $"{missing} did not load",
                    "See the “Last run” tab for which ones and why.", Warn);
                break;

            case "running":
                SetHealth("A sync is in progress", $"Started {Local(last.StartedUtc)}.", Busy);
                break;

            case "abandoned":
                SetHealth($"Interrupted — the run started {Local(last.StartedUtc)} did not finish",
                    "The service stopped mid-run. It resumes from its checkpoint; no data is lost.", Warn);
                break;

            default:
                SetHealth($"Failed — last sync at {Local(last.FinishedUtc)}" +
                          (missing > 0 ? $", {missing} datasets not loaded" : ""),
                    FirstPlainReason(last) ?? last.ErrorMessage ?? "See the “Problems” tab.", Bad);
                break;
        }
    }

    private static string? FirstPlainReason(RunRecord r) =>
        r.Failures().Count > 0 ? $"{r.Failures()[0].Dataset}: {r.Failures()[0].Reason}" : null;

    /// <summary>"extract:ledgers" is not a sentence.</summary>
    private static string Describe(string operation)
    {
        if (operation.StartsWith("extract:vouchers", StringComparison.OrdinalIgnoreCase))
            return "reading vouchers from Tally " + operation[(operation.IndexOf(' ') + 1)..];
        if (operation.StartsWith("extract:", StringComparison.OrdinalIgnoreCase))
            return "reading " + operation["extract:".Length..].Replace('_', ' ') + " from Tally";
        return operation switch
        {
            "preflight" => "checking Tally is reachable",
            "idle" => "idle",
            _ => operation,
        };
    }

    // ── 2. What did the last run do? ─────────────────────────────────────
    private void RefreshLastRun(SyncProgressSnapshot? p, RunRecord? last)
    {
        ProgressText.Text = p is { Status: "running" }
            ? $"In progress: {Describe(p.Operation)} — {p.Rows:N0} records so far."
            : "";

        if (last is null) { LastRunSummary.Text = "Nothing has run yet."; return; }

        LastRunHeading.Text = $"Last run — {last.Mode}, {last.Status}";
        var took = last.Duration is { } d ? $"{d.TotalMinutes:F0} min" : "—";
        LastRunSummary.Text =
            $"Started {Local(last.StartedUtc)}, finished {Local(last.FinishedUtc)} ({took}).  " +
            (last.WindowFrom is null ? "Masters and reports only.  "
                                     : $"Covered {last.WindowFrom} to {last.WindowTo}.  ") +
            $"{last.DatasetsSucceeded} of {last.DatasetsAttempted} datasets loaded, " +
            $"{last.RecordsQueued:N0} records queued for upload.";

        var failures = last.Failures();
        FailedGrid.ItemsSource = failures.Select(f => new { f.Dataset, f.Reason }).ToList();
        NoFailuresText.Visibility = failures.Count == 0 && last.Status == "success"
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 3. What has been sent to the cloud? ──────────────────────────────
    private void RefreshDelivery(RunHistoryRepository runs)
    {
        DeliveryGrid.ItemsSource = runs.DeliveryByDataset().Select(d => new
        {
            d.Dataset,
            Accepted = d.Acked.ToString("N0"),
            Pending = d.Pending == 0 ? "—" : d.Pending.ToString("N0"),
            Failed = d.Failed == 0 ? "—" : d.Failed.ToString("N0"),
            LastAccepted = Local(d.LastAckUtc),
        }).ToList();
    }

    // ── 4. Run history ───────────────────────────────────────────────────
    private void RefreshHistory(RunHistoryRepository runs)
    {
        HistoryGrid.ItemsSource = runs.Recent(20).Select(r => new
        {
            Started = Local(r.StartedUtc),
            r.Mode,
            Window = r.WindowFrom is null ? "masters/reports" : $"{r.WindowFrom} → {r.WindowTo}",
            Datasets = $"{r.DatasetsSucceeded}/{r.DatasetsAttempted}",
            Records = r.RecordsQueued.ToString("N0"),
            Duration = r.Duration is { } d ? $"{d.TotalMinutes:F0}m" : "—",
            Status = r.Status == "success" ? "completed"
                   : r.Status == "partial" ? $"incomplete ({r.Failures().Count} datasets)"
                   : r.Status,
        }).ToList();
    }

    // ── errors in plain language ─────────────────────────────────────────
    private void RefreshProblems()
    {
        if (_db is null) return;
        var rows = new List<object>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT ts_utc, category, message FROM error_log ORDER BY ts_utc DESC LIMIT 40";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var explanation = PlainLanguage.Describe(r.GetString(1));
            rows.Add(new { When = Local(r.GetString(0)), explanation.What, explanation.Action });
        }
        ErrorGrid.ItemsSource = rows;
    }

    private void RefreshButtons()
    {
        var lookback = _config?.Tally.IncrementalLookbackDays ?? 7;
        var from = DateTime.Today.AddDays(-lookback);
        SyncNowBtn.ToolTip =
            $"Reads {from:dd MMM yyyy} to {DateTime.Today:dd MMM yyyy} — the last {lookback} days " +
            "plus anything missed since the last run. Usually under a minute.";
    }

    // ── actions ──────────────────────────────────────────────────────────
    private void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        var lookback = _config?.Tally.IncrementalLookbackDays ?? 7;
        var from = DateTime.Today.AddDays(-lookback);
        if (MessageBox.Show(this,
                $"Read {from:dd MMM yyyy} to {DateTime.Today:dd MMM yyyy} from Tally?\n\n" +
                "This is the routine catch-up: recent vouchers, masters and the daily reports. " +
                "It normally takes under a minute.",
                "Sync now", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        WriteTrigger("sync-now", "Sync requested — the service will start within a few seconds.");
    }

    private void FullResync_Click(object sender, RoutedEventArgs e)
    {
        var office = DateTime.Now.Hour is >= 9 and < 20;
        if (MessageBox.Show(this,
                "Re-read the ENTIRE history from Tally?\n\n" +
                "Reads 2019 to date — roughly 2 hours, and heavy load on Tally the whole time. " +
                "Everything already collected is re-read; nothing is deleted.\n\n" +
                (office ? "It is currently office hours. People working in Tally will feel this. " +
                          "Consider running it this evening instead.\n\n" : "") +
                "Continue?",
                "Full re-extract", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        WriteTrigger("force-full", "Full re-extract requested — the service will restart the history walk.");
    }

    private void WriteTrigger(string name, string message)
    {
        try
        {
            AgentInfo.EnsureDirectories();
            File.WriteAllText(Path.Combine(AgentInfo.TriggerDir, $"{name}.trigger"),
                DateTime.UtcNow.ToString("O"));
            FooterText.Text = message;
        }
        catch (Exception ex) { FooterText.Text = $"Could not request it: {ex.Message}"; }
    }

    private async void TestTally_Click(object sender, RoutedEventArgs e)
    {
        if (_config is null) return;
        FooterText.Text = "Testing Tally…";
        try
        {
            using var client = new TallyClient(_config.Tally, NullLogger<TallyClient>.Instance);
            var probe = await client.ProbeAsync();
            FooterText.Text = probe.Ok
                ? $"Tally is reachable. Open companies: {string.Join(", ", probe.Companies)}"
                : $"Tally: {PlainLanguage.Describe(probe.Category ?? ErrorCategory.TallyNotRunning).What} " +
                  PlainLanguage.Describe(probe.Category ?? ErrorCategory.TallyNotRunning).Action;
        }
        catch (Exception ex) { FooterText.Text = $"Tally test failed: {ex.Message}"; }
    }

    private async void TestCloud_Click(object sender, RoutedEventArgs e)
    {
        if (_config is null) return;
        FooterText.Text = "Testing the ingestion API…";
        try
        {
            var api = new IngestionApiClient(_config, NullLogger<IngestionApiClient>.Instance);
            var ping = await api.PingAsync();
            FooterText.Text = ping.Ok
                ? $"Ingestion API reachable (server time {ping.ServerTime})."
                : "The ingestion API answered but reported a problem.";
        }
        catch (CloudApiException ex)
        {
            var p = PlainLanguage.Describe(ex.Category);
            FooterText.Text = $"{p.What} {p.Action}";
        }
        catch (Exception ex) { FooterText.Text = $"Cloud test failed: {ex.Message}"; }
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_db is null) return;
        try
        {
            var n = new BatchQueueRepository(_db).RetryAllFailed();
            FooterText.Text = n == 0 ? "Nothing was stuck." : $"{n} stuck upload(s) queued to try again.";
        }
        catch (Exception ex) { FooterText.Text = $"Retry failed: {ex.Message}"; }
    }

    private void Config_Click(object sender, RoutedEventArgs e)
    {
        if (new ConfigWindow { Owner = this }.ShowDialog() == true)
        {
            LoadBackend();
            FooterText.Text = "Settings saved — the service is restarting to pick them up.";
        }
    }

    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(AgentInfo.LogsDir) { UseShellExecute = true }); }
        catch (Exception ex) { FooterText.Text = $"Could not open the log folder: {ex.Message}"; }
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (_config is null || _db is null) return;
        try
        {
            var path = new DiagnosticsExporter(_config, new BatchQueueRepository(_db),
                new ErrorLogRepository(_db), new CheckpointRepository(_db)).Export();
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            FooterText.Text = $"Diagnostics written to {path}";
        }
        catch (Exception ex) { FooterText.Text = $"Diagnostics export failed: {ex.Message}"; }
    }
}
