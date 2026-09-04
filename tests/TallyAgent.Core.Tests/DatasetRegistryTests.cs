using System.Linq;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Tally.Extractors;
using Xunit;

namespace TallyAgent.Core.Tests;

public class DatasetRegistryTests
{
    /// <summary>Every Snapshot dataset.</summary>
    private static readonly string[] SnapshotNames =
    [
        "trial_balance", "balance_sheet", "profit_loss",
        "stock_summary", "outstanding_payables", "outstanding_receivables",
    ];

    /// <summary>The three that make Tally compute across the whole company and
    /// have hung tally.exe. Since v2.3.0 they are OFF unless an explicit config
    /// entry turns them on.</summary>
    private static readonly string[] HeavyNames = ["balance_sheet", "profit_loss", "stock_summary"];

    /// <summary>Snapshots a default install actually runs.</summary>
    private static readonly string[] DefaultOnSnapshots =
        [.. SnapshotNames.Where(n => !HeavyNames.Contains(n))];

    [Fact]
    public void Snapshots_AreEnabledByDefault_ExceptTheThreeThatHangTally()
    {
        var enabled = DatasetRegistry.Enabled(new TallySettings());
        Assert.All(DefaultOnSnapshots, n => Assert.Contains(enabled, d => d.Name == n));
        // v2.3.0: heavy reports default OFF and are derived downstream instead.
        Assert.All(HeavyNames, n => Assert.DoesNotContain(enabled, d => d.Name == n));
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

        // Only the snapshots a default install runs are lost when the blanket
        // flag goes off - the heavy three were already off.
        Assert.Equal(DefaultOnSnapshots.Length, withReports.Count - without.Count);
    }

    // ── per-dataset snapshot control (v2.0.9) ────────────────────────────

    [Fact]
    public void NoPerDatasetSection_IsTheSameAsAnAbsentOne()
    {
        // v2.1.0 promised that an absent section behaved exactly like the
        // blanket flag. v2.3.0 DELIBERATELY breaks that for the three heavy
        // reports, which now default off however the config is written - they
        // hang tally.exe and are derived downstream. Absent and null still agree
        // with each other, which is what upgrade safety actually requires.
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
        Assert.Contains(enabled, d => d.Name == "voucher_lines");
    }

    // ── zero-row expectations (v2.0.9) ───────────────────────────────────

    [Fact]
    public void EverySnapshot_ExpectsRows()
    {
        foreach (var name in SnapshotNames)
            Assert.True(DatasetRegistry.ExpectsRows(DatasetRegistry.All.Single(d => d.Name == name)));
    }

    [Fact]
    public void BillsReports_AreRetired_BecauseTheDataIsDerivable()
    {
        // bill_allocations already carries bill_ref, bill_type, amount, the
        // party ledger, voucher guid/date/type - everything needed to derive
        // both reports in SQL. Asking Tally to compute them was pure cost.
        Assert.DoesNotContain(DatasetRegistry.All, d => d.Name == "bills_payable");
        Assert.DoesNotContain(DatasetRegistry.All, d => d.Name == "bills_receivable");
        Assert.Contains(DatasetRegistry.Enabled(new TallySettings()), d => d.Name == "bill_allocations");
    }

    [Fact]
    public void OpeningBills_IsRetired_AndNoMasterExpectsRowsAnyMore()
    {
        // It produced zero rows for its whole history and would otherwise keep
        // checkpointing successfully on nothing. Removed rather than left in
        // place with an unverified fix.
        Assert.DoesNotContain(DatasetRegistry.All, d => d.Name == "opening_bills");
        Assert.Empty(DatasetRegistry.ExpectedNonEmptyMasters);
        Assert.All(DatasetRegistry.All.Where(d => d.Kind == DatasetKind.Master),
            d => Assert.False(DatasetRegistry.ExpectsRows(d)));
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
