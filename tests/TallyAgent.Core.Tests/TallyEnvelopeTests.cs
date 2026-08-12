using Xunit;
using TallyAgent.Core.Tally;

namespace TallyAgent.Core.Tests;

public sealed class TallyEnvelopeTests
{
    [Fact]
    public void VoucherCollection_IsPeriodBoundWithoutFullScanFilter()
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
        // The explicit $Date filter forced Tally to scan the ENTIRE voucher
        // file on every window (any window size timed out identically). The
        // collection must stay period-bound via SVFROMDATE/SVTODATE only.
        Assert.DoesNotContain("<FILTER>", xml);
        Assert.DoesNotContain("##SVFromDate", xml);
        // Wildcard sub-object fetches made Tally serialize every nested field
        // (huge XML, frozen UI). Only the explicit dotted fields the extractor
        // actually reads may be requested.
        Assert.DoesNotContain(".*", xml);
        Assert.Contains("ALLLEDGERENTRIES.LEDGERNAME", xml);
        Assert.Contains("ALLLEDGERENTRIES.BILLALLOCATIONS.NAME", xml);
        Assert.Contains("ALLINVENTORYENTRIES.STOCKITEMNAME", xml);
        Assert.Contains("ALLINVENTORYENTRIES.BATCHALLOCATIONS.GODOWNNAME", xml);
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
