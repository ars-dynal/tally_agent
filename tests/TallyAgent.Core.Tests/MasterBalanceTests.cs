using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Data;
using TallyAgent.Core.Tally;
using TallyAgent.Core.Tally.Extractors;
using Xunit;

namespace TallyAgent.Core.Tests;

/// <summary>v2.0.5: computed master balances are requested from Tally only on
/// the daily capture cycle, persisted per GUID, and served from that store on
/// every other cycle — so ledgers/stock_items always carry balance columns
/// while Tally is asked to re-value the company at most once a day.</summary>
public sealed class MasterBalanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mb-tests-" + Guid.NewGuid().ToString("N"));
    public MasterBalanceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class Handler(Func<string, string> reply) : HttpMessageHandler
    {
        public readonly List<string> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var body = await req.Content!.ReadAsStringAsync(ct);
            Requests.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(reply(body), Encoding.UTF8, "text/xml") };
        }
    }

    private const string LedgerXml =
        "<ENVELOPE><LEDGER><GUID>g-1</GUID><NAME>Cash</NAME><PARENT>Cash-in-Hand</PARENT>" +
        "<OPENINGBALANCE>-100.00</OPENINGBALANCE><CLOSINGBALANCE>-250.50</CLOSINGBALANCE></LEDGER>" +
        "<LEDGER><GUID>g-2</GUID><NAME>HDFC</NAME><PARENT>Bank Accounts</PARENT></LEDGER></ENVELOPE>";

    private (MasterExtractor ex, Handler h) Build(bool fetchBalances)
    {
        var db = new AgentDatabase(NullLogger<AgentDatabase>.Instance, Path.Combine(_dir, "agent.db"));
        var store = new MasterBalanceRepository(db);
        var handler = new Handler(body =>
            body.Contains("CLOSINGBALANCE") ? LedgerXml : LedgerXml.Replace("<OPENINGBALANCE>-100.00</OPENINGBALANCE><CLOSINGBALANCE>-250.50</CLOSINGBALANCE>", ""));
        var settings = new TallySettings { Company = "Co", RequestPauseSeconds = 0 };
        var client = new TallyClient(settings, NullLogger<TallyClient>.Instance, new HttpClient(handler), _dir)
        { DelayAsync = (_, _) => Task.CompletedTask };
        var ex = new MasterExtractor(client, store, NullLogger<MasterExtractor>.Instance);
        ex.BeginCycle("Co", fetchBalances);
        return (ex, handler);
    }

    [Fact]
    public async Task CaptureCycle_AsksTallyForBalances_AndPersistsThem()
    {
        var (ex, h) = Build(fetchBalances: true);
        var rows = await ex.Ledgers(CancellationToken.None);
        Assert.Contains("<FETCH>CLOSINGBALANCE</FETCH>", h.Requests[0]);
        var cash = rows.Single(r => (string)r["ledger_name"]! == "Cash");
        Assert.Equal(-250.5, (double)cash["closing_balance"]!);
        Assert.Equal(-100.0, (double)cash["opening_balance"]!);
        var capturedAt = (string)cash["balance_as_of"]!;
        Assert.EndsWith("Z", capturedAt);

        // A later NON-capture cycle must not ask Tally for balances, yet still
        // emits the last captured values (HDFC had no balance tag → captured 0).
        var (ex2, h2) = Build(fetchBalances: false);
        var rows2 = await ex2.Ledgers(CancellationToken.None);
        // Match the whole FETCH entry: the ledger's own balance fields must be
        // absent, while BILLALLOCATIONS.OPENINGBALANCE/CLOSINGBALANCE (opening
        // bill detail, not a computed valuation) are always requested.
        Assert.DoesNotContain("<FETCH>CLOSINGBALANCE</FETCH>", h2.Requests[0]);
        Assert.DoesNotContain("<FETCH>OPENINGBALANCE</FETCH>", h2.Requests[0]);
        var cash2 = rows2.Single(r => (string)r["ledger_name"]! == "Cash");
        Assert.Equal(-250.5, (double)cash2["closing_balance"]!);
        Assert.Equal(0.0, (double)rows2.Single(r => (string)r["ledger_name"]! == "HDFC")["closing_balance"]!);
        // Staleness is visible on the record: balance_as_of is the CAPTURE time,
        // not this cycle's extraction time.
        var asOf = (string)cash2["balance_as_of"]!;
        Assert.True(DateTime.Parse(asOf, null, System.Globalization.DateTimeStyles.RoundtripKind)
                    <= DateTime.UtcNow);
        Assert.All(rows2, r => Assert.Equal(asOf, (string)r["balance_as_of"]!));
    }

    [Fact]
    public async Task NoCaptureYet_BalanceAsOfIsNull()
    {
        var (ex, _) = Build(fetchBalances: false);
        var rows = await ex.Ledgers(CancellationToken.None);
        Assert.All(rows, r => Assert.Null(r["balance_as_of"]));
        Assert.All(rows, r => Assert.Null(r["closing_balance"]));
    }

    /// <summary>
    /// The two balance-bearing collections are scoped DIFFERENTLY, and the
    /// difference is the point.
    ///
    /// Ledger is FY-scoped: with only a to-date Tally accumulates a P&amp;L ledger
    /// from the start of the books, and Salary and Wages came back at
    /// -6,83,59,693 (all time) rather than -1,23,36,779 (this year).
    ///
    /// StockItem gets NO from-date: closing stock is cumulative by nature, and
    /// with SVTODATE alone it already ties to Tally exactly.
    /// </summary>
    [Fact]
    public async Task Ledger_IsFinancialYearScoped_StockItem_IsNot()
    {
        var db = new AgentDatabase(NullLogger<AgentDatabase>.Instance, Path.Combine(_dir, "fy.db"));
        var handler = new Handler(body => body.Contains("<TYPE>StockItem</TYPE>")
            ? "<ENVELOPE><STOCKITEM><NAME>Widget</NAME><CLOSINGVALUE>-100</CLOSINGVALUE></STOCKITEM></ENVELOPE>"
            : LedgerXml);
        var client = new TallyClient(new TallySettings { Company = "Co", RequestPauseSeconds = 0 },
            NullLogger<TallyClient>.Instance, new HttpClient(handler), _dir)
        { DelayAsync = (_, _) => Task.CompletedTask };
        var ex = new MasterExtractor(client, new MasterBalanceRepository(db),
            NullLogger<MasterExtractor>.Instance);
        // 5-Sep-2026 sits in FY 2026-27, which begins 1-Apr-2026.
        ex.BeginCycle("Co", fetchBalances: true, asOf: new DateOnly(2026, 9, 5));

        await ex.Ledgers(CancellationToken.None);
        await ex.StockItems(CancellationToken.None);

        var ledgerReq = handler.Requests.Single(r => r.Contains("<TYPE>Ledger</TYPE>"));
        var stockReq = handler.Requests.Single(r => r.Contains("<TYPE>StockItem</TYPE>"));

        Assert.Contains("<SVFROMDATE>20260401</SVFROMDATE>", ledgerReq);
        Assert.Contains("<SVTODATE>20260905</SVTODATE>", ledgerReq);

        Assert.DoesNotContain("SVFROMDATE", stockReq);
        Assert.Contains("<SVTODATE>20260905</SVTODATE>", stockReq);
    }

    [Theory]
    [InlineData(2026, 9, 5, 20260401)]    // September -> FY starts this April
    [InlineData(2026, 4, 1, 20260401)]    // 1 April itself
    [InlineData(2026, 3, 31, 20250401)]   // March -> FY started LAST April
    [InlineData(2027, 1, 15, 20260401)]   // January -> still the prior April
    public async Task FinancialYearStart_IsTheAprilOnOrBeforeTheAsOfDate(
        int y, int m, int d, int expected)
    {
        var db = new AgentDatabase(NullLogger<AgentDatabase>.Instance,
            Path.Combine(_dir, $"fy{y}{m}{d}.db"));
        var handler = new Handler(_ => LedgerXml);
        var client = new TallyClient(new TallySettings { Company = "Co", RequestPauseSeconds = 0 },
            NullLogger<TallyClient>.Instance, new HttpClient(handler), _dir)
        { DelayAsync = (_, _) => Task.CompletedTask };
        var ex = new MasterExtractor(client, new MasterBalanceRepository(db),
            NullLogger<MasterExtractor>.Instance);
        ex.BeginCycle("Co", fetchBalances: true, asOf: new DateOnly(y, m, d));

        await ex.Ledgers(CancellationToken.None);

        Assert.Contains($"<SVFROMDATE>{expected}</SVFROMDATE>", handler.Requests[0]);
    }

    [Fact]
    public async Task BalanceBearingCollections_PinSvToDate()
    {
        // Without SVTODATE, Tally evaluates $ClosingBalance against whatever
        // period is loaded, so the as-of date of every master balance was set
        // by whoever last pressed Alt+F2. Observed 2026-09: Salary and Wages
        // tying exactly at 31-Jul, a period end rather than a drift.
        var db = new AgentDatabase(NullLogger<AgentDatabase>.Instance, Path.Combine(_dir, "asof.db"));
        var handler = new Handler(_ => LedgerXml);
        var client = new TallyClient(new TallySettings { Company = "Co", RequestPauseSeconds = 0 },
            NullLogger<TallyClient>.Instance, new HttpClient(handler), _dir)
        { DelayAsync = (_, _) => Task.CompletedTask };
        var ex = new MasterExtractor(client, new MasterBalanceRepository(db),
            NullLogger<MasterExtractor>.Instance);
        ex.BeginCycle("Co", fetchBalances: true, asOf: new DateOnly(2026, 9, 5));

        var rows = await ex.Ledgers(CancellationToken.None);

        Assert.Contains("<SVTODATE>20260905</SVTODATE>", handler.Requests[0]);
        Assert.NotEmpty(rows);
        // balance_as_of is deliberately unchanged - still the capture instant.
        Assert.All(rows, r => Assert.EndsWith("Z", (string)r["balance_as_of"]!));
    }

    [Fact]
    public async Task LedgerCollection_IsFetchedOncePerCycle_ForAllDerivedDatasets()
    {
        var (ex, h) = Build(fetchBalances: false);
        await ex.Ledgers(CancellationToken.None);
        await ex.OpeningBills(CancellationToken.None);
        var banks = await ex.BankLedgerNames(CancellationToken.None);
        Assert.Single(h.Requests);                           // ONE Tally request
        Assert.Contains("HDFC", banks);
    }
}
