using System.Xml.Linq;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Tally;
using TallyAgent.Core.Tally.Extractors;
using Xunit;

namespace TallyAgent.Core.Tests;

/// <summary>
/// v2.2.0 — bills_payable / bills_receivable: the per-bill detail behind the
/// outstanding balances (date, reference, party, pending amount, due date and
/// TALLY'S OWN overdue days).
///
/// SCOPE OF THESE TESTS: the exact element names TallyPrime uses in a "Bills
/// Payable" export were NOT confirmed against a live Tally when these were
/// written (the Tally server was unreachable from the build machine), so the
/// parser accepts several candidate shapes. What is pinned here is the parser's
/// BEHAVIOUR — no double counting, Tally's overdue figure kept rather than
/// recomputed, layout rows ignored, party inherited from a group header — not
/// that the candidate tag names are the right ones. That is settled by
/// `TallyAgent.Cli verify-bills` against the real company.
/// </summary>
public class BillsExtractionTests
{
    private static readonly DateOnly AsOf = new(2026, 9, 3);

    private static XDocument Doc(string inner) => XDocument.Parse($"<ENVELOPE>{inner}</ENVELOPE>");

    private const string TwoBills = """
        <BILLFIXED>
          <BILLDATE>20260715</BILLDATE>
          <BILLREF>INV-001</BILLREF>
          <BILLPARTY>Acme Supplies</BILLPARTY>
          <BILLAMT>1,25,000.50</BILLAMT>
          <BILLDUE>20260814</BILLDUE>
          <BILLOVERDUE>20</BILLOVERDUE>
        </BILLFIXED>
        <BILLFIXED>
          <BILLDATE>20260801</BILLDATE>
          <BILLREF>INV-002</BILLREF>
          <BILLPARTY>Acme Supplies</BILLPARTY>
          <BILLAMT>500.00</BILLAMT>
          <BILLDUE>20260831</BILLDUE>
          <BILLOVERDUE>3</BILLOVERDUE>
        </BILLFIXED>
        """;

    [Fact]
    public void ParsesBillDetail_TheOutstandingDatasetsCannotCarry()
    {
        var rows = ReportExtractor.ParseBillsReport(Doc(TwoBills), AsOf);

        Assert.Equal(2, rows.Count);
        var first = rows[0];
        Assert.Equal("Acme Supplies", first["party_name"]);
        Assert.Equal("INV-001", first["bill_ref"]);
        Assert.Equal("2026-07-15", first["bill_date"]);
        Assert.Equal(125000.50, (double)first["pending_amount"]!);   // Indian grouping
        Assert.Equal("2026-08-14", first["due_date"]);
        Assert.Equal(20L, first["overdue_days"]);
        Assert.Equal("2026-09-03", first["as_of_date"]);
        Assert.Equal("report", first["source"]);
    }

    [Fact]
    public void OverdueDays_AreTallysOwnFigure_NeverRecomputed()
    {
        // 20260715 + a 30-day due date would be "50 days overdue" as of 2026-09-03
        // if we did the arithmetic ourselves. Tally says 20. Tally wins: the
        // credit period and bill type are its business, not ours.
        var rows = ReportExtractor.ParseBillsReport(Doc(TwoBills), AsOf);
        Assert.Equal(20L, rows[0]["overdue_days"]);
        Assert.Equal(3L, rows[1]["overdue_days"]);
    }

    [Fact]
    public void OverdueDays_RenderedWithAUnit_StillReadsAsANumber()
    {
        var rows = ReportExtractor.ParseBillsReport(Doc("""
            <BILLFIXED><BILLREF>X</BILLREF><BILLAMT>10</BILLAMT>
              <BILLOVERDUE>45 Days</BILLOVERDUE></BILLFIXED>
            """), AsOf);
        Assert.Equal(45L, rows[0]["overdue_days"]);
    }

    [Fact]
    public void MissingOverdueColumn_StaysNull_NotZero()
    {
        // 0 would read downstream as "due today", which is a different fact from
        // "Tally did not tell us".
        var rows = ReportExtractor.ParseBillsReport(Doc("""
            <BILLFIXED><BILLREF>X</BILLREF><BILLAMT>10</BILLAMT></BILLFIXED>
            """), AsOf);
        Assert.Null(rows[0]["overdue_days"]);
    }

    [Fact]
    public void NestedContainerShapes_DoNotDoubleCountABill()
    {
        // Some layouts nest one bill shape inside another. Unioning the
        // container names would report every bill twice and double the total —
        // a plausible-looking number that is simply wrong.
        var rows = ReportExtractor.ParseBillsReport(Doc("""
            <BILLFIXED>
              <BILLREF>INV-001</BILLREF><BILLAMT>1000</BILLAMT>
              <BILLS><BILLREF>INV-001</BILLREF><BILLAMT>1000</BILLAMT></BILLS>
            </BILLFIXED>
            """), AsOf);

        Assert.Single(rows);
        Assert.Equal(1000d, rows.Sum(r => (double)r["pending_amount"]!));
    }

    [Fact]
    public void PartyHeaderRows_AreNotBills_ButDoNameTheBillsBeneathThem()
    {
        var rows = ReportExtractor.ParseBillsReport(Doc("""
            <DSPACCNAME><DSPDISPNAME>Beta Traders</DSPDISPNAME></DSPACCNAME>
            <PARTYBLOCK>
              <DSPACCNAME><DSPDISPNAME>Beta Traders</DSPDISPNAME></DSPACCNAME>
              <BILLFIXED><BILLREF>B-9</BILLREF><BILLAMT>750.25</BILLAMT></BILLFIXED>
            </PARTYBLOCK>
            """), AsOf);

        Assert.Single(rows);
        Assert.Equal("Beta Traders", rows[0]["party_name"]);
    }

    [Fact]
    public void LayoutNodesWithNeitherReferenceNorAmount_AreIgnored()
    {
        var rows = ReportExtractor.ParseBillsReport(Doc("""
            <BILLFIXED><SOMELABEL>Total</SOMELABEL></BILLFIXED>
            <BILLFIXED><BILLREF>REAL-1</BILLREF><BILLAMT>42</BILLAMT></BILLFIXED>
            """), AsOf);

        Assert.Single(rows);
        Assert.Equal("REAL-1", rows[0]["bill_ref"]);
    }

    [Fact]
    public void EmptyReport_ParsesToNothing_SoTheCollectionFallbackRuns()
    {
        Assert.Empty(ReportExtractor.ParseBillsReport(Doc("<DUMMY/>"), AsOf));
    }

    // ── envelope ─────────────────────────────────────────────────────────

    [Fact]
    public void BillsEnvelope_CarriesTheReportPeriodAndCompany()
    {
        var xml = TallyEnvelopes.BillsReport("Bills Payable",
            new DateOnly(2026, 4, 1), new DateOnly(2026, 9, 3), "Dynalektric Equipment Private Limited");

        Assert.Contains("<REPORTNAME>Bills Payable</REPORTNAME>", xml);
        Assert.Contains("<SVFROMDATE>20260401</SVFROMDATE>", xml);
        Assert.Contains("<SVTODATE>20260903</SVTODATE>", xml);
        Assert.Contains("<SVCURRENTCOMPANY>Dynalektric Equipment Private Limited</SVCURRENTCOMPANY>", xml);
        // Without this the export collapses to one line per party.
        Assert.Contains("<EXPLODEFLAG>Yes</EXPLODEFLAG>", xml);
    }

    // ── registry: additive only ──────────────────────────────────────────

    [Fact]
    public void BillsDatasets_AreSnapshots_AndDoNotDisturbTheOutstandingOnes()
    {
        var enabled = DatasetRegistry.Enabled(new TallySettings());

        foreach (var name in new[] { "bills_payable", "bills_receivable" })
        {
            var ds = Assert.Single(enabled, d => d.Name == name);
            Assert.Equal(DatasetKind.Snapshot, ds.Kind);
            Assert.True(DatasetRegistry.ExpectsRows(ds));
            // These are per-bill lists, not whole-company computations.
            Assert.DoesNotContain(name, DatasetRegistry.HeavyReports);
        }

        // The datasets they sit beside are untouched — they reconcile to the
        // trial balance and have staging views deployed against them.
        Assert.Contains(enabled, d => d.Name == "outstanding_payables");
        Assert.Contains(enabled, d => d.Name == "outstanding_receivables");
    }

    [Fact]
    public void BillsDatasets_FollowTheSnapshotToggles()
    {
        Assert.DoesNotContain(DatasetRegistry.Enabled(new TallySettings { EnableSnapshots = false }),
            d => d.Name is "bills_payable" or "bills_receivable");

        var perDataset = new TallySettings
        {
            SnapshotDatasets = new Dictionary<string, bool>
            {
                ["bills_payable"] = true,
                ["bills_receivable"] = false,
                ["balance_sheet"] = false,
            },
        };
        var enabled = DatasetRegistry.Enabled(perDataset);
        Assert.Contains(enabled, d => d.Name == "bills_payable");
        Assert.DoesNotContain(enabled, d => d.Name == "bills_receivable");
    }
}
