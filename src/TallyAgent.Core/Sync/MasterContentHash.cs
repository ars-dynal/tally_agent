using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TallyAgent.Core.Sync;

using Row = Dictionary<string, object?>;

/// <summary>
/// Canonical content hash of an extracted master dataset. Two extractions of the
/// same Tally data must produce the same hash; any real change must produce a
/// different one.
///
/// AUDIT FIELDS ARE EXCLUDED (<see cref="BatchBuilder.AuditFields"/>).
/// <c>_sync_id</c> and <c>_sync_timestamp</c> are stamped on every row at
/// enqueue time and are different on every single upload by construction —
/// hashing them would make the hash never match, so the skip would quietly do
/// nothing while every test that only checks "changed content uploads" still
/// passed. That is the failure this exclusion exists to prevent.
///
/// <c>balance_as_of</c> is deliberately NOT excluded: it marks WHICH daily
/// balance capture the row carries, so it changes exactly when the balances
/// themselves do (once a day) and is stable in between. That is a real content
/// change and should re-upload.
/// </summary>
public static class MasterContentHash
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    /// <summary>Hex SHA-256 over dataset, company and every row in extraction
    /// order. Keys within a row are sorted so a change in how an extractor
    /// happens to order its dictionary is not mistaken for a data change; row
    /// ORDER is left alone, because a reordering is cheap to re-upload and
    /// treating it as unchanged would be the dangerous direction.</summary>
    public static string Compute(string dataset, string company, IReadOnlyList<Row> rows)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{dataset}{company}{rows.Count}\n"));
        foreach (var row in rows)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Canonical(row), JsonOpts)));
            hash.AppendData("\n"u8.ToArray());
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary><c>_company</c> is stamped onto the row by
    /// <see cref="BatchBuilder.BuildAndEnqueue"/> rather than by the extractor,
    /// so a row hashed before enqueue and the same row afterwards would
    /// otherwise differ. It carries no information the hash lacks — company is
    /// already part of the key and of the preamble below.</summary>
    private const string EnqueueStampedCompanyField = "_company";

    /// <summary>Row without audit or enqueue-stamped fields, keys in ordinal
    /// order. Hashing the same rows before and after an enqueue must give the
    /// same answer: anything the upload path writes onto a row is, by
    /// definition, not part of what Tally said.</summary>
    private static SortedDictionary<string, object?> Canonical(Row row)
    {
        var copy = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in row)
            if (Array.IndexOf(BatchBuilder.AuditFields, key) < 0 &&
                key != EnqueueStampedCompanyField)
                copy[key] = value;
        return copy;
    }
}
