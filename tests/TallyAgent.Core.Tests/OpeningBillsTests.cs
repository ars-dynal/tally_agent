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
/// opening_bills — RETIRED in v2.3.0 (see DatasetRegistry.RetiredOpeningBills).
///
/// It returned zero rows for its entire history. The diagnosis was that the
/// FETCH list asked for "BILLALLOCATIONS.LIST" — a serialisation name, not a
/// fetchable member, which Tally ignores silently — but that was reasoned from
/// the code and never confirmed against a live Tally, so the dataset was removed
/// rather than shipped with an unverified fix that might have left it
/// checkpointing on nothing for another few months.
///
/// MasterExtractor.OpeningBills and `TallyAgent.Cli diagnose-opening-bills`
/// remain, so it can be revived the moment there is evidence. These tests keep
/// the parser honest in the meantime, and pin that the LEDGER export no longer
/// carries bill allocations no dataset consumes.
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
    public async Task LedgerRequest_NoLongerCarriesBillAllocations()
    {
        // opening_bills is retired, so making Tally serialise a bill-allocation
        // sub-object for every ledger would be pure waste - the same cost
        // v2.0.5 removed elsewhere. diagnose-opening-bills sends its own fields.
        var (ex, h) = Build(LedgerWithBillsDotList);
        await ex.Ledgers(CancellationToken.None);

        Assert.DoesNotContain("BILLALLOCATIONS", h.Requests.Single());
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
