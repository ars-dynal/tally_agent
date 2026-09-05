using System.Security.Cryptography;
using System.Text;

namespace TallyAgent.Core.Sync;

using Row = Dictionary<string, object?>;

/// <summary>
/// The stable per-record identity the agent stamps on every row as
/// <c>_record_key</c>, so the ingestion API can MERGE instead of append.
///
/// THE RULE: a key is built from what a row IS, never from where it sat.
/// <c>line_index</c> is deliberately excluded from every key. Tally renumbers
/// it, and when it did, a truncated re-read overwrote a complete copy and left
/// 11,695 unbalanced vouchers. Any key containing a position has that failure
/// built into it.
///
/// Where the business columns alone are not unique — two genuinely identical
/// allocation lines on one voucher — an occurrence number disambiguates them.
/// That is safe precisely because such rows are interchangeable: which of the
/// two gets occurrence 0 cannot matter, and re-reading the same voucher
/// produces the same multiset and therefore the same set of keys.
///
/// See <c>docs/idempotency-contract.md</c>, which this class is the
/// implementation of.
/// </summary>
public static class DatasetRecordKey
{
    /// <summary>The column every row carries.</summary>
    public const string KeyField = "_record_key";

    private sealed record Spec(string[] Columns, bool IncludeWindow, string Note);

    /// <summary>
    /// Business columns per dataset. Datasets absent from this map fall back to
    /// hashing every non-audit column, which is correct but coarse — add an
    /// entry rather than relying on it.
    /// </summary>
    private static readonly Dictionary<string, Spec> Specs = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── masters: Tally's own GUID is the identity ────────────────────
        ["companies"] = new(["company_name"], false, "no GUID is fetched for Company"),
        ["groups"] = new(["master_guid"], false, ""),
        ["ledgers"] = new(["master_guid"], false, ""),
        ["voucher_types"] = new(["master_guid"], false, ""),
        ["cost_centres"] = new(["master_guid"], false, ""),
        ["cost_categories"] = new(["master_guid"], false, ""),
        ["currencies"] = new(["master_guid"], false, ""),
        ["uom"] = new(["master_guid"], false, ""),
        ["gst_rates"] = new(["master_guid"], false, "one row per stock item"),
        ["stock_groups"] = new(["master_guid"], false, ""),
        ["stock_items"] = new(["master_guid"], false, ""),
        ["godowns"] = new(["master_guid"], false, ""),
        ["stock_standard_costs"] = new(["master_guid", "effective_date"], false, ""),
        ["stock_standard_prices"] = new(["master_guid", "effective_date"], false, ""),

        // ── voucher level: one row per voucher ───────────────────────────
        ["voucher_headers"] = new(["guid"], false, ""),
        ["voucher_guid_manifest"] = new(["guid"], false, ""),
        ["sales_register"] = new(["guid"], false, ""),
        ["purchase_register"] = new(["guid"], false, ""),

        // ── voucher children: guid + what the line SAYS, never its index ──
        ["voucher_lines"] = new(
            ["voucher_guid", "ledger_name", "amount", "is_deemed_positive"], false, ""),
        ["vouchers"] = new(
            ["voucher_guid", "ledger_name", "amount", "is_deemed_positive"], false,
            "legacy copy of day_book; off by default"),
        ["day_book"] = new(
            ["voucher_guid", "ledger_name", "amount", "is_deemed_positive"], false, ""),
        ["bill_allocations"] = new(
            ["voucher_guid", "ledger_name", "bill_ref", "bill_type", "amount"], false, ""),
        ["bank_allocations"] = new(
            ["voucher_guid", "ledger_name", "instrument_number", "bank_name", "amount"], false, ""),
        ["cost_centre_allocations"] = new(
            ["voucher_guid", "ledger_name", "cost_centre", "cost_category", "amount"], false, ""),
        ["inventory_entries"] = new(
            ["voucher_guid", "stock_item", "godown", "quantity", "rate", "amount"], false, ""),
        ["sales_invoice_lines"] = new(
            ["voucher_guid", "stock_item", "godown", "quantity", "rate", "amount"], false, ""),

        // FLAGGED: bank_book carries no voucher GUID at all, so its identity
        // rests on the bank, the date and the instrument. Two identical
        // same-day transfers on one account are indistinguishable and rely on
        // the occurrence number.
        ["bank_book"] = new(
            ["bank_account", "txn_date", "voucher_number", "cheque_number", "debit", "credit"],
            false, "NO voucher GUID — see the contract document"),

        // ── snapshots: a value AS OF a date, so the date is part of identity
        // The rows carry no as-of column, so the batch's window_to supplies it.
        ["trial_balance"] = new(["ledger_name"], true, ""),
        ["balance_sheet"] = new(["ledger_name"], true, ""),
        ["profit_loss"] = new(["ledger_name"], true, ""),
        ["stock_summary"] = new(["item_name"], true, ""),
        ["outstanding_payables"] = new(["party_name"], true, ""),
        ["outstanding_receivables"] = new(["party_name"], true, ""),
    };

    /// <summary>The key columns a dataset uses, for the contract document and
    /// for tests. Empty when the dataset falls back to whole-row hashing.</summary>
    public static IReadOnlyList<string> KeyColumns(string dataset) =>
        Specs.TryGetValue(dataset, out var spec) ? spec.Columns : [];

    public static bool KeyIncludesWindow(string dataset) =>
        Specs.TryGetValue(dataset, out var spec) && spec.IncludeWindow;

    /// <summary>Datasets with no fully unique natural key, which therefore lean
    /// on the occurrence number. Named so the contract can flag them.</summary>
    public static IReadOnlyList<string> DatasetsWithoutAUniqueNaturalKey() =>
        [.. Specs.Where(kv => kv.Value.Note.Length > 0 && kv.Value.Note.StartsWith("NO "))
                 .Select(kv => kv.Key)];

    /// <summary>
    /// Stamp <c>_record_key</c> on every row.
    ///
    /// Called once per dataset per window with the COMPLETE row set, before the
    /// rows are sliced into batches — occurrence numbers must be assigned over
    /// the whole set, or a batch boundary would restart them and two batches
    /// would claim the same key.
    /// </summary>
    public static void Assign(string dataset, IReadOnlyList<Row> rows,
        string? windowFrom, string? windowTo)
    {
        var spec = Specs.TryGetValue(dataset, out var found) ? found : null;
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var basis = BuildBasis(dataset, row, spec, windowFrom, windowTo);
            seen.TryGetValue(basis, out var occurrence);
            seen[basis] = occurrence + 1;
            row[KeyField] = Hash(basis + "|#" + occurrence.ToString());
        }
    }

    private static string BuildBasis(string dataset, Row row, Spec? spec,
        string? windowFrom, string? windowTo)
    {
        var sb = new StringBuilder(dataset).Append('|');

        if (spec is null)
        {
            // No entry: hash every business column, keys sorted so the result
            // does not depend on insertion order.
            foreach (var key in row.Keys.Where(IsBusinessColumn).OrderBy(k => k, StringComparer.Ordinal))
                sb.Append(key).Append('=').Append(Value(row[key])).Append('|');
            return sb.ToString();
        }

        if (spec.IncludeWindow)
            sb.Append("asof=").Append(windowTo ?? windowFrom ?? "na").Append('|');

        foreach (var column in spec.Columns)
            sb.Append(column).Append('=')
              .Append(row.TryGetValue(column, out var v) ? Value(v) : "").Append('|');

        return sb.ToString();
    }

    /// <summary>Audit and derived columns never take part in identity — they
    /// change on every upload by construction.</summary>
    private static bool IsBusinessColumn(string column) =>
        column != KeyField &&
        column != "_company" &&
        Array.IndexOf(BatchBuilder.AuditFields, column) < 0;

    /// <summary>Invariant rendering: a double must not key differently because
    /// the machine's locale prints it with a comma.</summary>
    private static string Value(object? v) => v switch
    {
        null => "",
        double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        bool b => b ? "1" : "0",
        _ => v.ToString() ?? "",
    };

    private static string Hash(string basis) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(basis)))[..32].ToLowerInvariant();
}
