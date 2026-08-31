using TallyAgent.Core.Configuration;
using TallyAgent.Core.Tally.Extractors;
using Xunit;

namespace TallyAgent.Core.Tests;

public class DatasetRegistryTests
{
    private static readonly string[] SnapshotNames =
    [
        "trial_balance", "balance_sheet", "profit_loss",
        "stock_summary", "outstanding_payables", "outstanding_receivables",
    ];

    [Fact]
    public void Snapshots_AreEnabledByDefault()
    {
        var enabled = DatasetRegistry.Enabled(new TallySettings());
        Assert.All(SnapshotNames, n => Assert.Contains(enabled, d => d.Name == n));
    }

    [Fact]
    public void EnableSnapshotsFalse_RemovesEveryReport_AndKeepsTheRest()
    {
        var settings = new TallySettings { EnableSnapshots = false };
        var enabled = DatasetRegistry.Enabled(settings);

        // No report is asked for - these are the requests that stall a back-fill.
        Assert.All(SnapshotNames, n => Assert.DoesNotContain(enabled, d => d.Name == n));

        // The data the reports would have been derived from is still extracted.
        Assert.Contains(enabled, d => d.Name == "ledgers");
        Assert.Contains(enabled, d => d.Name == "vouchers");
        Assert.NotEmpty(enabled);
    }

    [Fact]
    public void EnableSnapshotsFalse_DoesNotAffectMasterOrVoucherToggles()
    {
        var withReports = DatasetRegistry.Enabled(new TallySettings());
        var without = DatasetRegistry.Enabled(new TallySettings { EnableSnapshots = false });

        Assert.Equal(SnapshotNames.Length, withReports.Count - without.Count);
    }
}
