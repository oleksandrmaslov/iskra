using Iskra.Core;

namespace Iskra.Application;

/// <summary>
/// UI-neutral stages surfaced to WPF, Avalonia, and future clients. Frontends
/// decide how to present them; the application layer owns their ordering.
/// </summary>
public enum FlashWorkflowStage
{
    ReservingBatch,
    AcquiringFirmware,
    ValidatingFirmware,
    Flashing,
}

public sealed record FlashWorkflowProgress(FlashWorkflowStage Stage);

public enum FlashWorkflowStatus
{
    Blocked,
    Failed,
    Passed,
}

/// <summary>
/// Immutable snapshot of one operator request. Product and release are resolved
/// again from the trusted catalog so a frontend cannot supply detached metadata.
/// </summary>
public sealed record FlashWorkflowRequest(
    Catalog Catalog,
    string? CatalogDirectory,
    string ProductId,
    string? FirmwareVersion,
    AppSettings Settings,
    string? Operator,
    string? EnteredBatchId,
    string? GdbPath,
    string? Port,
    string? ProbeSerial);

public sealed record FlashWorkflowResult(
    FlashWorkflowStatus Status,
    FlashOutcome Outcome,
    Product? Product,
    FirmwareRelease? Release,
    string EffectiveBatchId,
    string? FirmwarePath,
    bool AttemptLogged)
{
    public bool IsBlocked => Status == FlashWorkflowStatus.Blocked;
    public bool IsPass => Status == FlashWorkflowStatus.Passed;
}

/// <summary>
/// Platform-specific remote firmware acquisition. The WPF implementation uses
/// the Windows token store; future Avalonia implementations can use Keychain or
/// libsecret without adding those OS dependencies to this project.
/// </summary>
public interface IRemoteFirmwareProvider
{
    Task<string> AcquireAsync(FirmwareRelease release, CancellationToken cancellationToken);
}

public interface IGdbProcessFactory
{
    GdbProcess Create(string gdbPath);
}

public sealed class GdbProcessFactory : IGdbProcessFactory
{
    public GdbProcess Create(string gdbPath) => new(gdbPath);
}

/// <summary>
/// Runs one complete, fail-closed flash transaction. It deliberately contains
/// no WPF or Avalonia types so both supported frontends can share the same
/// safety gates and durable logging behavior.
/// </summary>
public sealed class FlashWorkflow
{
    private readonly IRemoteFirmwareProvider? _remoteFirmwareProvider;
    private readonly IGdbProcessFactory _gdbProcessFactory;

    public FlashWorkflow(
        IRemoteFirmwareProvider? remoteFirmwareProvider = null,
        IGdbProcessFactory? gdbProcessFactory = null)
    {
        _remoteFirmwareProvider = remoteFirmwareProvider;
        _gdbProcessFactory = gdbProcessFactory ?? new GdbProcessFactory();
    }

    public async Task<FlashWorkflowResult> ExecuteAsync(
        FlashWorkflowRequest request,
        IProgress<FlashWorkflowProgress>? progress = null,
        Action<GdbLine>? onGdbLine = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Catalog);
        ArgumentNullException.ThrowIfNull(request.Settings);

        var product = request.Catalog.FindProduct(request.ProductId);
        if (product is null)
            return Blocked("E_PRODUCT_NOT_FOUND", $"product '{request.ProductId}' is not present in the catalog");

        var release = string.IsNullOrWhiteSpace(request.FirmwareVersion)
            ? product.Default()
            : product.FindRelease(request.FirmwareVersion);
        if (release is null)
            return Blocked("E_RELEASE_NOT_FOUND",
                $"release '{request.FirmwareVersion}' is not present for product '{product.ProductId}'",
                product);

        var op = request.Operator?.Trim() ?? string.Empty;
        if (op.Length == 0)
            return Blocked("E_OPERATOR_REQUIRED", "operator is required", product, release);

        var batchPolicy = BatchPolicy.Resolve(request.Settings, request.EnteredBatchId);
        if (!batchPolicy.IsValid)
            return Blocked(batchPolicy.ErrorCode!, "batch ID is required when batch mode is enabled", product, release);

        var batch = batchPolicy.EffectiveBatchId;
        if (string.IsNullOrWhiteSpace(request.GdbPath))
            return Blocked("E_GDB_NOT_FOUND", "arm-none-eabi-gdb was not found", product, release, batch);
        if (string.IsNullOrWhiteSpace(request.Port))
            return Blocked("E_PROBE_NOT_FOUND", "exactly one Black Magic Probe is required", product, release, batch);

        var revocation = request.Catalog.FindRevocation(product.ProductId, release.Version);
        if (revocation is not null)
        {
            var message = string.IsNullOrWhiteSpace(revocation.Reason)
                ? $"{product.ProductId} v{release.Version} revoked in catalog"
                : $"{product.ProductId} v{release.Version} revoked: {revocation.Reason}";
            return FailureWithLog(request, product, release, batch, "E_RELEASE_REVOKED", message);
        }

        var databasePath = ResolveDatabasePath(request.Settings);
        if (batchPolicy.ShouldReserve)
        {
            progress?.Report(new FlashWorkflowProgress(FlashWorkflowStage.ReservingBatch));
            try
            {
                using var store = new SqliteLogStore(databasePath);
                var requested = new BatchLockDescriptor(
                    product.ProductId,
                    release.Version,
                    release.ElfSha256,
                    product.Target.BmpMatch,
                    product.Target.FlashKb);
                var reservation = store.ReserveBatchLock(batch, requested);
                if (!reservation.IsAccepted)
                {
                    var locked = reservation.Lock;
                    var message = $"locked to {locked.ProductId} v{locked.FirmwareVersion} "
                        + $"sha256={ShortSha(locked.FirmwareSha256)}, attempted "
                        + $"{product.ProductId} v{release.Version} sha256={ShortSha(release.ElfSha256)}";
                    return FailureWithLog(request, product, release, batch, "E_BATCH_LOCKED", message);
                }
            }
            catch (Exception ex)
            {
                return Failed("E_BATCH_LOCK_CHECK_FAILED", ex.Message, product, release, batch);
            }
        }

        progress?.Report(new FlashWorkflowProgress(FlashWorkflowStage.AcquiringFirmware));
        string firmwarePath;
        try
        {
            firmwarePath = await AcquireFirmwareAsync(
                request.CatalogDirectory,
                release,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (NotSignedInException)
        {
            return FailureWithLog(request, product, release, batch,
                "E_NOT_SIGNED_IN", "remote firmware requires GitHub sign-in");
        }
        catch (RefreshTokenExpiredException)
        {
            return FailureWithLog(request, product, release, batch,
                "E_AUTH_EXPIRED", "GitHub refresh token expired");
        }
        catch (GitHubAssetNotFoundException ex)
        {
            return FailureWithLog(request, product, release, batch,
                "E_ASSET_NOT_FOUND", ex.Message);
        }
        catch (Exception ex)
        {
            var code = release.IsRemote ? "E_FW_DOWNLOAD_FAILED" : "E_FW_NOT_FOUND";
            return FailureWithLog(request, product, release, batch, code, ex.Message);
        }

        progress?.Report(new FlashWorkflowProgress(FlashWorkflowStage.ValidatingFirmware));
        var preflight = FirmwarePreflight.Check(firmwarePath, release.FirmwareKind);
        if (preflight != FirmwarePreflight.CheckResult.Ok)
        {
            var kind = FirmwarePreflight.DisplayName(release.FirmwareKind);
            var (code, message) = preflight switch
            {
                FirmwarePreflight.CheckResult.NotFound =>
                    ("E_FW_NOT_FOUND", $"firmware file not found: {firmwarePath}"),
                FirmwarePreflight.CheckResult.IoError =>
                    ("E_FW_READ_FAILED", $"firmware file could not be read: {firmwarePath}"),
                _ => ("E_FW_BAD_FORMAT", $"file is not a valid {kind} image: {firmwarePath}"),
            };
            return FailureWithLog(request, product, release, batch, code, message, firmwarePath);
        }

        string computedHash;
        try
        {
            computedHash = FirmwareIntegrity.ComputeSha256Hex(firmwarePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FailureWithLog(request, product, release, batch,
                "E_FW_READ_FAILED", ex.Message, firmwarePath);
        }

        if (!FirmwareIntegrity.HashesMatch(computedHash, release.ElfSha256))
        {
            var message = $"computed {computedHash}, expected {release.ElfSha256.ToLowerInvariant()}";
            return FailureWithLog(request, product, release, batch,
                "E_FW_HASH_MISMATCH", message, firmwarePath);
        }

        var flash = EffectiveFlashSettings(request.Settings, product);
        var options = new FlashOptions(
            ElfPath: firmwarePath,
            Port: request.Port,
            Power: flash.Power,
            BmpFrequencyHz: flash.FrequencyHz,
            ConnectUnderReset: flash.ConnectReset,
            Product: product.ProductId,
            Operator: op,
            Batch: batch,
            StationId: request.Settings.StationId,
            TargetBmpMatch: product.Target.BmpMatch,
            TargetFlashKb: product.Target.FlashKb,
            FirmwareVersion: release.Version,
            FirmwareSha256: release.ElfSha256,
            GdbPath: request.GdbPath,
            DbPath: request.Settings.DbPath,
            FirmwareKind: release.FirmwareKind,
            TimeoutSeconds: flash.TimeoutSeconds);

        progress?.Report(new FlashWorkflowProgress(FlashWorkflowStage.Flashing));
        try
        {
            var gdb = _gdbProcessFactory.Create(request.GdbPath);
            var outcome = await FlashStateMachine.RunAsync(
                gdb,
                options,
                timeout: TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)),
                onLine: onGdbLine,
                ct: cancellationToken).ConfigureAwait(false);
            var logged = TryLogAttempt(request, product, release, batch, outcome);
            return new FlashWorkflowResult(
                outcome.IsPass ? FlashWorkflowStatus.Passed : FlashWorkflowStatus.Failed,
                outcome,
                product,
                release,
                batch,
                firmwarePath,
                logged);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failed("E_INTERNAL", ex.Message, product, release, batch, firmwarePath);
        }
    }

    public static string ResolveDatabasePath(AppSettings settings)
        => ApplicationPaths.ResolveDatabasePath(settings, ensureDirectory: true);

    private async Task<string> AcquireFirmwareAsync(
        string? catalogDirectory,
        FirmwareRelease release,
        CancellationToken cancellationToken)
    {
        if (release.IsRemote)
        {
            if (_remoteFirmwareProvider is null)
                throw new InvalidOperationException("remote firmware provider is not configured");
            return await _remoteFirmwareProvider
                .AcquireAsync(release, cancellationToken)
                .ConfigureAwait(false);
        }

        if (Path.IsPathRooted(release.ElfFilename)) return release.ElfFilename;
        if (string.IsNullOrWhiteSpace(catalogDirectory))
            throw new FileNotFoundException(
                "catalog directory is unavailable for relative firmware path",
                release.ElfFilename);
        return Path.Combine(catalogDirectory, release.ElfFilename);
    }

    private static FlashWorkflowResult FailureWithLog(
        FlashWorkflowRequest request,
        Product product,
        FirmwareRelease release,
        string batch,
        string code,
        string message,
        string? firmwarePath = null)
    {
        var outcome = FailOutcome(code, message);
        var logged = TryLogAttempt(request, product, release, batch, outcome);
        return new FlashWorkflowResult(
            FlashWorkflowStatus.Failed,
            outcome,
            product,
            release,
            batch,
            firmwarePath,
            logged);
    }

    private static FlashWorkflowResult Blocked(
        string code,
        string message,
        Product? product = null,
        FirmwareRelease? release = null,
        string batch = "") =>
        new(FlashWorkflowStatus.Blocked, FailOutcome(code, message), product, release, batch, null, false);

    private static FlashWorkflowResult Failed(
        string code,
        string message,
        Product? product = null,
        FirmwareRelease? release = null,
        string batch = "",
        string? firmwarePath = null) =>
        new(FlashWorkflowStatus.Failed, FailOutcome(code, message), product, release, batch, firmwarePath, false);

    private static FlashOutcome FailOutcome(string code, string message) =>
        new(FlashResult.Fail, code, message, null, TimeSpan.Zero, string.Empty);

    private static bool TryLogAttempt(
        FlashWorkflowRequest request,
        Product product,
        FirmwareRelease release,
        string batch,
        FlashOutcome outcome)
    {
        try
        {
            var flash = EffectiveFlashSettings(request.Settings, product);
            using var store = new SqliteLogStore(ResolveDatabasePath(request.Settings));
            store.Append(new FlashAttemptRecord(
                TsUtc: DateTime.UtcNow,
                Operator: request.Operator?.Trim() ?? string.Empty,
                StationId: request.Settings.StationId,
                BatchId: batch,
                ProductId: product.ProductId,
                FirmwareVersion: release.Version,
                FirmwareSha256: release.ElfSha256,
                TargetBmpMatch: product.Target.BmpMatch,
                TargetDetected: outcome.DetectedTarget,
                TargetFlashKb: product.Target.FlashKb,
                ComPort: request.Port ?? string.Empty,
                ProbeSerial: request.ProbeSerial,
                Power: flash.Power,
                ConnectRst: flash.ConnectReset,
                BmpFrequencyHz: flash.FrequencyHz,
                Result: outcome.Result,
                ErrorCode: outcome.ErrorCode,
                ErrorMessage: outcome.ErrorMessage,
                DurationMs: (long)outcome.Duration.TotalMilliseconds,
                GdbTail: string.IsNullOrEmpty(outcome.GdbTail) ? null : outcome.GdbTail),
                // FlashWorkflow performs the only reservation before firmware
                // acquisition. Refused pre-reservation attempts (notably a
                // revoked release) must never create a lock as a logging side
                // effect; accepted batch requests are already durable here.
                reserveBatchLock: false);
            return true;
        }
        catch
        {
            // A failed audit write must not hide the primary flash outcome or
            // crash a frontend. The result exposes AttemptLogged for telemetry.
            return false;
        }
    }

    private static (PowerMode Power, int FrequencyHz, bool ConnectReset, int TimeoutSeconds)
        EffectiveFlashSettings(AppSettings settings, Product product) =>
        (
            product.Target.PowerMode ?? settings.Power,
            product.Target.FrequencyHz ?? settings.BmpFrequencyHz,
            product.Target.ConnectReset ?? settings.ConnectUnderReset,
            product.Target.TimeoutSeconds ?? settings.TimeoutSeconds
        );

    private static string ShortSha(string value) =>
        value.Length <= 12 ? value : value[..12];
}
