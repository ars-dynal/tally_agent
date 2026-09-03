using System.Windows;
using System.Windows.Controls;
using TallyAgent.Core.Configuration;

namespace TallyAgent.Manager;

/// <summary>Edits config.json. Secret fields left blank keep their existing
/// (encrypted) values; entered values are DPAPI-encrypted on save.</summary>
public partial class ConfigWindow : Window
{
    private readonly ConfigStore _store = new();
    private readonly AgentConfig _config;

    public ConfigWindow()
    {
        InitializeComponent();
        // An existing-but-invalid config.json must still be repairable from here.
        try { _config = _store.LoadOrDefault(); }
        catch { _config = new AgentConfig(); }

        HostBox.Text = _config.Tally.Host;
        PortBox.Text = _config.Tally.Port.ToString();
        CompanyBox.Text = _config.Tally.Company;
        StartDateBox.Text = _config.Tally.ExtractionStartDate;
        EndDateBox.Text = _config.Tally.ExtractionEndDate;
        FrequencyBox.Text = _config.Tally.SyncFrequencyMinutes.ToString();
        LookbackBox.Text = _config.Tally.IncrementalLookbackDays.ToString();
        SnapshotsCheck.IsChecked = _config.Tally.EnableSnapshots;
        // Per-report flags (v2.1.0). A report with no entry in snapshotDatasets
        // falls back to the blanket flag, which is what an upgraded config has,
        // so an existing install shows exactly the state it was already in.
        TrialBalanceCheck.IsChecked = _config.Tally.IsSnapshotEnabled("trial_balance");
        OutstandingPayablesCheck.IsChecked = _config.Tally.IsSnapshotEnabled("outstanding_payables");
        OutstandingReceivablesCheck.IsChecked = _config.Tally.IsSnapshotEnabled("outstanding_receivables");
        BalanceSheetCheck.IsChecked = _config.Tally.IsSnapshotEnabled("balance_sheet");
        ProfitLossCheck.IsChecked = _config.Tally.IsSnapshotEnabled("profit_loss");
        StockSummaryCheck.IsChecked = _config.Tally.IsSnapshotEnabled("stock_summary");
        MastersCheck.IsChecked = _config.Tally.EnableMasters;
        VouchersCheck.IsChecked = _config.Tally.EnableVouchers;
        InventoryCheck.IsChecked = _config.Tally.EnableInventory;
        GstCheck.IsChecked = _config.Tally.EnableGst;
        CostCentresCheck.IsChecked = _config.Tally.EnableCostCentres;
        AutoDiscoverCheck.IsChecked = _config.Tally.AutoDiscoverCompanies;
        ApiUrlBox.Text = _config.Cloud.IngestionApiUrl;
        AgentIdBox.Text = _config.Cloud.AgentId;
        CompanyIdBox.Text = _config.Cloud.CompanyId;
        foreach (ComboBoxItem item in EnvironmentCombo.Items)
            if ((string)item.Content == _config.Cloud.Environment) EnvironmentCombo.SelectedItem = item;
        EmailBox.Text = _config.Notifications.AdminEmail;
        EmailAlertsCheck.IsChecked = _config.Notifications.EnableEmailAlerts;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _config.Tally.Host = HostBox.Text.Trim();
            _config.Tally.Port = int.Parse(PortBox.Text.Trim());
            _config.Tally.Company = CompanyBox.Text.Trim();
            _config.Tally.ExtractionStartDate = StartDateBox.Text.Trim();
            _config.Tally.ExtractionEndDate = EndDateBox.Text.Trim();
            _config.Tally.SyncFrequencyMinutes = int.Parse(FrequencyBox.Text.Trim());
            _config.Tally.IncrementalLookbackDays = int.Parse(LookbackBox.Text.Trim());
            _config.Tally.EnableSnapshots = SnapshotsCheck.IsChecked == true;
            // Always write every report explicitly, so what the window shows is
            // what the file says - no silent fallback once a person has chosen.
            _config.Tally.SnapshotDatasets = new Dictionary<string, bool>
            {
                ["trial_balance"] = TrialBalanceCheck.IsChecked == true,
                ["outstanding_payables"] = OutstandingPayablesCheck.IsChecked == true,
                ["outstanding_receivables"] = OutstandingReceivablesCheck.IsChecked == true,
                ["balance_sheet"] = BalanceSheetCheck.IsChecked == true,
                ["profit_loss"] = ProfitLossCheck.IsChecked == true,
                ["stock_summary"] = StockSummaryCheck.IsChecked == true,
            };
            _config.Tally.EnableMasters = MastersCheck.IsChecked == true;
            _config.Tally.EnableVouchers = VouchersCheck.IsChecked == true;
            _config.Tally.EnableInventory = InventoryCheck.IsChecked == true;
            _config.Tally.EnableGst = GstCheck.IsChecked == true;
            _config.Tally.EnableCostCentres = CostCentresCheck.IsChecked == true;
            _config.Tally.AutoDiscoverCompanies = AutoDiscoverCheck.IsChecked == true;
            _config.Cloud.IngestionApiUrl = ApiUrlBox.Text.Trim();
            _config.Cloud.AgentId = AgentIdBox.Text.Trim();
            _config.Cloud.CompanyId = CompanyIdBox.Text.Trim();
            if (EnvironmentCombo.SelectedItem is ComboBoxItem env)
                _config.Cloud.Environment = (string)env.Content;
            _config.Notifications.AdminEmail = EmailBox.Text.Trim();
            _config.Notifications.EnableEmailAlerts = EmailAlertsCheck.IsChecked == true;

            // blank secret boxes = keep existing encrypted values
            if (TokenBox.Password.Length > 0) _config.Cloud.ApiToken = TokenBox.Password;
            if (GChatBox.Password.Length > 0) _config.Notifications.GoogleChatWebhookUrl = GChatBox.Password;
            if (SlackBox.Password.Length > 0) _config.Notifications.SlackWebhookUrl = SlackBox.Password;

            _store.Save(_config);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Cannot save configuration:\n\n{ex.Message}",
                "Update Configuration", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
