using Iskra.Application;
using Iskra.Core;

namespace Iskra.Application.Tests;

/// <summary>
/// Covers the credential classification and cloud-log policy both desktop
/// frontends now render. Previously each frontend carried its own copy, which
/// is exactly how two UIs come to disagree about whether a station is signed in.
/// </summary>
public sealed class AuthAndCloudWorkflowTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
    private readonly List<string> _tempDirectories = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirectories)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    // ============================================================
    // Auth
    // ============================================================

    [Fact]
    public void A_platform_without_an_encrypted_store_never_offers_sign_in()
    {
        // Passing null is how Linux and macOS arrive here. Offering sign-in
        // would promise persistence the platform cannot deliver.
        var snapshot = new AuthWorkflow(store: null, clientConfigured: true).Evaluate(Now);

        Assert.Equal(AuthStatus.SecureStoreUnavailable, snapshot.Status);
        Assert.False(snapshot.CanSignIn);
        Assert.False(snapshot.CanSignOut);
        Assert.False(snapshot.CanFetchRemoteFirmware);
    }

    [Fact]
    public void A_build_without_a_client_id_never_offers_sign_in()
    {
        var snapshot = new AuthWorkflow(new FakeTokenStore(), clientConfigured: false).Evaluate(Now);

        Assert.Equal(AuthStatus.ClientNotConfigured, snapshot.Status);
        Assert.False(snapshot.CanSignIn);
    }

    [Fact]
    public void An_empty_store_reports_not_signed_in_and_offers_sign_in_only()
    {
        var snapshot = new AuthWorkflow(new FakeTokenStore(), clientConfigured: true).Evaluate(Now);

        Assert.Equal(AuthStatus.NotSignedIn, snapshot.Status);
        Assert.True(snapshot.CanSignIn);
        Assert.False(snapshot.CanSignOut);
        Assert.False(snapshot.CanFetchRemoteFirmware);
    }

    [Fact]
    public void A_corrupt_store_is_reported_rather_than_thrown()
    {
        // A damaged DPAPI blob must not take down a status-bar refresh.
        var store = new FakeTokenStore { LoadThrows = new TokenStoreException("bad blob") };

        var snapshot = new AuthWorkflow(store, clientConfigured: true).Evaluate(Now);

        Assert.Equal(AuthStatus.TokenStoreCorrupt, snapshot.Status);
        Assert.Equal("bad blob", snapshot.Diagnostic);
        Assert.True(snapshot.CanSignOut); // there is something worth deleting
    }

    [Fact]
    public void An_expired_refresh_token_is_a_session_expiry_not_a_valid_session()
    {
        var store = new FakeTokenStore
        {
            Tokens = Tokens(accessExpires: Now.AddHours(1), refreshExpires: Now.AddDays(-1)),
        };

        var snapshot = new AuthWorkflow(store, clientConfigured: true).Evaluate(Now);

        Assert.Equal(AuthStatus.SessionExpired, snapshot.Status);
        Assert.False(snapshot.CanFetchRemoteFirmware);
        Assert.True(snapshot.CanSignIn);
    }

    [Fact]
    public void A_stale_access_token_still_counts_as_signed_in_because_it_refreshes()
    {
        // Inside the skew window: the provider will refresh it on next use, so
        // the operator must not be told to sign in again.
        var store = new FakeTokenStore
        {
            Tokens = Tokens(accessExpires: Now.AddMinutes(2), refreshExpires: Now.AddDays(90)),
        };

        var snapshot = new AuthWorkflow(store, clientConfigured: true).Evaluate(Now);

        Assert.Equal(AuthStatus.SignedIn, snapshot.Status);
        Assert.False(snapshot.AccessTokenFresh);
        Assert.True(snapshot.CanFetchRemoteFirmware);
    }

    [Fact]
    public void A_fresh_access_token_reports_signed_in_and_fresh()
    {
        var store = new FakeTokenStore
        {
            Tokens = Tokens(accessExpires: Now.AddHours(6), refreshExpires: Now.AddDays(90)),
        };

        var snapshot = new AuthWorkflow(store, clientConfigured: true).Evaluate(Now);

        Assert.Equal(AuthStatus.SignedIn, snapshot.Status);
        Assert.True(snapshot.AccessTokenFresh);
    }

    [Fact]
    public void Signing_out_deletes_the_credentials_and_reports_the_new_state()
    {
        var store = new FakeTokenStore
        {
            Tokens = Tokens(accessExpires: Now.AddHours(6), refreshExpires: Now.AddDays(90)),
        };
        var workflow = new AuthWorkflow(store, clientConfigured: true);

        var after = workflow.SignOut();

        Assert.True(store.Deleted);
        Assert.Equal(AuthStatus.NotSignedIn, after.Status);
    }

    [Fact]
    public void A_failed_delete_is_surfaced_instead_of_pretending_to_be_signed_out()
    {
        var store = new FakeTokenStore
        {
            Tokens = Tokens(accessExpires: Now.AddHours(6), refreshExpires: Now.AddDays(90)),
            DeleteThrows = new IOException("file in use"),
        };

        var after = new AuthWorkflow(store, clientConfigured: true).SignOut();

        Assert.Equal(AuthStatus.TokenStoreCorrupt, after.Status);
        Assert.Equal("file in use", after.Diagnostic);
    }

    // ============================================================
    // Cloud log
    // ============================================================

    [Fact]
    public void Disabled_shipping_reports_disabled_without_touching_the_database()
    {
        var settings = SettingsWithDatabase(createFile: true);
        settings.LogShippingEnabled = false;

        var snapshot = new CloudLogWorkflow(isConfigured: () => true).Inspect(settings);

        Assert.Equal(CloudLogStatus.Disabled, snapshot.Status);
    }

    [Fact]
    public void An_unprovisioned_build_reports_not_configured()
    {
        var settings = SettingsWithDatabase(createFile: true);

        var snapshot = new CloudLogWorkflow(isConfigured: () => false).Inspect(settings);

        Assert.Equal(CloudLogStatus.NotConfigured, snapshot.Status);
    }

    [Fact]
    public void Inspecting_a_station_that_has_never_flashed_does_not_create_a_log()
    {
        // A status refresh must never be the reason a database appears.
        var settings = SettingsWithDatabase(createFile: false);

        var snapshot = new CloudLogWorkflow(isConfigured: () => true).Inspect(settings);

        Assert.Equal(CloudLogStatus.NoDatabase, snapshot.Status);
        Assert.False(File.Exists(snapshot.DatabasePath));
    }

    [Fact]
    public void Pending_rows_are_counted_from_the_real_store()
    {
        var settings = SettingsWithDatabase(createFile: false);
        using (var store = new SqliteLogStore(ApplicationPaths.ResolveDatabasePath(settings)))
        {
            store.Append(Attempt("station-1"), reserveBatchLock: false);
            store.Append(Attempt("station-1"), reserveBatchLock: false);
        }

        var snapshot = new CloudLogWorkflow(isConfigured: () => true).Inspect(settings);

        Assert.Equal(CloudLogStatus.Pending, snapshot.Status);
        Assert.Equal(2, snapshot.PendingRows);
    }

    [Fact]
    public async Task Shipping_refuses_when_the_station_key_is_absent()
    {
        var settings = SettingsWithDatabase(createFile: true);
        settings.LogShipperPrivateKeyPath = Path.Combine(Path.GetTempPath(), "absent-station.pem");
        var shipped = false;

        var result = await new CloudLogWorkflow(
            ship: (_, _, _) => { shipped = true; return Task.FromResult(new ShipReport(0, 0, 0, 0)); },
            isConfigured: () => true).ShipAsync(settings);

        Assert.Equal(CloudShipStatus.KeyMissing, result.Status);
        Assert.False(shipped);
    }

    [Fact]
    public async Task Shipping_reports_the_pushed_counts_on_success()
    {
        var settings = SettingsWithDatabase(createFile: true);
        var keyPath = WriteTempFile("station-app.pem", "-----BEGIN RSA PRIVATE KEY-----");
        settings.LogShipperPrivateKeyPath = keyPath;

        var result = await new CloudLogWorkflow(
            ship: (_, key, _) =>
            {
                Assert.Equal(keyPath, key);
                return Task.FromResult(new ShipReport(12, 1, 2, 3));
            },
            isConfigured: () => true).ShipAsync(settings);

        Assert.True(result.IsShipped);
        Assert.Equal(12, result.RowsPushed);
        Assert.Equal(3, result.RowsLeftover);
    }

    [Fact]
    public async Task An_edited_key_path_overrides_the_saved_one()
    {
        // The operator may point at a freshly delivered .pem before saving.
        var settings = SettingsWithDatabase(createFile: true);
        settings.LogShipperPrivateKeyPath = Path.Combine(Path.GetTempPath(), "absent-station.pem");
        var overridePath = WriteTempFile("override.pem", "-----BEGIN RSA PRIVATE KEY-----");

        var result = await new CloudLogWorkflow(
            ship: (_, key, _) =>
            {
                Assert.Equal(overridePath, key);
                return Task.FromResult(new ShipReport(1, 1, 0, 0));
            },
            isConfigured: () => true).ShipAsync(settings, overridePath);

        Assert.True(result.IsShipped);
    }

    [Fact]
    public async Task A_transport_failure_is_reported_and_never_thrown_at_the_frontend()
    {
        var settings = SettingsWithDatabase(createFile: true);
        settings.LogShipperPrivateKeyPath = WriteTempFile("station-app.pem", "key");

        var result = await new CloudLogWorkflow(
            ship: (_, _, _) => Task.FromException<ShipReport>(new LogShipperException("403 forbidden", 403)),
            isConfigured: () => true).ShipAsync(settings);

        Assert.Equal(CloudShipStatus.Failed, result.Status);
        Assert.Contains("403", result.Diagnostic);
    }

    // ============================================================
    // Fixture
    // ============================================================

    private AppSettings SettingsWithDatabase(bool createFile)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"iskra-cloud-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirectories.Add(dir);

        var settings = new AppSettings
        {
            StationId = "station-1",
            DbPath = Path.Combine(dir, "attempts.db"),
            LogShippingEnabled = true,
            LogShipperPrivateKeyPath = Path.Combine(dir, "station-app.pem"),
        };

        // A real store, not a placeholder file: ShipAsync opens it, so a text
        // stub would fail as a SQLite error and mask the case under test.
        if (createFile)
        {
            using var store = new SqliteLogStore(settings.DbPath);
        }

        return settings;
    }

    private string WriteTempFile(string name, string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"iskra-key-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirectories.Add(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static FlashAttemptRecord Attempt(string stationId) => new(
        TsUtc: DateTime.UtcNow,
        Operator: "operator-1",
        StationId: stationId,
        BatchId: "",
        ProductId: "ci-clop",
        FirmwareVersion: "1.0.0",
        FirmwareSha256: new string('a', 64),
        TargetBmpMatch: "PY32Fxxx",
        TargetDetected: "PY32Fxxx M0+",
        TargetFlashKb: 32,
        ComPort: "COM30",
        ProbeSerial: "BMP-001",
        Power: PowerMode.External,
        ConnectRst: false,
        BmpFrequencyHz: 1_000_000,
        Result: FlashResult.Pass,
        ErrorCode: null,
        ErrorMessage: null,
        DurationMs: 1200,
        GdbTail: null);

    private static StoredTokens Tokens(DateTime accessExpires, DateTime refreshExpires) =>
        new("access", "refresh", accessExpires, refreshExpires, null);

    private sealed class FakeTokenStore : ITokenStore
    {
        public StoredTokens? Tokens { get; set; }
        public Exception? LoadThrows { get; set; }
        public Exception? DeleteThrows { get; set; }
        public bool Deleted { get; private set; }

        public string Path => "in-memory";
        public bool Exists() => Tokens is not null;

        public StoredTokens? Load() => LoadThrows is not null ? throw LoadThrows : Tokens;

        public void Save(StoredTokens tokens) => Tokens = tokens;

        public void Delete()
        {
            if (DeleteThrows is not null) throw DeleteThrows;
            Deleted = true;
            Tokens = null;
        }
    }
}
