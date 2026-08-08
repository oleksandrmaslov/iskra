using System.Net.Http;
using Iskra.Core;

namespace Iskra.Application;

public enum CloudLogStatus
{
    /// <summary>Shipping is switched off in settings.</summary>
    Disabled,
    /// <summary>The build carries no log-shipper GitHub App identifiers.</summary>
    NotConfigured,
    /// <summary>No attempts have been recorded yet, so there is nothing to ship.</summary>
    NoDatabase,
    Synced,
    Pending,
    Failed,
}

public sealed record CloudLogSnapshot(
    CloudLogStatus Status,
    int PendingRows,
    string DatabasePath,
    string? Diagnostic);

public enum CloudShipStatus
{
    Shipped,
    Disabled,
    NotConfigured,
    /// <summary>The station private key is absent from the configured path.</summary>
    KeyMissing,
    NoDatabase,
    Failed,
}

public sealed record CloudShipResult(
    CloudShipStatus Status,
    int RowsPushed,
    int FilesCreated,
    int FilesUpdated,
    int RowsLeftover,
    string? KeyPath,
    string? Diagnostic)
{
    public bool IsShipped => Status == CloudShipStatus.Shipped;
}

/// <summary>
/// Cloud log-mirror status and manual flush, shared by WPF and Avalonia.
///
/// <para>Local SQLite remains the source of truth: a shipping failure never
/// affects a flash outcome, and rows stay marked unsynced for the next pass.
/// The repository is fixed to <see cref="GitHubAppConfig.LogsRepoOwner"/> /
/// <see cref="GitHubAppConfig.LogsRepoName"/> — it is not operator-settable.</para>
/// </summary>
public sealed class CloudLogWorkflow
{
    private readonly Func<SqliteLogStore, string, CancellationToken, Task<ShipReport>> _ship;
    private readonly Func<bool> _isConfigured;
    private readonly Func<string, bool> _fileExists;

    public CloudLogWorkflow(
        Func<SqliteLogStore, string, CancellationToken, Task<ShipReport>>? ship = null,
        Func<bool>? isConfigured = null,
        Func<string, bool>? fileExists = null)
    {
        _ship = ship ?? ShipWithGitHubAppAsync;
        _isConfigured = isConfigured ?? (() => GitHubAppConfig.IsLogShipperConfigured);
        _fileExists = fileExists ?? File.Exists;
    }

    /// <summary>
    /// Cheap read-only status for the station indicator. Deliberately does not
    /// create the database: an untouched station must not gain a log file just
    /// because a status bar refreshed.
    /// </summary>
    public CloudLogSnapshot Inspect(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var databasePath = ApplicationPaths.ResolveDatabasePath(settings);

        if (!settings.LogShippingEnabled)
            return new CloudLogSnapshot(CloudLogStatus.Disabled, 0, databasePath, null);
        if (!_isConfigured())
            return new CloudLogSnapshot(CloudLogStatus.NotConfigured, 0, databasePath, null);
        if (!_fileExists(databasePath))
            return new CloudLogSnapshot(CloudLogStatus.NoDatabase, 0, databasePath, null);

        try
        {
            using var store = new SqliteLogStore(databasePath);
            var pending = store.CountUnsynced();
            return new CloudLogSnapshot(
                pending == 0 ? CloudLogStatus.Synced : CloudLogStatus.Pending,
                pending,
                databasePath,
                null);
        }
        catch (Exception ex)
        {
            return new CloudLogSnapshot(CloudLogStatus.Failed, 0, databasePath, ex.Message);
        }
    }

    public async Task<CloudShipResult> ShipAsync(
        AppSettings settings,
        string? keyPathOverride = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.LogShippingEnabled) return Refused(CloudShipStatus.Disabled, null);
        if (!_isConfigured()) return Refused(CloudShipStatus.NotConfigured, null);

        var keyPath = string.IsNullOrWhiteSpace(keyPathOverride)
            ? settings.LogShipperPrivateKeyPath
            : keyPathOverride.Trim();
        if (string.IsNullOrWhiteSpace(keyPath) || !_fileExists(keyPath))
            return Refused(CloudShipStatus.KeyMissing, keyPath);

        var databasePath = ApplicationPaths.ResolveDatabasePath(settings);
        if (!_fileExists(databasePath)) return Refused(CloudShipStatus.NoDatabase, keyPath);

        try
        {
            using var store = new SqliteLogStore(databasePath);
            var report = await _ship(store, keyPath, cancellationToken).ConfigureAwait(false);
            return new CloudShipResult(
                CloudShipStatus.Shipped,
                report.RowsPushed,
                report.FilesCreated,
                report.FilesUpdated,
                report.RowsLeftover,
                keyPath,
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CloudShipResult(CloudShipStatus.Failed, 0, 0, 0, 0, keyPath, ex.Message);
        }
    }

    private static async Task<ShipReport> ShipWithGitHubAppAsync(
        SqliteLogStore store,
        string keyPath,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        var tokens = new GitHubAppInstallationTokenProvider(
            http,
            GitHubAppConfig.LogShipperAppId,
            GitHubAppConfig.LogShipperInstallationId,
            () => GitHubAppInstallationTokenProvider.LoadPemKey(keyPath));
        var shipper = new LogShipper(
            store,
            tokens,
            http,
            GitHubAppConfig.LogsRepoOwner,
            GitHubAppConfig.LogsRepoName);
        return await shipper.ShipPendingAsync(cancellationToken).ConfigureAwait(false);
    }

    private static CloudShipResult Refused(CloudShipStatus status, string? keyPath) =>
        new(status, 0, 0, 0, 0, keyPath, null);
}
