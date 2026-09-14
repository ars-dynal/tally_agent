using TallyAgent.Core.Sync;
using TallyAgent.Core.Tally.Extractors;
using Xunit;

namespace TallyAgent.Core.Tests;

using Row = Dictionary<string, object?>;

/// <summary>
/// The agent's half of the idempotency contract: every row carries a
/// <c>_record_key</c> that is stable across re-reads, so the ingestion API can
/// MERGE instead of append.
///
/// The failure this exists to prevent is on record: <c>voucher_lines</c> was
/// keyed on <c>line_index</c>, Tally renumbered it, and a truncated re-read
/// overwrote a complete copy — 11,695 unbalanced vouchers.
/// </summary>
public class RecordKeyTests
{
    private static Row Line(string guid, string ledger, double amount, int lineIndex) => new()
    {
        ["voucher_guid"] = guid,
        ["entry_type"] = "ledger_line",
        ["line_index"] = lineIndex,
        ["ledger_name"] = ledger,
        ["amount"] = amount,
        ["is_deemed_positive"] = amount > 0,
    };

    [Fact]
    public void RenumberingLineIndex_DoesNotChangeTheKey()
    {
        // THE regression. Same lines, different positions.
        var first = new List<Row> { Line("g1", "Sales", -100, 0), Line("g1", "Cash", 100, 1) };
        var second = new List<Row> { Line("g1", "Cash", 100, 7), Line("g1", "Sales", -100, 9) };

        DatasetRecordKey.Assign("voucher_lines", first, null, null);
        DatasetRecordKey.Assign("voucher_lines", second, null, null);

        Assert.Equal(
            first.Select(r => (string)r[DatasetRecordKey.KeyField]!).OrderBy(x => x),
            second.Select(r => (string)r[DatasetRecordKey.KeyField]!).OrderBy(x => x));
    }

    [Fact]
    public void ReReadingTheSameWindow_ProducesTheSameKeys()
    {
        var a = new List<Row> { Line("g1", "Sales", -100, 0), Line("g2", "Cash", 50, 0) };
        var b = new List<Row> { Line("g1", "Sales", -100, 0), Line("g2", "Cash", 50, 0) };

        DatasetRecordKey.Assign("voucher_lines", a, "2026-09-01", "2026-09-07");
        DatasetRecordKey.Assign("voucher_lines", b, "2026-09-01", "2026-09-07");

        Assert.Equal(a.Select(r => r[DatasetRecordKey.KeyField]),
                     b.Select(r => r[DatasetRecordKey.KeyField]));
    }

    [Fact]
    public void ADifferentAmount_IsADifferentRecord()
    {
        var rows = new List<Row> { Line("g1", "Sales", -100, 0), Line("g1", "Sales", -101, 1) };
        DatasetRecordKey.Assign("voucher_lines", rows, null, null);

        Assert.NotEqual(rows[0][DatasetRecordKey.KeyField], rows[1][DatasetRecordKey.KeyField]);
    }

    [Fact]
    public void TwoGenuinelyIdenticalLines_GetDistinctKeys_AndStayStable()
    {
        // Interchangeable rows still need distinct keys, or a MERGE collapses
        // two real lines into one. Which one gets occurrence 0 cannot matter.
        var a = new List<Row> { Line("g1", "Freight", -50, 0), Line("g1", "Freight", -50, 1) };
        var b = new List<Row> { Line("g1", "Freight", -50, 4), Line("g1", "Freight", -50, 5) };

        DatasetRecordKey.Assign("voucher_lines", a, null, null);
        DatasetRecordKey.Assign("voucher_lines", b, null, null);

        Assert.NotEqual(a[0][DatasetRecordKey.KeyField], a[1][DatasetRecordKey.KeyField]);
        Assert.Equal(a.Select(r => r[DatasetRecordKey.KeyField]),
                     b.Select(r => r[DatasetRecordKey.KeyField]));
    }

    [Fact]
    public void AVoucherReadInTwoOverlappingWindows_KeepsOneIdentity()
    {
        // The incremental lookback re-reads the last 7 days every cycle. If the
        // window took part in a voucher key, every overlap would mint new rows.
        var monday = new List<Row> { Line("g1", "Sales", -100, 0) };
        var tuesday = new List<Row> { Line("g1", "Sales", -100, 0) };

        DatasetRecordKey.Assign("voucher_lines", monday, "2026-09-01", "2026-09-07");
        DatasetRecordKey.Assign("voucher_lines", tuesday, "2026-09-02", "2026-09-08");

        Assert.Equal(monday[0][DatasetRecordKey.KeyField], tuesday[0][DatasetRecordKey.KeyField]);
    }

    [Fact]
    public void ASnapshotTakenOnTwoDays_IsTwoRecords_NotOneOverwritten()
    {
        // trial_balance carries no as-of column, so the batch window supplies
        // it. Without that, Tuesday's trial balance would overwrite Monday's
        // and the history would be a single mutable row.
        var mon = new List<Row> { new() { ["ledger_name"] = "Capital Account", ["closing_credit"] = 30000000.0 } };
        var tue = new List<Row> { new() { ["ledger_name"] = "Capital Account", ["closing_credit"] = 30000000.0 } };

        DatasetRecordKey.Assign("trial_balance", mon, "2026-04-01", "2026-09-05");
        DatasetRecordKey.Assign("trial_balance", tue, "2026-04-01", "2026-09-06");

        Assert.NotEqual(mon[0][DatasetRecordKey.KeyField], tue[0][DatasetRecordKey.KeyField]);
        Assert.True(DatasetRecordKey.KeyIncludesWindow("trial_balance"));
        Assert.False(DatasetRecordKey.KeyIncludesWindow("voucher_lines"));
    }

    [Fact]
    public void NoKeyAnywhereIsPositional()
    {
        // line_index and sequence numbers must not appear in any key, ever.
        foreach (var ds in DatasetRegistry.All)
        {
            var cols = DatasetRecordKey.KeyColumns(ds.Name);
            Assert.DoesNotContain("line_index", cols);
            Assert.DoesNotContain("sequence_no", cols);
        }
    }

    [Fact]
    public void EveryRegisteredDataset_HasADeclaredKey()
    {
        // A dataset with no entry falls back to hashing the whole row, which is
        // correct but coarse and silently so. The contract names all of them.
        foreach (var ds in DatasetRegistry.All)
            Assert.True(DatasetRecordKey.KeyColumns(ds.Name).Count > 0,
                $"{ds.Name} has no declared record key — add it to DatasetRecordKey " +
                "and to docs/idempotency-contract.md.");
    }

    [Fact]
    public void DatasetsWithoutAUniqueNaturalKey_AreDeclaredRatherThanHidden()
    {
        // bank_book carries no voucher GUID. That is a real gap and the
        // contract must say so out loud.
        Assert.Contains("bank_book", DatasetRecordKey.DatasetsWithoutAUniqueNaturalKey());
    }
}
