using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Tally;
using TallyAgent.Core.Tally.Extractors;
using Xunit;

namespace TallyAgent.Core.Tests;

/// <summary>
/// A Stock Journal in the Day Book report carries its lines as
/// INVENTORYENTRIESOUT.LIST (consumed) and INVENTORYENTRIESIN.LIST (produced).
/// Found 2026-10-08: 137 September stock journals arrived with no stock line.
/// </summary>
public sealed class StockJournalLinesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sj-" + Guid.NewGuid().ToString("N"));
    public StockJournalLinesTests() => Directory.CreateDirectory(_dir);
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

    private const string StockJournal =
        "<VOUCHER VCHTYPE=\"Stock Journal\"><DATE>20260917</DATE><VOUCHERNUMBER>2338</VOUCHERNUMBER><GUID>g-2338</GUID>" +
        "<NARRATION>Transformer Step Down 100VA</NARRATION>" +
        "<INVENTORYENTRIESOUT.LIST><STOCKITEMNAME>DE/ISSUE/26-27/09-773</STOCKITEMNAME><ACTUALQTY> 60 Nos</ACTUALQTY><RATE>904.59/Nos</RATE><AMOUNT>54275.10</AMOUNT></INVENTORYENTRIESOUT.LIST>" +
        "<INVENTORYENTRIESIN.LIST><STOCKITEMNAME>PVT00000023 Transformer Step Down 100VA</STOCKITEMNAME><ACTUALQTY> 60 Nos</ACTUALQTY><RATE>904.59/Nos</RATE><AMOUNT>-54275.10</AMOUNT></INVENTORYENTRIESIN.LIST>" +
        "</VOUCHER>";

    [Fact]
    public async Task StockJournal_SourceAndDestinationLinesAreExtracted_Signed()
    {
        var r = await Build(StockJournal).ExtractWindow(new DateOnly(2026, 9, 17), new DateOnly(2026, 9, 17),
            new HashSet<string>(), CancellationToken.None);
        Assert.Equal(2, r.InventoryEntries.Count);
        var consumed = Assert.Single(r.InventoryEntries, l => (string)l["stock_item"]! == "DE/ISSUE/26-27/09-773");
        var produced = Assert.Single(r.InventoryEntries, l => ((string)l["stock_item"]!).StartsWith("PVT00000023"));
        Assert.True((double)consumed["quantity"]! < 0);
        Assert.True((double)consumed["amount"]! > 0);
        Assert.True((double)produced["quantity"]! > 0);
        Assert.True((double)produced["amount"]! < 0);
    }
}
