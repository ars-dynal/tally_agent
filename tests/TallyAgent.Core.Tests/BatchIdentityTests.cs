using TallyAgent.Core.Sync;
using Xunit;

namespace TallyAgent.Core.Tests;

public class BatchIdentityTests
{
    private const string Sha = "9f2c1ab34e01deadbeef00112233445566778899aabbccddeeff001122334455";

    [Fact]
    public void IdenticalInputs_ProduceIdenticalIds()
    {
        var a = BatchIdentity.Compute("TALLY-SERVER-01", "dynel-electric", "vouchers",
            "2026-07-23", "2026-07-30", 42, Sha);
        var b = BatchIdentity.Compute("TALLY-SERVER-01", "dynel-electric", "vouchers",
            "2026-07-23", "2026-07-30", 42, Sha);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ExpectedFormat()
    {
        var id = BatchIdentity.Compute("TALLY-SERVER-01", "dynel-electric", "vouchers",
            "2026-07-23", "2026-07-30", 42, Sha);
        Assert.Equal("TALLY-SERVER-01-dynel-electric-vouchers-2026-07-23-2026-07-30-000042-9f2c1ab34e01", id);
    }

    [Fact]
    public void DifferentChecksum_ProducesDifferentId()
    {
        var a = BatchIdentity.Compute("A", "C", "vouchers", "2026-07-23", "2026-07-30", 42, Sha);
        var b = BatchIdentity.Compute("A", "C", "vouchers", "2026-07-23", "2026-07-30", 42,
            "0000aaaa1111" + Sha[12..]);
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData(41)]
    [InlineData(43)]
    public void DifferentSequence_ProducesDifferentId(long otherSeq)
    {
        var a = BatchIdentity.Compute("A", "C", "vouchers", "2026-07-23", "2026-07-30", 42, Sha);
        var b = BatchIdentity.Compute("A", "C", "vouchers", "2026-07-23", "2026-07-30", otherSeq, Sha);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void MissingWindow_UsesNaTokens()
    {
        var id = BatchIdentity.Compute("A", "C", "ledgers", null, null, 1, Sha);
        Assert.Contains("-na-na-", id);
    }

    [Fact]
    public void NoWallClockInfluence_IdStableAcrossCalls()
    {
        // Same inputs at different times must match — the formula has no time input.
        var a = BatchIdentity.Compute("A", "C", "vouchers", "2026-07-01", "2026-07-31", 7, Sha);
        System.Threading.Thread.Sleep(30);
        var b = BatchIdentity.Compute("A", "C", "vouchers", "2026-07-01", "2026-07-31", 7, Sha);
        Assert.Equal(a, b);
    }

    [Fact]
    public void UnsafeCharacters_AreSanitized()
    {
        var id = BatchIdentity.Compute("agent one", "co/id", "vouchers", null, null, 1, Sha);
        Assert.DoesNotContain(' ', id);
        Assert.DoesNotContain('/', id);
        Assert.StartsWith("agent_one-co_id-", id);
    }

    [Fact]
    public void ChecksumIsLowercasedAndTruncatedTo12()
    {
        var id = BatchIdentity.Compute("A", "C", "vouchers", null, null, 1, Sha.ToUpperInvariant());
        Assert.EndsWith("-9f2c1ab34e01", id);
    }

    [Theory]
    [InlineData("", "C", "d")]
    [InlineData("A", "", "d")]
    [InlineData("A", "C", "")]
    public void MissingRequiredParts_Throw(string agent, string company, string dataset)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            BatchIdentity.Compute(agent, company, dataset, null, null, 1, Sha));
    }

    [Fact]
    public void ShortChecksum_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            BatchIdentity.Compute("A", "C", "d", null, null, 1, "abc"));
    }
}
