namespace Iskra.Core;

/// <summary>
/// Fixed origin policy for URLs received inside GitHub API responses. OAuth
/// bearer tokens are sent only to the GitHub API origin; browser/release links
/// are accepted only from the public GitHub web origin.
/// </summary>
internal static class GitHubUrlPolicy
{
    internal static bool IsTrustedApiAssetUrl(string? value)
    {
        if (!TryHttpsUri(value, "api.github.com", out var uri)) return false;
        var path = uri.AbsolutePath;
        return path.StartsWith("/repos/", StringComparison.Ordinal)
            && path.Contains("/releases/assets/", StringComparison.Ordinal);
    }

    internal static bool IsTrustedWebUrl(string? value) =>
        TryHttpsUri(value, "github.com", out _);

    private static bool TryHttpsUri(string? value, string expectedHost, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed is null)
            return false;

        uri = parsed;
        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.IdnHost, expectedHost, StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo);
    }
}
