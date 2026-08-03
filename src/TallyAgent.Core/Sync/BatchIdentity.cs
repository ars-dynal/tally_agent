using System.Text;

namespace TallyAgent.Core.Sync;

/// <summary>
/// Deterministic batch identity (ARCHITECTURE.md §9.2).
///
///   batch_id = {agent_id}-{company_id}-{dataset}-{window_from}-{window_to}
///              -{sequence:D6}-{sha256(payload)[:12]}
///
/// Properties:
///  • pure function of stable inputs — no wall-clock, no randomness, so the same
///    extraction produces the same ID across process restarts and upgrades;
///  • the ID is computed once at enqueue time and persisted in SQLite; consumers
///    always read the stored value and never recompute it, so batches created by
///    older agent versions (legacy timestamp format) keep their original IDs;
///  • datasets without a date window (masters/snapshots) use the literal "na"
///    for both window tokens — uniqueness is still guaranteed by sequence+checksum.
/// </summary>
public static class BatchIdentity
{
    /// <summary>Filesystem-/header-safe charset for ID parts.</summary>
    private const string AllowedExtra = "._-";
    private const int MaxPartLength = 64;

    public static string Compute(string agentId, string companyId, string dataset,
        string? windowFrom, string? windowTo, long sequence, string checksumSha256)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("agentId is required", nameof(agentId));
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("companyId is required", nameof(companyId));
        if (string.IsNullOrWhiteSpace(dataset))
            throw new ArgumentException("dataset is required", nameof(dataset));
        if (sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(sequence), "sequence must be non-negative");
        if (string.IsNullOrWhiteSpace(checksumSha256) || checksumSha256.Length < 12)
            throw new ArgumentException("checksumSha256 must be at least 12 hex chars", nameof(checksumSha256));

        return string.Join('-',
            Sanitize(agentId),
            Sanitize(companyId),
            Sanitize(dataset),
            Sanitize(string.IsNullOrWhiteSpace(windowFrom) ? "na" : windowFrom),
            Sanitize(string.IsNullOrWhiteSpace(windowTo) ? "na" : windowTo),
            sequence.ToString("D6"),
            checksumSha256[..12].ToLowerInvariant());
    }

    /// <summary>Replace anything outside [A-Za-z0-9._-] with '_' and cap length.
    /// Config validation already restricts agent/company IDs to this charset;
    /// this is a defensive guarantee, not a transformation in the normal path.</summary>
    private static string Sanitize(string part)
    {
        var sb = new StringBuilder(Math.Min(part.Length, MaxPartLength));
        foreach (var c in part)
        {
            if (sb.Length >= MaxPartLength) break;
            sb.Append(char.IsAsciiLetterOrDigit(c) || AllowedExtra.Contains(c) ? c : '_');
        }
        return sb.Length > 0 ? sb.ToString() : "_";
    }
}
