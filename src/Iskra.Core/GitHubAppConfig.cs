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

    /// <summary>
    /// Sprint 5: GitHub App that the LogShipper uses to push per-station
    /// JSONL files into the iskra-logs repo. Distinct from the user-facing
    /// Device Flow app above: it's a separate App, installed only on
    /// iskra-logs with contents:write, whose private key ships with the
    /// station MSI in %PROGRAMDATA%\Iskra\station-app.pem.
    ///
    /// <para>
    /// Both ids are empty until the App is registered + installed. Until
    /// then, the log shipper stays dormant.
    /// </para>
    ///
    /// <para>To wire a fresh log-shipper app:</para>
    /// <list type="number">
    ///   <item>Register a GitHub App: Repository contents = Read &amp; write,
    ///     Metadata = Read-only, no other permissions, no webhook.</item>
    ///   <item>Install on iskra-logs only (scope: single repo).</item>
    ///   <item>Generate a private key (download .pem). Distribute via the
    ///     MSI to <c>%PROGRAMDATA%\Iskra\station-app.pem</c>.</item>
    ///   <item>Paste the App ID and Installation ID below.</item>
    /// </list>
    /// </summary>
    public const string LogShipperAppId          = "";
    public const string LogShipperInstallationId = "";

    public static bool IsLogShipperConfigured =>
        !string.IsNullOrWhiteSpace(LogShipperAppId) &&
        !string.IsNullOrWhiteSpace(LogShipperInstallationId);

    /// <summary>
    /// Hard-locked logs repo coordinates. Mirrors the
    /// <c>CatalogTrust.OfficialCatalogSource</c> pattern: the LogShipper
    /// only writes here, regardless of what settings.json says.
    /// </summary>
    public const string LogsRepoOwner = "oleksandrmaslov";
    public const string LogsRepoName  = "iskra-logs";
}
