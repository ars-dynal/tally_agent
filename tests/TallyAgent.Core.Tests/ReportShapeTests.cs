using System.Text;
using System.Xml.Linq;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Tally;
using TallyAgent.Core.Tally.Extractors;
using Xunit;

namespace TallyAgent.Core.Tests;

/// <summary>
/// v2.3.0 — the report parsers, pinned to the shapes in Tally's OWN exports.
///
/// The samples here are hand-written miniatures of the real files (TrialBal.xml,
/// Bills.xml) — same element names, nesting, sign conventions and date format,
/// invented figures. The real exports are live accounting data and are
/// deliberately NOT committed (CLAUDE.md: no production payloads in tests); they
/// are checked with `TallyAgent.Cli verify`, which runs them through these exact
/// parsers.
/// </summary>
public class ReportShapeTests
{
    // ── Bills: the amount is a SIBLING of the record container ───────────

    /// <summary>Exactly the layout Tally emits: BILLCL / BILLDUE / BILLOVERDUE
    /// follow BILLFIXED as siblings rather than sitting inside it.</summary>
    private const string BillsXml = """
        <ENVELOPE>
         <BILLFIXED>
          <BILLDATE>1-Nov-21</BILLDATE>
          <BILLREF>PO/21-22/00172</BILLREF>
          <BILLPARTY>Northwind Supplies</BILLPARTY>
        </BILLFIXED>
         <BILLCL>-369.00</BILLCL>
         <BILLDUE>1-Nov-21</BILLDUE>
         <BILLOVERDUE>1768</BILLOVERDUE>
         <BILLFIXED>
          <BILLDATE>29-Jul-26</BILLDATE>
          <BILLREF>ACME/26-27/164</BILLREF>
          <BILLPARTY>Acme Components</BILLPARTY>
        </BILLFIXED>
         <BILLCL>-112100.00</BILLCL>
         <BILLDUE>26-Nov-26</BILLDUE>
         <BILLOVERDUE></BILLOVERDUE>
         <BILLFIXED>
          <BILLDATE>4-Sep-26</BILLDATE>
          <BILLREF>ACME/26-27/220</BILLREF>
          <BILLPARTY>Acme Components</BILLPARTY>
        </BILLFIXED>
         <BILLCL>-444034.00</BILLCL>
         <BILLDUE>4-Sep-26</BILLDUE>
         <BILLOVERDUE>0</BILLOVERDUE>
        </ENVELOPE>
        """;

    private static readonly DateOnly AsOf = new(2026, 9, 4);

    [Fact]
    public void Bills_ReadTheAmountFromTheFollowingSibling_NotFromInsideTheContainer()
    {
        var rows = ReportExtractor.ParseBillsReport(XDocument.Parse(BillsXml), AsOf);

        Assert.Equal(3, rows.Count);
        // THE regression: searching inside BILLFIXED finds the reference and the
        // party but never BILLCL, giving rows of 0.00 that look like a working
        // extraction. Not one amount may be zero.
        Assert.DoesNotContain(rows, r => (double)r["pending_amount"]! == 0);
        Assert.Equal(-369.00 + -112100.00 + -444034.00,
                     rows.Sum(r => (double)r["pending_amount"]!), 2);
    }

    [Fact]
    public void Bills_KeepTheCreditSignExactlyAsTallyReportsIt()
    {
        var rows = ReportExtractor.ParseBillsReport(XDocument.Parse(BillsXml), AsOf);
        Assert.All(rows, r => Assert.True((double)r["pending_amount"]! < 0));
    }

    [Fact]
    public void Bills_ParseTheTwoDigitYearDatesTallyActuallyEmits()
    {
        // "1-Nov-21" is not "d-MMM-yyyy". Without that format every bill date
        // and due date parsed to null while the row still looked complete.
        var rows = ReportExtractor.ParseBillsReport(XDocument.Parse(BillsXml), AsOf);

        Assert.All(rows, r => Assert.NotNull(r["bill_date"]));
        Assert.All(rows, r => Assert.NotNull(r["due_date"]));
        Assert.Equal("2021-11-01", rows[0]["bill_date"]);
        Assert.Equal("2026-11-26", rows[1]["due_date"]);
    }

    [Fact]
    public void Bills_BlankOverdueIsNull_ButZeroIsZero()
    {
        var rows = ReportExtractor.ParseBillsReport(XDocument.Parse(BillsXml), AsOf);

        Assert.Equal(1768L, rows[0]["overdue_days"]);
        Assert.Null(rows[1]["overdue_days"]);   // blank: not yet due
        Assert.Equal(0L, rows[2]["overdue_days"]);   // zero: due today
    }

    [Fact]
    public void Bills_EachRecordKeepsItsOwnPartyAndReference()
    {
        var rows = ReportExtractor.ParseBillsReport(XDocument.Parse(BillsXml), AsOf);

        Assert.Equal("Northwind Supplies", rows[0]["party_name"]);
        Assert.Equal("Acme Components", rows[1]["party_name"]);
        Assert.Equal("ACME/26-27/220", rows[2]["bill_ref"]);
        // A record must not absorb the NEXT record's amount.
        Assert.Equal(-369.00, (double)rows[0]["pending_amount"]!);
    }

    // ── Trial Balance: DSPCLDRAMTA / DSPCLCRAMTA, debits negative ────────

    private const string TrialBalanceXml = """
        <ENVELOPE>
         <DSPACCNAME><DSPDISPNAME>Capital Account</DSPDISPNAME></DSPACCNAME>
         <DSPACCINFO>
          <DSPCLDRAMT><DSPCLDRAMTA></DSPCLDRAMTA></DSPCLDRAMT>
          <DSPCLCRAMT><DSPCLCRAMTA>30000000.00</DSPCLCRAMTA></DSPCLCRAMT>
        </DSPACCINFO>
         <DSPACCNAME><DSPDISPNAME>Current Liabilities</DSPDISPNAME></DSPACCNAME>
         <DSPACCINFO>
          <DSPCLDRAMT><DSPCLDRAMTA>-110996207.04</DSPCLDRAMTA></DSPCLDRAMT>
          <DSPCLCRAMT><DSPCLCRAMTA>56888982.54</DSPCLCRAMTA></DSPCLCRAMT>
        </DSPACCINFO>
        </ENVELOPE>
        """;

    [Fact]
    public void TrialBalance_ReadsDspclAmtA_AndTreatsNegativeAsDebit()
    {
        var rows = ReportExtractor.ParseTrialBalanceReport(XDocument.Parse(TrialBalanceXml));

        Assert.Equal(2, rows.Count);
        // Pre-v2.3.0 this read DSPCLDR / DSPCLCR / BSMAINAMT — none of which are
        // in this shape — so the report route would have produced rows of zeros.
        Assert.Equal(0d, (double)rows[0]["closing_debit"]!);
        Assert.Equal(30000000.00, (double)rows[0]["closing_credit"]!);

        // Debits arrive NEGATIVE and are reported as positive debit amounts.
        Assert.Equal(110996207.04, (double)rows[1]["closing_debit"]!);
        Assert.Equal(56888982.54, (double)rows[1]["closing_credit"]!);
    }

    [Fact]
    public void TrialBalance_NetAmountIsDebitPositive_MatchingTheLedgerFallback()
    {
        var rows = ReportExtractor.ParseTrialBalanceReport(XDocument.Parse(TrialBalanceXml));

        // The fallback emits -closing (Tally: positive = credit), i.e.
        // debit-positive. The report route must agree or the two routes would
        // silently disagree on sign.
        Assert.Equal(-30000000.00, (double)rows[0]["net_amount"]!);
        Assert.Equal(110996207.04 - 56888982.54, (double)rows[1]["net_amount"]!, 2);
    }

    [Fact]
    public void TrialBalance_TagsEveryRowWithTheReportRoute()
    {
        var rows = ReportExtractor.ParseTrialBalanceReport(XDocument.Parse(TrialBalanceXml));
        Assert.All(rows, r => Assert.Equal("report", r["source"]));
    }

    // ── encoding ─────────────────────────────────────────────────────────

    [Fact]
    public void Utf16LeWithBom_IsDecodedAndTheBomDoesNotBreakTheParse()
    {
        // Tally's UI exports are UTF-16LE with a BOM. A decoded BOM survives as
        // U+FEFF and XDocument.Parse rejects it as "Data at the root level is
        // invalid" — on byte one, before any extractor is reached.
        var xml = "<ENVELOPE><ITEM><NAME>SS M8×40MM</NAME></ITEM></ENVELOPE>";
        var bytes = new byte[] { 0xFF, 0xFE }
            .Concat(Encoding.Unicode.GetBytes(xml)).ToArray();

        var doc = TallyXml.Parse(bytes);
        Assert.Equal("SS M8×40MM", doc.Descendants("NAME").Single().Value);
    }

    [Fact]
    public void SingleByteText_IsNotMangledIntoReplacementCharacters()
    {
        // 0xD7 is the multiplication sign in Latin-1 and an invalid UTF-8 lead
        // byte. Decoding it as UTF-8 produced "SS M8�40MM" silently, since
        // U+FFFD is a valid character that nothing downstream could distinguish
        // from real data.
        var bytes = Encoding.Latin1.GetBytes("<ENVELOPE><NAME>SS M8×40MM</NAME></ENVELOPE>");

        var doc = TallyXml.Parse(bytes);
        var name = doc.Descendants("NAME").Single().Value;

        Assert.Equal("SS M8×40MM", name);
        Assert.DoesNotContain('�', name);
    }

    [Fact]
    public void PlainUtf8_IsStillDecodedAsUtf8()
    {
        var bytes = Encoding.UTF8.GetBytes("<ENVELOPE><NAME>Ω 25×4</NAME></ENVELOPE>");
        Assert.Equal("Ω 25×4", TallyXml.Parse(bytes).Descendants("NAME").Single().Value);
    }

    // ── heavy reports are off by default ─────────────────────────────────

    [Fact]
    public void HeavyReports_DefaultOff_WithNoConfigEntry()
    {
        var enabled = DatasetRegistry.Enabled(new TallySettings());

        foreach (var heavy in new[] { "balance_sheet", "profit_loss", "stock_summary" })
            Assert.DoesNotContain(enabled, d => d.Name == heavy);

        // Everything else a default install expects is still there.
        Assert.Contains(enabled, d => d.Name == "trial_balance");
        Assert.Contains(enabled, d => d.Name == "outstanding_payables");
        Assert.Contains(enabled, d => d.Name == "outstanding_receivables");
    }

    [Fact]
    public void HeavyReports_CanStillBeTurnedOnExplicitly()
    {
        // The default is a default, not a prohibition — an explicit entry wins,
        // so an operator who deliberately asks for one gets it.
        var settings = new TallySettings
        {
            SnapshotDatasets = new Dictionary<string, bool> { ["balance_sheet"] = true },
        };
        Assert.Contains(DatasetRegistry.Enabled(settings), d => d.Name == "balance_sheet");
        Assert.DoesNotContain(DatasetRegistry.Enabled(settings), d => d.Name == "profit_loss");
    }

    // ── opening_bills is retired ─────────────────────────────────────────

    [Fact]
    public void OpeningBills_IsNoLongerRegistered_SoItCannotCheckpointOnNothing()
    {
        Assert.DoesNotContain(DatasetRegistry.All, d => d.Name == "opening_bills");
        Assert.Empty(DatasetRegistry.ExpectedNonEmptyMasters);
    }

    // ── voucher_lines survives emitLegacyVouchersDataset = false ─────────

    [Fact]
    public void VoucherLines_IsRegisteredAndEnabled_IndependentOfTheLegacyCopy()
    {
        var enabled = DatasetRegistry.Enabled(new TallySettings());

        // emitLegacyVouchersDataset only controls the `vouchers` duplicate of
        // day_book; voucher_lines is a separate fan-out target.
        Assert.False(new TallySettings().EmitLegacyVouchersDataset);
        var ds = Assert.Single(enabled, d => d.Name == "voucher_lines");
        Assert.Equal(DatasetKind.Voucher, ds.Kind);
        Assert.Contains(enabled, d => d.Name == "day_book");
    }
}
