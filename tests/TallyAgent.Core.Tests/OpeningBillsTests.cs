using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Data;
using TallyAgent.Core.Tally;
using TallyAgent.Core.Tally.Extractors;
using Xunit;

namespace TallyAgent.Core.Tests;

/// <summary>
/// v2.2.0 — opening_bills has returned zero rows for its entire history while
/// bill-wise details are enabled in Tally.
///
/// The leading hypothesis is the FETCH list: v2.1.0 asked for the single field
/// "BILLALLOCATIONS.LIST", but ".LIST" is how Tally SERIALISES a list-valued
/// member, not a member that can be fetched — and Tally ignores an unknown FETCH
/// entry silently, returning a valid response with the sub-object simply absent.
/// The dotted sub-field form is the technique VoucherCollection already uses for
/// ALLLEDGERENTRIES.BILLALLOCATIONS.*, which does produce rows.
///
/// THIS IS NOT YET CONFIRMED against a live Tally — the server was unreachable
/// from the build machine. `TallyAgent.Cli diagnose-opening-bills` sends both
/// field lists and reports which one actually returns bill elements. What these
/// tests pin is that the request now asks for both forms and that the parser
/// reads whichever shape comes back.
/// </summary>
public sealed class OpeningBillsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ob-tests-" + Guid.NewGuid().ToString("N"));
    public OpeningBillsTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class Handler(string reply) : HttpMessageHandler
    {
        public readonly List<string> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Requests.Add(await req.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(reply, Encoding.UTF8, "text/xml") };
        }
    }

    private (MasterExtractor Ex, Handler H) Build(string reply)
    {
        var db = new AgentDatabase(NullLogger<AgentDatabase>.Instance, Path.Combine(_dir, "agent.db"));
        var handler = new Handler(reply);
        var settings = new TallySettings { Company = "Co", RequestPauseSeconds = 0 };
        var client = new TallyClient(settings, NullLogger<TallyClient>.Instance,
            new HttpClient(handler), _dir) { DelayAsync = (_, _) => Task.CompletedTask };
        var ex = new MasterExtractor(client, new MasterBalanceRepository(db),
            NullLogger<MasterExtractor>.Instance);
        ex.BeginCycle("Co", fetchBalances: false);
        return (ex, handler);
    }

    /// <summary>The shape TallyPrime documents for a ledger's opening bill breakup.</summary>
    private const string LedgerWithBillsDotList = """
        <ENVELOPE>
          <LEDGER><GUID>g-1</GUID><NAME>Acme Supplies</NAME><PARENT>Sundry Creditors</PARENT>
            <ISBILLWISEON>Yes</ISBILLWISEON>
            <BILLALLOCATIONS.LIST>
              <NAME>OB-INV-77</NAME><BILLDATE>20250401</BILLDATE>
              <BILLCREDITPERIOD>30 Days</BILLCREDITPERIOD><BILLTYPE>New Ref</BILLTYPE>
              <ISADVANCE>No</ISADVANCE>
              <OPENINGBALANCE>-45000.00</OPENINGBALANCE><CLOSINGBALANCE>-45000.00</CLOSINGBALANCE>
            </BILLALLOCATIONS.LIST>
          </LEDGER>
        </ENVELOPE>
        """;

    /// <summary>The same data under the bare member name, which some builds emit.</summary>
    private const string LedgerWithBillsBare = """
        <ENVELOPE>
          <LEDGER><GUID>g-1</GUID><NAME>Acme Supplies</NAME><PARENT>Sundry Creditors</PARENT>
            <ISBILLWISEON>Yes</ISBILLWISEON>
            <BILLALLOCATIONS>
              <NAME>OB-INV-77</NAME><BILLDATE>20250401</BILLDATE>
              <OPENINGBALANCE>-45000.00</OPENINGBALANCE><CLOSINGBALANCE>-45000.00</CLOSINGBALANCE>
            </BILLALLOCATIONS>
          </LEDGER>
        </ENVELOPE>
        """;

    [Fact]
    public async Task LedgerRequest_AsksForBillAllocationSubFields_NotJustTheListName()
    {
        var (ex, h) = Build(LedgerWithBillsDotList);
        await ex.OpeningBills(CancellationToken.None);

        var request = h.Requests.Single();
        // The dotted sub-fields are the change; the old entry is kept because
        // over-fetching is harmless and the true cause is not yet confirmed.
        Assert.Contains("BILLALLOCATIONS.NAME", request);
        Assert.Contains("BILLALLOCATIONS.BILLDATE", request);
        Assert.Contains("BILLALLOCATIONS.OPENINGBALANCE", request);
        Assert.Contains("BILLALLOCATIONS.CLOSINGBALANCE", request);
        Assert.Contains("BILLALLOCATIONS.LIST", request);
    }

    [Fact]
    public async Task ReadsTheDotListSerialisation()
    {
        var (ex, _) = Build(LedgerWithBillsDotList);
        var row = Assert.Single(await ex.OpeningBills(CancellationToken.None));

        Assert.Equal("Acme Supplies", row["ledger_name"]);
        Assert.Equal("OB-INV-77", row["bill_ref"]);
        Assert.Equal("2025-04-01", row["bill_date"]);
        Assert.Equal("New Ref", row["bill_type"]);
        Assert.Equal(-45000.0, (double)row["opening_amount"]!);
        Assert.False((bool)row["is_advance"]!);
    }

    [Fact]
    public async Task ReadsTheBareSerialisationToo()
    {
        var (ex, _) = Build(LedgerWithBillsBare);
        var row = Assert.Single(await ex.OpeningBills(CancellationToken.None));
        Assert.Equal("OB-INV-77", row["bill_ref"]);
        Assert.Equal(-45000.0, (double)row["opening_amount"]!);
    }

    [Fact]
    public async Task LedgerWithNoOpeningBills_ContributesNoRows()
    {
        var (ex, _) = Build("""
            <ENVELOPE><LEDGER><GUID>g-2</GUID><NAME>Cash</NAME>
              <ISBILLWISEON>No</ISBILLWISEON></LEDGER></ENVELOPE>
            """);
        Assert.Empty(await ex.OpeningBills(CancellationToken.None));
    }

    [Fact]
    public void BillAllocationElements_MatchesEitherSerialisation_CaseInsensitively()
    {
        var ledger = XElement.Parse(
            "<LEDGER><BillAllocations.List><NAME>a</NAME></BillAllocations.List>" +
            "<BILLALLOCATIONS><NAME>b</NAME></BILLALLOCATIONS></LEDGER>");
        Assert.Equal(2, MasterExtractor.BillAllocationElements(ledger).Count());
    }
}
