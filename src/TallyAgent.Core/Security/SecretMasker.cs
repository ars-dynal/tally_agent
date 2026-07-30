using System.Text.RegularExpressions;

namespace TallyAgent.Core.Security;

/// <summary>Masks secrets before anything reaches logs, alerts or diagnostics.</summary>
public static partial class SecretMasker
{
    [GeneratedRegex(@"(Bearer\s+)[A-Za-z0-9\-._~+/=]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"(dpapi:)[A-Za-z0-9+/=]+")]
    private static partial Regex DpapiRegex();

    [GeneratedRegex(@"(token|key|secret|password|authorization)(""?\s*[:=]\s*""?)([^\s"",;&]{4,})",
        RegexOptions.IgnoreCase)]
    private static partial Regex KeyValueRegex();

    [GeneratedRegex(@"(https://hooks\.slack\.com/services/)[A-Za-z0-9/._\-]+")]
    private static partial Regex SlackRegex();

    [GeneratedRegex(@"(https://chat\.googleapis\.com/v1/spaces/)[^\s""]+")]
    private static partial Regex GChatRegex();

    /// <summary>Show only the last 4 characters of a secret ("••••abcd").</summary>
    public static string MaskSecret(string? secret) =>
        string.IsNullOrEmpty(secret) ? "(not set)"
        : secret.Length <= 4 ? "••••"
        : $"••••{secret[^4..]}";

    /// <summary>Scrub any known secret patterns from free text (log lines, exceptions, URLs).</summary>
    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        text = BearerRegex().Replace(text, "$1••••");
        text = DpapiRegex().Replace(text, "$1••••");
        text = KeyValueRegex().Replace(text, "$1$2••••");
        text = SlackRegex().Replace(text, "$1••••");
        text = GChatRegex().Replace(text, "$1••••");
        return text;
    }
}
