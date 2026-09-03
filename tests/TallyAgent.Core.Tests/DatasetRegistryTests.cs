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

    // ── per-dataset snapshot control (v2.0.9) ────────────────────────────

    [Fact]
    public void NoPerDatasetSection_BehavesExactlyLikeTheBlanketFlag()
    {
        // Migration guarantee: an existing config.json has no snapshotDatasets
        // section, and must keep the behaviour it had before the upgrade.
        Assert.Equal(
            DatasetRegistry.Enabled(new TallySettings()).Select(d => d.Name),
            DatasetRegistry.Enabled(new TallySettings { SnapshotDatasets = null }).Select(d => d.Name));

        var off = new TallySettings { EnableSnapshots = false, SnapshotDatasets = null };
        Assert.All(SnapshotNames, n => Assert.DoesNotContain(DatasetRegistry.Enabled(off), d => d.Name == n));
    }

    [Fact]
    public void HeavyReportsOff_OutstandingsOn_IsTheProductionShape()
    {
        // The whole point of the release: reach the outstandings without asking
        // Tally for the three reports that hang it.
        var settings = new TallySettings
        {
            SnapshotDatasets = new Dictionary<string, bool>
            {
                ["balance_sheet"] = false,
                ["profit_loss"] = false,
                ["stock_summary"] = false,
            },
        };
        var enabled = DatasetRegistry.Enabled(settings);

        Assert.DoesNotContain(enabled, d => d.Name == "balance_sheet");
        Assert.DoesNotContain(enabled, d => d.Name == "profit_loss");
        Assert.DoesNotContain(enabled, d => d.Name == "stock_summary");
        Assert.Contains(enabled, d => d.Name == "outstanding_payables");
        Assert.Contains(enabled, d => d.Name == "outstanding_receivables");
        Assert.Contains(enabled, d => d.Name == "trial_balance");
    }

    [Fact]
    public void BlanketFlagOff_OverridesAnEntryThatSaysTrue()
    {
        var settings = new TallySettings
        {
            EnableSnapshots = false,
            SnapshotDatasets = new Dictionary<string, bool> { ["outstanding_payables"] = true },
        };
        Assert.DoesNotContain(DatasetRegistry.Enabled(settings), d => d.Name == "outstanding_payables");
    }

    [Fact]
    public void PerDatasetKeys_AreCaseInsensitive()
    {
        var settings = new TallySettings
        {
            SnapshotDatasets = new Dictionary<string, bool> { ["Balance_Sheet"] = false },
        };
        Assert.DoesNotContain(DatasetRegistry.Enabled(settings), d => d.Name == "balance_sheet");
    }

    [Fact]
    public void PerDatasetFlags_DoNotLeakIntoMastersOrVouchers()
    {
        var settings = new TallySettings
        {
            SnapshotDatasets = new Dictionary<string, bool> { ["balance_sheet"] = false },
        };
        var enabled = DatasetRegistry.Enabled(settings);

        Assert.Contains(enabled, d => d.Name == "ledgers");
        Assert.Contains(enabled, d => d.Name == "vouchers");
        Assert.Contains(enabled, d => d.Name == "opening_bills");
    }

    // ── zero-row expectations (v2.0.9) ───────────────────────────────────

    [Fact]
    public void EverySnapshot_ExpectsRows()
    {
        foreach (var name in SnapshotNames)
            Assert.True(DatasetRegistry.ExpectsRows(DatasetRegistry.All.Single(d => d.Name == name)));
    }

    [Fact]
    public void OpeningBills_ExpectsRows_ButOtherMastersMayBeEmpty()
    {
        // opening_bills is a Master, so the old Kind-only guard never fired and
        // it checkpointed successfully on nothing.
        Assert.True(DatasetRegistry.ExpectsRows(
            DatasetRegistry.All.Single(d => d.Name == "opening_bills")));
        Assert.False(DatasetRegistry.ExpectsRows(
            DatasetRegistry.All.Single(d => d.Name == "cost_centres")));
    }

    [Fact]
    public void HeavyReports_AreTheThreeThatComputeAcrossTheWholeCompany()
    {
        Assert.Equal(
            new[] { "balance_sheet", "profit_loss", "stock_summary" },
            DatasetRegistry.HeavyReports.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.All(DatasetRegistry.HeavyReports,
            n => Assert.Equal(DatasetKind.Snapshot, DatasetRegistry.All.Single(d => d.Name == n).Kind));
    }
}
