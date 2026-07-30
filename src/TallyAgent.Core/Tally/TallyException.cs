using TallyAgent.Core.Notifications;

namespace TallyAgent.Core.Tally;

/// <summary>Tally-layer failure carrying its error-taxonomy category.</summary>
public sealed class TallyException(ErrorCategory category, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public ErrorCategory Category { get; } = category;
}
