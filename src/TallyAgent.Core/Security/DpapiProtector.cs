using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TallyAgent.Core.Security;

/// <summary>
/// Encrypts/decrypts secrets with Windows DPAPI (LocalMachine scope) so that both
/// the LocalService-hosted Windows Service and elevated admin tools on the same
/// machine can read them, while the config file is useless if copied elsewhere.
/// Values are stored as "dpapi:&lt;base64&gt;".
/// </summary>
public static class DpapiProtector
{
    public const string Prefix = "dpapi:";

    // Application-specific entropy — not a secret; binds blobs to this product.
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("TallyBigQueryAgent.v1.entropy");

    public static bool IsProtected(string? value) =>
        value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Encrypt a plaintext secret. Returns "dpapi:&lt;base64&gt;". Empty stays empty.</summary>
    public static string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;
        if (IsProtected(plaintext)) return plaintext; // already protected
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("DPAPI is only available on Windows.");

        var blob = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.LocalMachine);
        return Prefix + Convert.ToBase64String(blob);
    }

    /// <summary>Decrypt a "dpapi:" value. Plaintext passthrough for unprotected values
    /// (supports hand-edited dev configs); empty stays empty.</summary>
    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return string.Empty;
        if (!IsProtected(stored)) return stored;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("DPAPI is only available on Windows.");

        var blob = Convert.FromBase64String(stored[Prefix.Length..]);
        var plain = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(plain);
    }
}
