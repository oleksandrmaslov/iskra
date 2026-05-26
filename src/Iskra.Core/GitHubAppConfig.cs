namespace Iskra.Core;

/// <summary>
/// Static configuration for the GitHub App that backs Iskra's Device Flow
/// authentication. The Client ID is a public value (not a secret) — Device
/// Flow has no client secret. To wire a fresh app:
/// <list type="number">
///   <item>Register a GitHub App at https://github.com/settings/apps/new with
///     "Enable Device Flow" checked, Contents = Read-only, no webhook.</item>
///   <item>Install it on the firmware repo(s).</item>
///   <item>Paste the Client ID below.</item>
/// </list>
/// </summary>
public static class GitHubAppConfig
{
    /// <summary>
    /// Client ID from the registered GitHub App. Empty until provisioned —
    /// CLI / WPF surfaces guard on <see cref="IsConfigured"/>.
    /// </summary>
    public const string ClientId = "Iv23liyvPYbleFCY7D96";

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);
}
