namespace TallyAgent.Core.Cloud;

/// <summary>Exponential backoff with jitter for upload retries:
/// 1m → 2m → 4m → 8m → 16m → cap (default 30m), ±20% jitter.</summary>
public static class RetryPolicy
{
    public static TimeSpan NextDelay(int retryCount, int capMinutes = 30)
    {
        var baseMinutes = Math.Min(Math.Pow(2, Math.Min(retryCount, 20)), capMinutes);
        var jitter = 0.8 + Random.Shared.NextDouble() * 0.4; // 0.8 – 1.2
        return TimeSpan.FromMinutes(baseMinutes * jitter);
    }
}
