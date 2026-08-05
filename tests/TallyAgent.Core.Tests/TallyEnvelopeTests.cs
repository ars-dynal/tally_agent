using TallyAgent.Core.Tally;

namespace TallyAgent.Core.Tests;

public sealed class TallyEnvelopeTests
{
    [Fact]
    public void VoucherCollection_UsesExplicitDateFilterAndCompany()
    {
        var xml = TallyEnvelopes.VoucherCollection(
            new DateOnly(2019, 4, 1),
            new DateOnly(2019, 5, 1),
            "Dynalektric Equipment Private Limited");

        Assert.Contains("<TYPE>Collection</TYPE>", xml);
        Assert.Contains("<TYPE>Voucher</TYPE>", xml);
        Assert.Contains("<SVFROMDATE>20190401</SVFROMDATE>", xml);
        Assert.Contains("<SVTODATE>20190501</SVTODATE>", xml);
        Assert.Contains("<SVCURRENTCOMPANY>Dynalektric Equipment Private Limited</SVCURRENTCOMPANY>", xml);
        Assert.Contains("<FILTER>AgentVoucherDateFilter</FILTER>", xml);
        Assert.Contains("$Date &gt;= ##SVFromDate AND $Date &lt;= ##SVToDate", xml);
        Assert.Contains("ALLLEDGERENTRIES.*", xml);
        Assert.Contains("ALLINVENTORYENTRIES.*", xml);
    }

    [Fact]
    public void VoucherCollection_EscapesCompanyName()
    {
        var xml = TallyEnvelopes.VoucherCollection(
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 5),
            "A & B <India>");

        Assert.Contains("<SVCURRENTCOMPANY>A &amp; B &lt;India&gt;</SVCURRENTCOMPANY>", xml);
    }
}
