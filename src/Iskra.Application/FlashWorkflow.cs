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
    CatalogActivationPermit CatalogPermit,
    string? CatalogDirectory,
    string ProductId,
    string? FirmwareVersion,
    AppSettings Settings,
    string? Operator,
    string? EnteredBatchId,
    string? GdbPath,
    string? Port,
    string? ProbeSerial)
{
    public Catalog Catalog => CatalogPermit.Catalog;
}

public sealed record FlashWorkflowResult(
    FlashWorkflowStatus Status,
    FlashOutcome Outcome,
    Product? Product,
    FirmwareRelease? Release,
    string EffectiveBatchId,
    string? FirmwarePath,
    bool AttemptLogged,
    long? AttemptId = null)
{
    public bool IsBlocked => Status == FlashWorkflowStatus.Blocked;
    public bool IsPass => Status == FlashWorkflowStatus.Passed;
}

/// <summary>
/// Platform-specific remote firmware acquisition. WPF keeps its Windows DPAPI
/// adapter; Avalonia composes the shared platform store factory for DPAPI,
/// Linux Secret Service, or macOS Keychain without adding UI dependencies here.
/// </summary>
public interface IRemoteFirmwareProvider
{
    Task<string> AcquireAsync(FirmwareRelease release, CancellationToken cancellationToken);
}

internal interface IGdbProcessFactory
{
    GdbProcess Create(string gdbPath);
}

internal sealed class GdbProcessFactory : IGdbProcessFactory
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
    private readonly bool _enforceGdbTrust;

    public FlashWorkflow(IRemoteFirmwareProvider? remoteFirmwareProvider = null)
        : this(remoteFirmwareProvider, gdbProcessFactory: null)
    {
    }

    internal FlashWorkflow(
        IRemoteFirmwareProvider? remoteFirmwareProvider = null,
        IGdbProcessFactory? gdbProcessFactory = null)
    {
        _remoteFirmwareProvider = remoteFirmwareProvider;
        _gdbProcessFactory = gdbProcessFactory ?? new GdbProcessFactory();
        _enforceGdbTrust = gdbProcessFactory is null;
    }

    public async Task<FlashWorkflowResult> ExecuteAsync(
        FlashWorkflowRequest request,
        IProgress<FlashWorkflowProgress>? progress = null,
        Action<GdbLine>? onGdbLine = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.CatalogPermit);
        ArgumentNullException.ThrowIfNull(request.Settings);

        // AppSettings is mutable for UI binding. Freeze one validated clone at
        // the application boundary so a concurrent settings save cannot change
        // station identity or policy part-way through an attempt.
        try
        {
            var settingsSnapshot = request.Settings.Clone();
            AppSettingsStore.Validate(settingsSnapshot);
            request = request with { Settings = settingsSnapshot };
        }
        catch (Exception ex) when (ex is InvalidDataException
            or ArgumentException
            or NotSupportedException)
        {
            return Blocked("E_SETTINGS_INVALID", ex.Message);
        }

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
        var gdbPath = _enforceGdbTrust
            ? GdbDiscovery.Find(request.GdbPath)
            : request.GdbPath;
        if (string.IsNullOrWhiteSpace(gdbPath))
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

        string databasePath;
        try
        {
            databasePath = ResolveDatabasePath(request.Settings);
        }
        catch (Exception ex)
        {
            return Blocked("E_AUDIT_PATH_INVALID", ex.Message, product, release, batch);
        }
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

        var flash = EffectiveFlashSettings(request.Settings, product);
        long attemptId;
        try
        {
            using var store = new SqliteLogStore(databasePath);
            attemptId = store.BeginAttempt(new FlashAttemptStartRecord(
                TsUtc: DateTime.UtcNow,
                Operator: op,
                StationId: request.Settings.StationId,
                BatchId: batch,
                ProductId: product.ProductId,
                FirmwareVersion: release.Version,
                FirmwareSha256: release.ElfSha256,
                TargetBmpMatch: product.Target.BmpMatch,
                TargetFlashKb: product.Target.FlashKb,
                ComPort: request.Port,
                ProbeSerial: request.ProbeSerial,
                Power: flash.Power,
                ConnectRst: flash.ConnectReset,
                BmpFrequencyHz: flash.FrequencyHz),
                // Batch policy made the only allowed reservation above.
                reserveBatchLock: false);
        }
        catch (Exception ex)
        {
            return Failed(
                "E_AUDIT_START_FAILED",
                $"the audit STARTED row could not be persisted; no external work was performed: {ex.Message}",
                product,
                release,
                batch);
        }

        try
        {
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
            return FinalizeAttemptResult(databasePath, attemptId, product, release, batch,
                FailOutcome("E_NOT_SIGNED_IN", "remote firmware requires GitHub sign-in"));
        }
        catch (RefreshTokenExpiredException)
        {
            return FinalizeAttemptResult(databasePath, attemptId, product, release, batch,
                FailOutcome("E_AUTH_EXPIRED", "GitHub refresh token expired"));
        }
        catch (GitHubRepoAccessDeniedException ex)
        {
            // Distinct from a download failure: nothing about the network or
            // the artefact is wrong, this account simply is not approved for
            // the firmware repository.
            return FinalizeAttemptResult(databasePath, attemptId, product, release, batch,
                FailOutcome("E_NO_REPO_ACCESS", ex.Message));
        }
        catch (GitHubAssetNotFoundException ex)
        {
            return FinalizeAttemptResult(databasePath, attemptId, product, release, batch,
                FailOutcome("E_ASSET_NOT_FOUND", ex.Message));
        }
        catch (Exception ex)
        {
            var code = release.IsRemote ? "E_FW_DOWNLOAD_FAILED" : "E_FW_NOT_FOUND";
            return FinalizeAttemptResult(databasePath, attemptId, product, release, batch,
                FailOutcome(code, ex.Message));
        }

        progress?.Report(new FlashWorkflowProgress(FlashWorkflowStage.ValidatingFirmware));
        VerifiedFirmwareSnapshot snapshot;
        try
        {
            snapshot = await VerifiedFirmwareSnapshot.CreateAsync(
                firmwarePath,
                FirmwareIntegrity.IsValidSha256Hex(release.ElfSha256)
                    ? release.ElfSha256
                    : null,
                release.FirmwareKind,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FirmwareSnapshotException ex)
        {
            var code = ex.Status switch
            {
                FirmwareSnapshotStatus.NotFound => "E_FW_NOT_FOUND",
                FirmwareSnapshotStatus.ReadFailed => "E_FW_READ_FAILED",
                FirmwareSnapshotStatus.TooLarge => "E_FW_TOO_LARGE",
                FirmwareSnapshotStatus.HashMismatch => "E_FW_HASH_MISMATCH",
                _ => "E_FW_BAD_FORMAT",
            };
            return FinalizeAttemptResult(databasePath, attemptId, product, release, batch,
                FailOutcome(code, ex.Message), firmwarePath);
        }

        using (snapshot)
        {

        // Integrity proves the file is the one the catalog names. This proves
        // the named file can physically belong to the target in front of the
        // operator: BMP's bmp_match only identifies an MCU family, so a build
        // for a larger sibling part, or for a different memory map entirely,
        // reaches this point looking perfectly valid.
        var range = FirmwareRangeCheck.Validate(snapshot.Image, product.Target);
        if (!range.IsAcceptable)
        {
            var code = range.Status switch
            {
                FirmwareRangeStatus.TooLargeForFlash => "E_FW_TOO_LARGE",
                FirmwareRangeStatus.OutsideDeclaredMemory => "E_FW_ADDRESS_RANGE",
                _ => "E_FW_BAD_FORMAT",
            };
            return FinalizeAttemptResult(databasePath, attemptId, product, release, batch,
                FailOutcome(code, range.Diagnostic ?? range.Status.ToString()), firmwarePath);
        }

        var options = new FlashOptions(
            ElfPath: snapshot.SnapshotPath,
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
            GdbPath: gdbPath,
            DbPath: request.Settings.DbPath,
            FirmwareKind: release.FirmwareKind,
            TimeoutSeconds: flash.TimeoutSeconds,
            ProbeLockIdentity: ProbeDiscovery.ResolveProbeLockIdentity(
                request.Port,
                request.ProbeSerial),
            ExpectedLoadSections: release.FirmwareKind == FirmwareKind.Elf
                ? snapshot.Image.LoadSections
                : null);

        progress?.Report(new FlashWorkflowProgress(FlashWorkflowStage.Flashing));
        try
        {
            var gdb = _gdbProcessFactory.Create(gdbPath);
            var outcome = await FlashStateMachine.RunAsync(
                gdb,
                options,
                timeout: TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)),
                onLine: onGdbLine,
                ct: cancellationToken).ConfigureAwait(false);
            return FinalizeAttemptResult(
                databasePath, attemptId, product, release, batch, outcome, firmwarePath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FinalizeAttemptResult(
                databasePath,
                attemptId,
                product,
                release,
                batch,
                FailOutcome("E_INTERNAL", ex.Message),
                firmwarePath);
        }
        }
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            TryFinalizeCancellation(databasePath, attemptId, ex);
            throw;
        }
        catch (Exception ex)
        {
            return FinalizeAttemptResult(
                databasePath,
                attemptId,
                product,
                release,
                batch,
                FailOutcome("E_INTERNAL", ex.Message));
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
        var logged = TryLogAttempt(request, product, release, batch, outcome, out _);
        return new FlashWorkflowResult(
            FlashWorkflowStatus.Failed,
            outcome,
            product,
            release,
            batch,
            firmwarePath,
            logged);
    }

    private static FlashWorkflowResult FinalizeAttemptResult(
        string databasePath,
        long attemptId,
        Product product,
        FirmwareRelease release,
        string batch,
        FlashOutcome outcome,
        string? firmwarePath = null)
    {
        var logged = TryFinalizeAttempt(databasePath, attemptId, outcome, out var logError);
        if (!logged)
        {
            var priorOutcome = outcome.ErrorCode is null
                ? outcome.Result.ToString().ToUpperInvariant()
                : $"{outcome.Result.ToString().ToUpperInvariant()} {outcome.ErrorCode}";
            outcome = new FlashOutcome(
                FlashResult.Fail,
                "E_AUDIT_WRITE_FAILED",
                $"attempt ended with {priorOutcome}, but its terminal audit record could not be persisted: {logError}",
                outcome.DetectedTarget,
                outcome.Duration,
                outcome.GdbTail);
        }

        return new FlashWorkflowResult(
            outcome.IsPass ? FlashWorkflowStatus.Passed : FlashWorkflowStatus.Failed,
            outcome,
            product,
            release,
            batch,
            firmwarePath,
            logged,
            attemptId);
    }

    private static bool TryFinalizeAttempt(
        string databasePath,
        long attemptId,
        FlashOutcome outcome,
        out string? error)
    {
        try
        {
            using var store = new SqliteLogStore(databasePath);
            store.FinalizeAttempt(attemptId, new FlashAttemptFinalization(
                CompletedAtUtc: DateTime.UtcNow,
                TargetDetected: outcome.DetectedTarget,
                Result: outcome.Result,
                ErrorCode: outcome.ErrorCode,
                ErrorMessage: outcome.ErrorMessage,
                DurationMs: Math.Max(0, (long)outcome.Duration.TotalMilliseconds),
                GdbTail: string.IsNullOrEmpty(outcome.GdbTail) ? null : outcome.GdbTail));
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void TryFinalizeCancellation(
        string databasePath,
        long attemptId,
        OperationCanceledException cancellation)
    {
        var outcome = FailOutcome("E_CANCELLED", "the flash attempt was cancelled");
        if (!TryFinalizeAttempt(databasePath, attemptId, outcome, out var error))
            cancellation.Data["IskraAuditFinalizationError"] = error;
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
        FlashOutcome outcome,
        out string? error)
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
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
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
