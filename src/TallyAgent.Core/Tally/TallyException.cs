using TallyAgent.Core.Notifications;

namespace TallyAgent.Core.Tally;

/// <summary>Tally-layer failure carrying its error-taxonomy category.</summary>
public sealed class TallyException(ErrorCategory category, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public ErrorCategory Category { get; } = category;

    /// <summary>True when the failure means "stop talking to Tally for this
    /// run" (retry budget exhausted, Tally still busy after a drain wait).
    /// Callers must not retry, split windows, or move on to the next dataset —
    /// the cycle ends and resumes from checkpoints next time.</summary>
    public bool IsRunEnding { get; init; }
}
