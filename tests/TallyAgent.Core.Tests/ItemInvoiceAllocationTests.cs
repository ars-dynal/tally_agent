using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Tally;
using TallyAgent.Core.Tally.Extractors;
using Xunit;

namespace TallyAgent.Core.Tests;

/// <summary>
/// Item invoices keep the sales/purchase ledger inside each stock line's
/// ACCOUNTINGALLOCATIONS.LIST, not on ALLLEDGERENTRIES.LIST. Found 2026-09-10:
/// 14 sales vouchers from 4 Sep arrived with party + GST and no sales line,
/// Rs 1.79 cr short against Tally's trial balance. The extractor now appends
/// the allocations as ledger lines when the ledger list does not balance.
/// </summary>
public sealed class ItemInvoiceAllocationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alloc-" + Guid.NewGuid().ToString("N"));
    public ItemInvoiceAllocationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class FixedTally(string voucherXml) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<ENVELOPE>" + voucherXml + "</ENVELOPE>", Encoding.UTF8, "text/xml")
            });
    }

    private VoucherExtractor Build(string voucherXml)
    {
        var client = new TallyClient(new TallySettings { Company = "Co", RequestPauseSeconds = 0 },
            NullLogger<TallyClient>.Instance, new HttpClient(new FixedTally(voucherXml)), _dir)
        { DelayAsync = (_, _) => Task.CompletedTask };
        return new VoucherExtractor(client, NullLogger<VoucherExtractor>.Instance);
    }

    private const string Head = "<VOUCHER VCHTYPE=\"Sales\"><DATE>20260910</DATE><VOUCHERNUMBER>DEPL/26-27/233</VOUCHERNUMBER><GUID>g-233</GUID>" +
                                "<PARTYLEDGERNAME>PATIALA LOCOMOTIVE WORKS</PARTYLEDGERNAME>";

    // Party debit 44.84 L, IGST 6.84 L; the 38.00 L sales ledger lives only on the stock line.
    private const string ItemInvoice = Head +
        "<ALLLEDGERENTRIES.LIST><LEDGERNAME>PATIALA LOCOMOTIVE WORKS</LEDGERNAME><ISDEEMEDPOSITIVE>Yes</ISDEEMEDPOSITIVE><AMOUNT>-4484000</AMOUNT></ALLLEDGERENTRIES.LIST>" +
        "<ALLLEDGERENTRIES.LIST><LEDGERNAME>Output IGST</LEDGERNAME><ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE><AMOUNT>684000</AMOUNT></ALLLEDGERENTRIES.LIST>" +
        "<ALLINVENTORYENTRIES.LIST><STOCKITEMNAME>Rectifier 1</STOCKITEMNAME><ACTUALQTY>1 Nos</ACTUALQTY><RATE>3800000</RATE><AMOUNT>3800000</AMOUNT>" +
        "<ACCOUNTINGALLOCATIONS.LIST><LEDGERNAME>Interstate Sales @18%-(HSN CODE-85371000)</LEDGERNAME><ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE><AMOUNT>3800000</AMOUNT></ACCOUNTINGALLOCATIONS.LIST>" +
        "</ALLINVENTORYENTRIES.LIST></VOUCHER>";

    // Accounting invoice: the sales ledger is already on the ledger list AND repeated in the allocation.
    private const string AccountingInvoice = Head +
        "<ALLLEDGERENTRIES.LIST><LEDGERNAME>PATIALA LOCOMOTIVE WORKS</LEDGERNAME><AMOUNT>-4484000</AMOUNT></ALLLEDGERENTRIES.LIST>" +
        "<ALLLEDGERENTRIES.LIST><LEDGERNAME>Output IGST</LEDGERNAME><AMOUNT>684000</AMOUNT></ALLLEDGERENTRIES.LIST>" +
        "<ALLLEDGERENTRIES.LIST><LEDGERNAME>Interstate Sales @18%-(HSN CODE-85371000)</LEDGERNAME><AMOUNT>3800000</AMOUNT></ALLLEDGERENTRIES.LIST>" +
        "<ALLINVENTORYENTRIES.LIST><STOCKITEMNAME>Rectifier 1</STOCKITEMNAME><AMOUNT>3800000</AMOUNT>" +
        "<ACCOUNTINGALLOCATIONS.LIST><LEDGERNAME>Interstate Sales @18%-(HSN CODE-85371000)</LEDGERNAME><AMOUNT>3800000</AMOUNT></ACCOUNTINGALLOCATIONS.LIST>" +
        "</ALLINVENTORYENTRIES.LIST></VOUCHER>";

    [Fact]
    public async Task ItemInvoice_SalesLedgerComesFromTheAccountingAllocation_AndTheVoucherBalances()
    {
        var ex = Build(ItemInvoice);
        var day = new DateOnly(2026, 9, 10);
        var r = await ex.ExtractWindow(day, day, new HashSet<string>(), CancellationToken.None);

        Assert.Single(r.VoucherHeaders);
        Assert.Equal(3, r.VoucherLines.Count);
        var sales = Assert.Single(r.VoucherLines, l => (string)l["ledger_name"]! == "Interstate Sales @18%-(HSN CODE-85371000)");
        Assert.Equal(3800000.0, (double)sales["amount"]!);
        Assert.Equal("ledger", sales["entry_type"]);
        Assert.Equal(2, (int)sales["line_index"]!);                       // continues the ledger ordinals
        Assert.Equal(0.0, r.VoucherLines.Sum(l => (double)l["amount"]!), 2);
        Assert.Equal(3, r.Vouchers.Count);                          // flat rows follow
        Assert.Equal(3, r.DayBook.Count);
        Assert.Single(r.InventoryEntries);                          // stock line untouched
    }

    [Fact]
    public async Task AccountingInvoice_AllocationIsNotAppended_NoDoubleCounting()
    {
        var ex = Build(AccountingInvoice);
        var day = new DateOnly(2026, 9, 10);
        var r = await ex.ExtractWindow(day, day, new HashSet<string>(), CancellationToken.None);

        Assert.Equal(3, r.VoucherLines.Count);
        Assert.Single(r.VoucherLines, l => (string)l["ledger_name"]! == "Interstate Sales @18%-(HSN CODE-85371000)");
        Assert.Equal(0.0, r.VoucherLines.Sum(l => (double)l["amount"]!), 2);
    }

    [Fact]
    public void Windows1252_EnDashSurvivesTheLatin1Fallback()
    {
        // "Travel Expenses – New Customer" as Tally writes it: 0x96 in a
        // document that is not valid UTF-8.
        var bytes = Encoding.Latin1.GetBytes("<L><N>Travel Expenses  New Customer</N></L>");
        Assert.Contains("Travel Expenses – New Customer", TallyXml.Sanitize(bytes));
        Assert.Equal("SS M8×40MM", TallyXml.Cp1252("SS M8×40MM"));   // 0xA0-0xFF untouched
    }
}
