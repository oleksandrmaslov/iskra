using System.Security.Cryptography;

namespace FlashlightApp.Core;

/// <summary>
/// SHA-256 helpers for verifying firmware ELFs against a catalog entry.
/// Hashes are always handled as lowercase hex strings; comparisons are
/// case-insensitive so a UPPERCASE catalog hash still matches a lowercase
/// computed one.
/// </summary>
public static class FirmwareIntegrity
{
    public static string ComputeSha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        var bytes = SHA256.HashData(stream);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static bool IsValidSha256Hex(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length != 64) return false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>
    /// True when both sides are valid hex and equal ignoring case.
    /// </summary>
    public static bool HashesMatch(string computed, string expected) =>
        IsValidSha256Hex(computed)
        && IsValidSha256Hex(expected)
        && string.Equals(computed, expected, StringComparison.OrdinalIgnoreCase);
}
