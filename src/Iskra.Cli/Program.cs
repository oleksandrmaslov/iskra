using System.Net.Http;
using System.Text;
using Iskra.Application;
using Iskra.Application.Localization;
using Iskra.Core;

Console.OutputEncoding = Encoding.UTF8;

AppSettings startupSettings;
try
{
    startupSettings = AppSettingsStore.Load();
}
catch (AppSettingsLoadException ex)
{
    var requestedLanguage = CliLanguage.Resolve(args, IskraLanguages.Ukrainian);
    System.Globalization.CultureInfo.CurrentUICulture =
        IskraLanguages.CultureFor(requestedLanguage.LanguageCode);
    Console.Error.WriteLine(CliText.Get("Settings.LoadFailed", ex.SettingsPath, ex.InnerException?.Message ?? ex.Message));
    return 2;
}

var language = CliLanguage.Resolve(args, startupSettings.LanguageCode);
System.Globalization.CultureInfo.CurrentUICulture = IskraLanguages.CultureFor(language.LanguageCode);
if (!language.Ok)
{
    Console.Error.WriteLine(CliText.Get("Language.Invalid"));
    return 2;
}
args = language.Args;

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    PrintUsage();
    return 0;
}

if (args.Contains("--doctor"))
    return Doctor(args);

if (args.Contains("--list-probes"))
    return ListProbes();

if (args.Contains("--gen-keypair"))
    return GenKeypair(args);

if (args.Contains("--sign-catalog"))
    return SignCatalog(args);

if (args.Contains("--generate-catalog"))
    return GenerateCatalog(args);

if (args.Contains("--login"))
    return await LoginAsync();

if (args.Contains("--logout"))
    return Logout();

if (args.Contains("--whoami"))
    return await WhoamiAsync();

if (args.Contains("--ship-logs-now"))
    return await ShipLogsNowAsync(args);

// Production-safe default: catalog files must be signed. Unsigned catalogs
// and sideload directories require an explicit lab-only override.
bool allowUnsigned = args.Contains("--allow-unsigned-catalog");
bool allowManualFlash = args.Contains("--allow-manual-flash");
bool labMode = CatalogTrust.IsUnsignedLabModeEnabled();
if ((allowUnsigned || allowManualFlash) && !labMode)
{
    Console.Error.WriteLine(CliText.Get("Lab.Locked", CatalogTrust.UnsignedLabModeEnvironmentVariable));
    return 2;
}
bool requireSigned = !allowUnsigned;
var hasCatalog = args.Contains("--catalog");
var hasSideload = args.Contains("--sideload-dir");
if (!hasCatalog && !hasSideload && !allowManualFlash)
{
    Console.Error.WriteLine(CliText.Get("Catalog.Required", CatalogTrust.UnsignedLabModeEnvironmentVariable));
    return 2;
}
args = args.Where(a => a is not "--require-signed-catalog"
    and not "--allow-unsigned-catalog"
    and not "--allow-manual-flash").ToArray();

if (hasSideload && requireSigned)
{
    Console.Error.WriteLine(CliText.Get("Catalog.SideloadUnsigned"));
    return 2;
}
if (hasCatalog && hasSideload)
{
    Console.Error.WriteLine(CliText.Get(
        "Catalog.Error",
        "use either --catalog or --sideload-dir, not both"));
    return 2;
}

int catIdx = Array.IndexOf(args, "--catalog");
string? verifiedCatalogPath = null;
string? resolvedCatalogDirectory = null;
CatalogActivationPermit? verifiedCatalogPermit = null;
CatalogFileVerificationResult? catalogVerification = null;
if (catIdx >= 0 && catIdx + 1 < args.Length)
{
    verifiedCatalogPath = args[catIdx + 1];
    catalogVerification = CatalogTrust.ReadAndVerifyCatalogFile(
        verifiedCatalogPath,
        requireSigned);
    var trust = catalogVerification.TrustResult;
    switch (trust)
    {
        case CatalogTrustResult.Verified:
            Console.WriteLine(CliText.Get("Catalog.SignatureVerified"));
            break;
        case CatalogTrustResult.UnsignedAllowed:
            Console.WriteLine(CliText.Get("Catalog.SignatureMissingLab"));
            break;
        case CatalogTrustResult.UnsignedRejected:
            Console.Error.WriteLine(CliText.Get("Catalog.UnsignedRejected"));
            return 2;
        case CatalogTrustResult.BadSignature:
            Console.Error.WriteLine(CliText.Get("Catalog.BadSignature"));
            return 2;
        case CatalogTrustResult.NoPublicKeyConfigured:
            Console.Error.WriteLine(CliText.Get("Catalog.NoPublicKey"));
            return 2;
        case CatalogTrustResult.IoError:
            Console.Error.WriteLine(CliText.Get("Catalog.SignatureReadFailed"));
            return 2;
    }
}

// A signed catalog is authoritative in production. Catalog-controlled CLI
// values may only win when the two-part lab/manual gate above is active.
ResolveResult resolution;
if (verifiedCatalogPath is not null)
{
    if (catalogVerification?.CatalogBytes is not { } catalogBytes)
    {
        Console.Error.WriteLine(CliText.Get("Catalog.SignatureReadFailed"));
        return 2;
    }

    Catalog catalog;
    try
    {
        // Resolve from the same bounded buffer whose signature was checked;
        // reopening --catalog here would reintroduce a TOCTOU window.
        catalog = CatalogJson.Parse(catalogBytes.Span);
    }
    catch (CatalogParseException ex)
    {
        Console.Error.WriteLine(CliText.Get("Catalog.Error", ex.Message));
        return 2;
    }

    if (catalogVerification.TrustResult == CatalogTrustResult.Verified)
    {
        try
        {
            CatalogJson.ValidateTrustedArtifactPaths(catalog);
        }
        catch (CatalogParseException ex)
        {
            Console.Error.WriteLine(CliText.Get("Catalog.Error", ex.Message));
            return 2;
        }

        var activation = CatalogActivationPolicy.ValidateAndAdvance(
            catalog.GeneratedAt,
            catalogSha256: CatalogActivationPolicy.ComputeCatalogSha256(catalogBytes.Span));
        if (!activation.IsAccepted)
        {
            Console.Error.WriteLine(CliText.Get(
                "Catalog.Error",
                activation.Diagnostic ?? activation.Status.ToString()));
            return 2;
        }
    }

    verifiedCatalogPermit = catalogVerification.TrustResult == CatalogTrustResult.Verified
        ? CatalogActivationPermit.FromVerifiedSnapshot(catalogVerification, verifiedCatalogPath)
        : CatalogActivationPermit.FromUnsignedLab(catalog, verifiedCatalogPath);

    resolvedCatalogDirectory = Path.GetDirectoryName(Path.GetFullPath(verifiedCatalogPath)) ?? "";
    resolution = CatalogResolver.ResolveWithCatalog(
        args,
        catalog,
        resolvedCatalogDirectory,
        allowCatalogOverrides: allowManualFlash);
}
else
{
    resolution = CatalogResolver.Resolve(args, allowCatalogOverrides: allowManualFlash);
}
if (!resolution.Ok)
{
    Console.Error.WriteLine(CliText.Get("Catalog.Error", resolution.Error));
    return 2;
}
args = resolution.ResolvedArgs!;
if (resolution.Product is not null && resolution.Release is not null)
{
    var p = resolution.Product;
    var r = resolution.Release;
    Console.WriteLine(CliText.Get("Catalog.Resolved", p.ProductId, r.Version,
        p.Target.BmpMatch, p.Target.FlashKb, FirmwarePreflight.DisplayName(r.FirmwareKind)));
}

// Keep remote acquisition inside FlashWorkflow so its durable STARTED record
// exists before network/cache work. FlashOptions only needs a syntactic path;
// the workflow ignores this placeholder for a release with elf_source.
var remoteAcquisitionPending = resolution.Release?.IsRemote == true && !args.Contains("--elf");
if (remoteAcquisitionPending)
    args = args.Concat(new[] { "--elf", resolution.Release!.ElfFilename }).ToArray();

bool dryRun = args.Contains("--dry-run");
args = args.Where(a => a != "--dry-run").ToArray();

ProbeInfo? selectedProbe = null;

// Auto-detect --port if omitted and exactly one BMP GDB interface is attached.
if (!args.Contains("--port"))
{
    var probes = ProbeDiscovery.FindGdbPorts();
    switch (probes.Count)
    {
        case 0:
            Console.Error.WriteLine(CliText.Get("Probe.NotFound"));
            Console.Error.WriteLine(CliText.Get("Probe.ConnectHint"));
            return 3;
        case 1:
            selectedProbe = probes[0];
            Console.WriteLine(CliText.Get("Probe.Detected", probes[0].PortName,
                probes[0].FriendlyName is not null ? $" ({probes[0].FriendlyName})" : ""));
            args = args.Concat(new[] { "--port", probes[0].PortName }).ToArray();
            break;
        default:
            Console.Error.WriteLine(CliText.Get("Probe.Multiple", probes.Count));
            foreach (var p in probes)
                Console.Error.WriteLine($"  {p.PortName}  {p.FriendlyName}");
            return 2;
    }
}

var opts = FlashOptions.Parse(args);
if (opts is null)
{
    PrintUsage();
    return 2;
}
selectedProbe ??= ProbeDiscovery.FindGdbPorts()
    .FirstOrDefault(p => string.Equals(p.PortName, opts.Port, StringComparison.OrdinalIgnoreCase));
var probeSerial = selectedProbe?.SerialNumber;
opts = opts with
{
    ProbeLockIdentity = ProbeDiscovery.ResolveProbeLockIdentity(opts.Port, probeSerial),
};

var firmwareKindName = FirmwarePreflight.DisplayName(opts.FirmwareKind);
var gdbExe = GdbDiscovery.Find(opts.GdbPath);
if (gdbExe is null)
{
    Console.Error.WriteLine(CliText.Get("Gdb.NotFound"));
    Console.Error.WriteLine(CliText.Get("Gdb.InstallHint"));
    return 3;
}

if (dryRun)
{
    var dryRunPath = opts.ElfPath;
    if (remoteAcquisitionPending)
    {
        var source = resolution.Release!.ElfSource!;
        Console.WriteLine($"GitHub: {source.Repo}@{source.Tag} → {source.Asset}");
        try
        {
            dryRunPath = await FetchRemoteFirmwareAsync(
                source,
                resolution.Release.ElfSha256,
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is NotSignedInException
            or RefreshTokenExpiredException
            or GitHubRepoAccessDeniedException
            or GitHubAssetNotFoundException
            or GitHubApiException
            or FirmwareCacheException
            or PlatformNotSupportedException)
        {
            Console.Error.WriteLine(CliText.Get("Firmware.DownloadError", ex.Message));
            return 5;
        }
    }

    switch (FirmwarePreflight.Check(dryRunPath, opts.FirmwareKind))
    {
        case FirmwarePreflight.CheckResult.NotFound:
            Console.Error.WriteLine(CliText.Get("Firmware.NotFound", firmwareKindName, dryRunPath));
            return 4;
        case FirmwarePreflight.CheckResult.InvalidFormat:
            Console.Error.WriteLine(CliText.Get("Firmware.BadFormat", firmwareKindName, dryRunPath));
            return 4;
        case FirmwarePreflight.CheckResult.IoError:
            Console.Error.WriteLine(CliText.Get("Firmware.ReadFailed", dryRunPath));
            return 4;
    }

    var computedSha = FirmwareIntegrity.ComputeSha256Hex(dryRunPath);
    var hashWasRequired = FirmwareIntegrity.IsValidSha256Hex(opts.FirmwareSha256);
    var hashVerified = hashWasRequired
        && FirmwareIntegrity.HashesMatch(computedSha, opts.FirmwareSha256);
    var rangeResult = FirmwareRangeCheck.Validate(
        dryRunPath,
        opts.FirmwareKind,
        opts.ToTargetDescriptor());

    Console.WriteLine(CliText.Get("DryRun.Header"));
    if (hashWasRequired)
    {
        Console.WriteLine($"{firmwareKindName} SHA-256: {computedSha}");
        Console.WriteLine(CliText.Get("DryRun.CatalogSha", opts.FirmwareSha256.ToLowerInvariant()));
        Console.WriteLine(hashVerified
            ? CliText.Get("DryRun.HashMatch")
            : CliText.Get("DryRun.HashMismatch"));
    }
    else
    {
        Console.WriteLine(CliText.Get("DryRun.HashSkipped"));
    }
    Console.WriteLine(rangeResult.IsAcceptable
        ? CliText.Get("DryRun.RangeOk", rangeResult.TotalBytes, opts.TargetFlashKb)
        : CliText.Get("DryRun.RangeFail", rangeResult.Diagnostic ?? rangeResult.Status.ToString()));
    Console.WriteLine(CliText.Get("DryRun.Executable", gdbExe));
    var processArgs = GdbCommandBuilder.BuildProcessArgs(
        opts.Port, opts.Power, opts.BmpFrequencyHz, opts.ConnectUnderReset, dryRunPath);
    foreach (var a in processArgs)
        Console.WriteLine($"  {a}");
    return (!rangeResult.IsAcceptable || (hashWasRequired && !hashVerified)) ? 4 : 0;
}

var target = new TargetDescriptor(
    BmpMatch: opts.TargetBmpMatch,
    PartNumber: resolution.Product?.Target.PartNumber ?? opts.Product,
    FlashKb: opts.TargetFlashKb,
    FrequencyHz: opts.BmpFrequencyHz,
    PowerMode: opts.Power,
    ConnectReset: opts.ConnectUnderReset,
    TimeoutSeconds: opts.TimeoutSeconds,
    FlashOrigin: opts.TargetFlashOrigin,
    RamOrigin: opts.TargetRamOrigin,
    RamKb: opts.TargetRamKb);

var effectiveRelease = remoteAcquisitionPending
    ? resolution.Release!
    : (resolution.Release ?? new FirmwareRelease(
        opts.FirmwareVersion,
        opts.ElfPath,
        opts.FirmwareSha256,
        null,
        DateTime.UnixEpoch,
        null,
        null,
        opts.FirmwareKind)) with
    {
        Version = opts.FirmwareVersion,
        ElfFilename = Path.GetFullPath(opts.ElfPath),
        ElfSha256 = opts.FirmwareSha256,
        ElfSource = null,
        FirmwareKind = opts.FirmwareKind,
    };
var product = new Product(
    opts.Product,
    resolution.Product?.DisplayName ?? opts.Product,
    target,
    [effectiveRelease],
    effectiveRelease.Version);
var workflowCatalog = new Catalog(1, DateTime.UtcNow, [product]);
var workflowCatalogPermit = verifiedCatalogPermit is not null && !allowManualFlash
    ? verifiedCatalogPermit
    : CatalogActivationPermit.FromUnsignedLab(
        workflowCatalog,
        verifiedCatalogPath,
        isSideload: hasSideload);
var workflowSettings = new AppSettings
{
    LanguageCode = language.LanguageCode,
    GdbPath = gdbExe,
    BmpFrequencyHz = opts.BmpFrequencyHz,
    Power = opts.Power,
    ConnectUnderReset = opts.ConnectUnderReset,
    TimeoutSeconds = opts.TimeoutSeconds,
    DbPath = opts.DbPath ?? Path.Combine(Environment.CurrentDirectory, "flash_log.db"),
    StationId = opts.StationId,
    BatchesEnabled = true,
};

IRemoteFirmwareProvider? remoteProvider = effectiveRelease.IsRemote
    ? new DelegateRemoteFirmwareProvider(async (release, cancellationToken) =>
    {
        var source = release.ElfSource
            ?? throw new InvalidOperationException("remote firmware source is missing");
        Console.WriteLine($"GitHub: {source.Repo}@{source.Tag} → {source.Asset}");
        var localPath = await FetchRemoteFirmwareAsync(
            source,
            release.ElfSha256,
            cancellationToken);
        Console.WriteLine(CliText.Get("Firmware.CacheHit", localPath));
        return localPath;
    })
    : null;

Console.WriteLine(CliText.Get("Flash.Summary", opts.Product, opts.FirmwareVersion, opts.TargetBmpMatch, opts.Port));
Console.WriteLine(CliText.Get("Flash.Operator", opts.Operator, opts.Batch, opts.StationId));
Console.WriteLine(CliText.Get("Flash.Running"));
Console.WriteLine();

var workflow = new FlashWorkflow(remoteProvider);
using var cliCancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cliCancellation.Cancel();
};
Console.CancelKeyPress += cancelHandler;
FlashWorkflowResult workflowResult;
try
{
    workflowResult = await workflow.ExecuteAsync(
        new FlashWorkflowRequest(
            workflowCatalogPermit,
            resolvedCatalogDirectory ?? Path.GetDirectoryName(Path.GetFullPath(opts.ElfPath)),
            product.ProductId,
            effectiveRelease.Version,
            workflowSettings,
            opts.Operator,
            opts.Batch,
            gdbExe,
            opts.Port,
            probeSerial),
        onGdbLine: line =>
        {
            if (line.Stream == GdbStream.Stderr || !string.IsNullOrWhiteSpace(line.Text))
                Console.WriteLine($"  gdb> {line.Text}");
        },
        cancellationToken: cliCancellation.Token);
}
catch (OperationCanceledException) when (cliCancellation.IsCancellationRequested)
{
    Console.Error.WriteLine(CliText.Get("Result.ErrorRaw", "E_CANCELLED", "flash attempt cancelled"));
    return 130;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

var outcome = workflowResult.Outcome;

Console.WriteLine();
if (outcome.IsPass)
{
    Console.WriteLine("============================================");
    Console.WriteLine(CliText.Get("Flash.Success", outcome.Duration.TotalMilliseconds));
    Console.WriteLine(CliText.Get("Flash.Target", outcome.DetectedTarget));
    Console.WriteLine("============================================");
}
else
{
    Console.WriteLine("============================================");
    Console.WriteLine(CliText.Get("Result.Error", outcome.ErrorCode));
    Console.WriteLine($"  {OperatorText.ErrorHint(outcome.ErrorCode)}");
    if (!string.IsNullOrEmpty(outcome.ErrorMessage))
        Console.WriteLine(CliText.Get("Result.Details", outcome.ErrorMessage));
    Console.WriteLine("============================================");
}

if (workflowResult.AttemptLogged && workflowResult.AttemptId is { } attemptId)
{
    string auditPath;
    try { auditPath = FlashWorkflow.ResolveDatabasePath(workflowSettings); }
    catch { auditPath = workflowSettings.DbPath ?? "flash_log.db"; }
    Console.WriteLine(CliText.Get("Flash.Logged", attemptId, auditPath));
}
else if (!workflowResult.AttemptLogged && !workflowResult.IsBlocked)
{
    Console.Error.WriteLine(CliText.Get(
        "Result.LogWarning",
        outcome.ErrorMessage ?? outcome.ErrorCode ?? "audit write failed"));
}

var authFailure = outcome.ErrorCode is "E_NOT_SIGNED_IN"
    or "E_AUTH_EXPIRED"
    or "E_NO_REPO_ACCESS"
    or "E_ASSET_NOT_FOUND"
    or "E_FW_DOWNLOAD_FAILED";
return outcome.IsPass ? 0 : authFailure ? 5 : 1;

static int GenKeypair(string[] args)
{
    int i = Array.IndexOf(args, "--gen-keypair");
    if (i + 1 >= args.Length)
    {
        Console.Error.WriteLine("--gen-keypair requires <out-dir>");
        return 2;
    }
    var dir = args[i + 1];
    Directory.CreateDirectory(dir);
    var kp = CatalogSignature.GenerateKeypair();
    var pubB64  = Convert.ToBase64String(kp.PublicKey);
    var privB64 = Convert.ToBase64String(kp.PrivateKey);
    var pubPath  = Path.Combine(dir, "catalog-key.pub");
    var privPath = Path.Combine(dir, "catalog-key.priv");
    if (File.Exists(pubPath) || File.Exists(privPath))
    {
        Console.Error.WriteLine("keypair already exists; refusing to overwrite catalog-key.pub/.priv");
        return 2;
    }
    try
    {
        WriteNewKeyFile(privPath, privB64, privateKey: true);
        WriteNewKeyFile(pubPath, pubB64, privateKey: false);
    }
    catch (Exception ex)
    {
        try { File.Delete(privPath); } catch { /* best effort */ }
        try { File.Delete(pubPath); } catch { /* best effort */ }
        Console.Error.WriteLine($"keypair write failed: {ex.Message}");
        return 2;
    }
    Console.WriteLine($"public key  → {pubPath}");
    Console.WriteLine($"private key → {privPath}");
    Console.WriteLine();
    Console.WriteLine("Public key (base64) — paste into CatalogTrust.EmbeddedPublicKeyBase64:");
    Console.WriteLine(pubB64);
    return 0;
}

static void WriteNewKeyFile(string path, string base64, bool privateKey)
{
    var options = new FileStreamOptions
    {
        Mode = FileMode.CreateNew,
        Access = FileAccess.Write,
        Share = FileShare.None,
        Options = FileOptions.WriteThrough,
    };
    if (!OperatingSystem.IsWindows() && privateKey)
        options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    using (var stream = new FileStream(path, options))
    {
        var bytes = Encoding.ASCII.GetBytes(base64);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    if (!privateKey) return;
    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return;
    }

    // DPAPI is not relevant for an offline signing key. Restrict the newly
    // created file to the current Windows identity and remove inherited ACLs.
    var identity = $"{Environment.UserDomainName}\\{Environment.UserName}:(F)";
    var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
    var icaclsPath = Path.Combine(systemDirectory, "icacls.exe");
    if (!File.Exists(icaclsPath))
        throw new IOException($"trusted icacls.exe was not found at {icaclsPath}");

    var psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName = icaclsPath,
        UseShellExecute = false,
        RedirectStandardError = true,
        RedirectStandardOutput = false,
        CreateNoWindow = true,
    };
    psi.ArgumentList.Add(path);
    psi.ArgumentList.Add("/inheritance:r");
    psi.ArgumentList.Add("/grant:r");
    psi.ArgumentList.Add(identity);
    using var process = System.Diagnostics.Process.Start(psi)
        ?? throw new IOException("could not start icacls.exe");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var errorTask = ReadBoundedProcessTextAsync(
        process.StandardError,
        maximumChars: 4_096,
        timeout.Token);
    try
    {
        process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
    }
    catch (OperationCanceledException ex)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        throw new IOException("icacls timed out while securing the private key", ex);
    }
    var error = errorTask.GetAwaiter().GetResult();
    if (process.ExitCode != 0)
        throw new IOException($"icacls failed: {error.Trim()}");
}

static async Task<string> ReadBoundedProcessTextAsync(
    TextReader reader,
    int maximumChars,
    CancellationToken cancellationToken)
{
    var retained = new StringBuilder(Math.Min(maximumChars, 512));
    var buffer = new char[512];
    while (true)
    {
        var read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read == 0) break;
        var keep = Math.Min(read, maximumChars - retained.Length);
        if (keep > 0) retained.Append(buffer, 0, keep);
    }
    return retained.ToString();
}

static int GenerateCatalog(string[] args)
{
    int from = Array.IndexOf(args, "--from-targets");
    if (from < 0 || from + 1 >= args.Length)
    {
        Console.Error.WriteLine("--generate-catalog requires --from-targets <dir>");
        return 2;
    }
    int outIdx = Array.IndexOf(args, "--out");
    if (outIdx < 0 || outIdx + 1 >= args.Length)
    {
        Console.Error.WriteLine("--generate-catalog requires --out <path>");
        return 2;
    }
    int ownerIdx = Array.IndexOf(args, "--owner");
    var owner = ownerIdx >= 0 && ownerIdx + 1 < args.Length ? args[ownerIdx + 1] : "oleksandrmaslov";

    var strictTagMatch = args.Contains("--strict-tag-match");

    int revokedIdx = Array.IndexOf(args, "--revoked");
    string? revokedPath = revokedIdx >= 0 && revokedIdx + 1 < args.Length
        ? args[revokedIdx + 1]
        : null;

    // Points every elf_source at one artefact repository instead of at each
    // product's source repository, so operators can be approved for the
    // flashable binaries without being approved for the firmware source.
    int distIdx = Array.IndexOf(args, "--dist-repo");
    string? distRepo = distIdx >= 0 && distIdx + 1 < args.Length
        ? args[distIdx + 1]
        : null;

    var targetsDir = args[from + 1];
    var outPath    = args[outIdx + 1];

    List<TargetSidecar> sidecars;
    try { sidecars = CatalogGenerator.ReadTargetsTree(targetsDir, strictTagMatch); }
    catch (CatalogGeneratorException ex) { Console.Error.WriteLine(ex.Message); return 2; }
    catch (TargetSidecarException ex)    { Console.Error.WriteLine(ex.Message); return 2; }

    IReadOnlyList<RevokedRelease> revoked;
    try { revoked = CatalogGenerator.ReadRevokedFile(revokedPath); }
    catch (CatalogGeneratorException ex) { Console.Error.WriteLine(ex.Message); return 2; }

    Catalog catalog;
    try { catalog = CatalogGenerator.Build(sidecars, owner, DateTime.UtcNow, revoked, distRepo); }
    catch (ArgumentException ex)         { Console.Error.WriteLine(ex.Message); return 2; }
    catch (CatalogGeneratorException ex) { Console.Error.WriteLine(ex.Message); return 2; }
    catch (CatalogParseException ex)     { Console.Error.WriteLine($"generated catalog failed validation: {ex.Message}"); return 2; }

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
    File.WriteAllBytes(outPath, CatalogJson.WriteUtf8(catalog));
    Console.WriteLine($"generated → {outPath}");
    Console.WriteLine($"  {catalog.Products.Count} product(s), {catalog.Products.Sum(p => p.Releases.Count)} release(s)");
    if (revoked.Count > 0)
        Console.WriteLine($"  · {revoked.Count} revoked release(s)");
    if (distRepo is not null)
        Console.WriteLine($"  · firmware served from {distRepo} (source repos not referenced)");
    foreach (var p in catalog.Products)
        Console.WriteLine($"  · {p.ProductId} → default v{p.DefaultRelease} ({p.Releases.Count} release(s))");
    return 0;
}

static int SignCatalog(string[] args)
{
    int i = Array.IndexOf(args, "--sign-catalog");
    if (i + 1 >= args.Length)
    {
        Console.Error.WriteLine("--sign-catalog requires <catalog-path>");
        return 2;
    }
    int j = Array.IndexOf(args, "--private-key");
    if (j < 0 || j + 1 >= args.Length)
    {
        Console.Error.WriteLine("--sign-catalog requires --private-key <path>");
        return 2;
    }
    var catalogPath = args[i + 1];
    var keyPath     = args[j + 1];
    if (!File.Exists(catalogPath))
    {
        Console.Error.WriteLine($"catalog not found: {catalogPath}");
        return 2;
    }
    if (!File.Exists(keyPath))
    {
        Console.Error.WriteLine($"private key not found: {keyPath}");
        return 2;
    }
    var priv = Convert.FromBase64String(
        BoundedFileReader.ReadUtf8String(keyPath, 64 * 1024).Trim());
    var bytes = BoundedFileReader.ReadAllBytes(catalogPath, CatalogJson.MaxCatalogBytes);
    var sig   = CatalogSignature.Sign(bytes, priv);
    var sigPath = CatalogTrust.SignaturePathFor(catalogPath);
    File.WriteAllText(sigPath, Convert.ToBase64String(sig));
    Console.WriteLine($"signed → {sigPath}");
    return 0;
}

static async Task<int> LoginAsync()
{
    var store = PlatformTokenStoreFactory.Create();
    if (store is null)
    {
        Console.Error.WriteLine(CliText.Get("Auth.StoreUnsupported"));
        return 5;
    }

    if (!GitHubAppConfig.IsConfigured)
    {
        Console.Error.WriteLine(CliText.Get("Auth.ClientMissing"));
        return 2;
    }

    using var http = new HttpClient();
    var flow = new GitHubDeviceFlow(http, GitHubAppConfig.ClientId);

    Console.WriteLine(CliText.Get("Auth.RequestCode"));
    DeviceCodeResponse code;
    try { code = await flow.RequestDeviceCodeAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(CliText.Get("Common.ErrorDetails", ex.Message)); return 5; }

    Console.WriteLine();
    Console.WriteLine("============================================");
    Console.WriteLine(CliText.Get("Auth.OpenBrowser", code.VerificationUri));
    Console.WriteLine(CliText.Get("Auth.EnterCode", code.UserCode));
    Console.WriteLine("============================================");
    Console.WriteLine();
    Console.WriteLine(CliText.Get("Auth.Waiting", code.ExpiresIn / 60));

    TokenResponse token;
    try { token = await flow.PollForTokenAsync(code); }
    catch (GitHubAuthException ex) when (ex.ErrorCode == "access_denied")
    {
        Console.Error.WriteLine(CliText.Get("Auth.Denied")); return 5;
    }
    catch (GitHubAuthException ex) when (ex.ErrorCode == "expired_token")
    {
        Console.Error.WriteLine(CliText.Get("Auth.CodeExpired")); return 5;
    }
    catch (GitHubAuthException ex)
    {
        Console.Error.WriteLine(CliText.Get("Auth.GitHubError", ex.Message)); return 5;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine(CliText.Get("Common.Cancelled")); return 5;
    }

    try
    {
        TokenStoreOperationLock.Run(store, () =>
        {
            store.Save(StoredTokens.From(token, DateTime.UtcNow));
            return true;
        });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(CliText.Get("Auth.SaveFailed", store.Path, ex.Message));
        Console.Error.WriteLine(CliText.Get("Auth.AdminHint"));
        return 5;
    }

    Console.WriteLine();
    Console.WriteLine(CliText.Get("Auth.Success", store.Path));
    Console.WriteLine(CliText.Get("Auth.AccessHours", token.ExpiresIn / 3600));
    Console.WriteLine(CliText.Get("Auth.RefreshDays", token.RefreshTokenExpiresIn / 86400));
    return 0;
}

static int Logout()
{
    var store = PlatformTokenStoreFactory.Create();
    if (store is null)
    {
        Console.Error.WriteLine(CliText.Get("Auth.StoreUnavailable"));
        return 5;
    }

    bool exists;
    try { exists = store.Exists(); }
    catch (Exception ex)
    {
        Console.Error.WriteLine(CliText.Get("Auth.DeleteFailed", store.Path, ex.Message));
        return 5;
    }

    if (!exists)
    {
        Console.WriteLine(CliText.Get("Auth.AlreadyLoggedOut"));
        return 0;
    }
    try
    {
        TokenStoreOperationLock.Run(store, () =>
        {
            store.Delete();
            return true;
        });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(CliText.Get("Auth.DeleteFailed", store.Path, ex.Message));
        return 5;
    }
    Console.WriteLine(CliText.Get("Auth.Deleted", store.Path));
    return 0;
}

static async Task<int> WhoamiAsync()
{
    var store = PlatformTokenStoreFactory.Create();
    if (store is null)
    {
        Console.Error.WriteLine(CliText.Get("Auth.StoreUnavailable"));
        return 5;
    }

    StoredTokens? stored;
    try { stored = store.Load(); }
    catch (TokenStoreException ex)
    {
        Console.Error.WriteLine(CliText.Get("Auth.StoreCorrupt", ex.Message));
        Console.Error.WriteLine(CliText.Get("Auth.Reauthenticate"));
        return 5;
    }

    if (stored is null)
    {
        Console.WriteLine(CliText.Get("Auth.NotSignedIn"));
        return 5;
    }

    var now = DateTime.UtcNow;
    Console.WriteLine(CliText.Get("Auth.File", store.Path));
    Console.WriteLine(CliText.Get("Auth.AccessUntil", stored.AccessTokenExpiresAtUtc, FormatFutureDuration(stored.AccessTokenExpiresAtUtc - now)));
    Console.WriteLine(CliText.Get("Auth.RefreshUntil", stored.RefreshTokenExpiresAtUtc, FormatFutureDuration(stored.RefreshTokenExpiresAtUtc - now)));

    if (!GitHubAppConfig.IsConfigured)
    {
        Console.WriteLine(CliText.Get("Auth.CheckSkipped"));
        return 0;
    }

    // Verify the access token still works server-side and show the login.
    using var http = new HttpClient();
    var flow = new GitHubDeviceFlow(http, GitHubAppConfig.ClientId);
    var provider = new AccessTokenProvider(store, flow);
    string accessToken;
    try { accessToken = await provider.GetFreshAccessTokenAsync(); }
    catch (NotSignedInException)        { Console.Error.WriteLine(CliText.Get("Auth.ParentheticalNotSignedIn")); return 5; }
    catch (RefreshTokenExpiredException) { Console.Error.WriteLine(CliText.Get("Auth.RefreshExpired")); return 5; }
    catch (Exception ex)                 { Console.Error.WriteLine(CliText.Get("Auth.RefreshFailed", ex.Message)); return 5; }

    using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
    req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    req.Headers.UserAgent.ParseAdd("Iskra");
    req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
    using var resp = await http.SendAsync(
        req, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
    if (!resp.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"GitHub /user → {(int)resp.StatusCode} {resp.ReasonPhrase}");
        return 5;
    }
    var body = await BoundedHttpContent.ReadUtf8StringAsync(
        resp.Content, 64 * 1024, CancellationToken.None);
    using var doc = System.Text.Json.JsonDocument.Parse(body);
    if (doc.RootElement.TryGetProperty("login", out var login))
        Console.WriteLine(CliText.Get("Auth.GitHubUser", login.GetString()));
    return 0;
}

static async Task<int> ShipLogsNowAsync(string[] args)
{
    var settings = AppSettingsStore.Load();
    if (!settings.LogShippingEnabled)
    {
        Console.WriteLine(CliText.Get("Logs.Disabled"));
        return 0;
    }

    if (!GitHubAppConfig.IsLogShipperConfigured)
    {
        Console.Error.WriteLine(CliText.Get("Logs.AppMissing"));
        Console.Error.WriteLine(CliText.Get("Logs.ConfigMissing"));
        return 5;
    }

    var keyPath = ArgValue(args, "--key") ?? settings.LogShipperPrivateKeyPath;
    if (!File.Exists(keyPath))
    {
        Console.Error.WriteLine(CliText.Get("Logs.KeyMissing", keyPath));
        Console.Error.WriteLine(CliText.Get("Logs.KeyHint"));
        return 5;
    }

    var dbPath = ArgValue(args, "--db-path")
        ?? settings.DbPath
        ?? Path.Combine(Environment.CurrentDirectory, "flash_log.db");
    if (!File.Exists(dbPath))
    {
        Console.WriteLine(CliText.Get("Logs.Empty", dbPath));
        return 0;
    }

    using var store = new SqliteLogStore(dbPath);
    int pending = store.CountUnsynced();
    if (pending == 0)
    {
        Console.WriteLine(CliText.Get("Logs.AllShipped"));
        return 0;
    }
    Console.WriteLine(CliText.Get("Logs.Pending", pending, dbPath));

    using var http = new HttpClient();
    var tokens = new GitHubAppInstallationTokenProvider(
        http,
        GitHubAppConfig.LogShipperAppId,
        GitHubAppConfig.LogShipperInstallationId,
        () => GitHubAppInstallationTokenProvider.LoadPemKey(keyPath));
    var shipper = new LogShipper(
        store, tokens, http,
        GitHubAppConfig.LogsRepoOwner,
        GitHubAppConfig.LogsRepoName);

    ShipReport report;
    try
    {
        report = await shipper.ShipPendingAsync();
    }
    catch (GitHubAppAuthException ex)
    {
        Console.Error.WriteLine(CliText.Get("Logs.AuthError", ex.Message));
        return 5;
    }
    catch (LogShipperException ex)
    {
        Console.Error.WriteLine(CliText.Get("Logs.UploadError", ex.Message));
        return 5;
    }

    Console.WriteLine(CliText.Get("Logs.Uploaded", report.RowsPushed, report.FilesCreated, report.FilesUpdated));
    if (report.RowsLeftover > 0)
        Console.WriteLine(CliText.Get("Logs.Leftover", report.RowsLeftover));
    return 0;
}

static async Task<string> FetchRemoteFirmwareAsync(
    GitHubReleaseRef src,
    string expectedSha,
    CancellationToken cancellationToken)
{
    var store = PlatformTokenStoreFactory.Create();
    if (store is null)
        throw new PlatformNotSupportedException(CliText.Get("Firmware.PrivateUnsupported"));

    using var http = new HttpClient();
    var flow = new GitHubDeviceFlow(http, GitHubAppConfig.ClientId);
    var provider = new AccessTokenProvider(store, flow);
    var api = new GitHubReleaseAssetClient(http);
    var cache = new FirmwareCache(api, provider.GetFreshAccessTokenAsync);
    return await cache.GetOrDownloadAsync(src, expectedSha, cancellationToken);
}

static string FormatFutureDuration(TimeSpan d)
{
    if (d.TotalSeconds <= 0) return CliText.Get("Duration.Expired");
    if (d.TotalDays >= 30)   return CliText.Get("Duration.Months", (int)(d.TotalDays / 30));
    if (d.TotalDays >= 1)    return CliText.Get("Duration.Days", d.Days, d.Hours);
    if (d.TotalHours >= 1)   return CliText.Get("Duration.Hours", d.Hours, d.Minutes);
    return CliText.Get("Duration.Minutes", d.Minutes);
}

static int ListProbes()
{
    var all = ProbeDiscovery.FindAll();
    if (all.Count == 0)
    {
        Console.WriteLine(CliText.Get("Probe.None"));
        Console.WriteLine(CliText.Get("Probe.SearchDetail"));
        return 0;
    }
    Console.WriteLine(CliText.Get("Probe.Interfaces", all.Count));
    foreach (var p in all)
    {
        var role = p.Interface switch
        {
            ProbeInterface.Gdb     => "GDB ",
            ProbeInterface.Uart    => "UART",
            _                      => "??? ",
        };
        var serial = string.IsNullOrWhiteSpace(p.SerialNumber) ? "" : $"  serial={p.SerialNumber}";
        Console.WriteLine($"  [{role}]  {p.PortName,-8}  {p.FriendlyName}{serial}");
    }
    Console.WriteLine();
    var gdb = ProbeDiscovery.FindGdbPorts();
    if (gdb.Count == 1)
        Console.WriteLine(CliText.Get("Probe.DefaultPort", gdb[0].PortName));
    return 0;
}

static int Doctor(string[] args)
{
    var failures = 0;
    var warnings = 0;

    void Pass(string name, string detail = "") =>
        WriteDoctorLine("PASS", name, detail);

    void Warn(string name, string detail = "")
    {
        warnings++;
        WriteDoctorLine("WARN", name, detail);
    }

    void Fail(string name, string detail = "")
    {
        failures++;
        WriteDoctorLine("FAIL", name, detail);
    }

    Console.WriteLine(CliText.Get("Doctor.Title"));
    Console.WriteLine("====================");

    Pass(CliText.Get("Doctor.OperatingSystem"), System.Runtime.InteropServices.RuntimeInformation.OSDescription);
    // The runtime identifier decides which update package and which OS adapters
    // apply, so a support report is ambiguous without it.
    Pass(CliText.Get("Doctor.Runtime"), CliText.Get(
        "Doctor.RuntimeDetail",
        System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
        System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription));

    var appDir = AppContext.BaseDirectory;
    var appFileName = OperatingSystem.IsWindows() ? "Iskra.exe" : "Iskra";
    var cliFileName = OperatingSystem.IsWindows() ? "Iskra.Cli.exe" : "Iskra.Cli";
    var appExe = Path.Combine(appDir, appFileName);
    var cliExe = Environment.ProcessPath ?? Path.Combine(appDir, cliFileName);

    if (File.Exists(cliExe))
        Pass(cliFileName, cliExe);
    else
        Warn(cliFileName, CliText.Get("Doctor.CliPathUnknown"));

    if (File.Exists(appExe))
        Pass(appFileName, appExe);
    else
        Warn(appFileName, CliText.Get("Doctor.GuiMissing"));

    var gdbPath = GdbDiscovery.Find(ArgValue(args, "--gdb-path"));
    if (gdbPath is null)
    {
        Fail("Arm GNU Toolchain", CliText.Get("Doctor.GdbMissing"));
    }
    else
    {
        Pass("Arm GNU Toolchain", gdbPath);
        try
        {
            var provenance = GdbDiscovery.Inspect(gdbPath);
            if (provenance.IsTrustedLocation)
                Pass("gdb provenance", provenance.CanonicalPath);
            else
                Warn("gdb provenance", "untrusted location accepted by an explicit lab-only override");
            Pass("gdb sha256", provenance.Sha256);
            if (provenance.VersionBanner is null)
                Warn("gdb version", CliText.Get("Doctor.GdbVersionUnknown"));
            else
                Pass("gdb version", provenance.VersionBanner);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            Warn("gdb provenance", ex.Message);
        }
    }

    var probes = ProbeDiscovery.FindGdbPorts();
    switch (probes.Count)
    {
        case 0:
            Fail("Black Magic Probe", CliText.Get("Doctor.ProbeMissing"));
            break;
        case 1:
            Pass("Black Magic Probe", $"{probes[0].PortName} {probes[0].FriendlyName}");
            break;
        default:
            Warn("Black Magic Probe", CliText.Get("Doctor.ProbeMultiple", probes.Count));
            foreach (var p in probes)
                Console.WriteLine($"       {p.PortName} {p.FriendlyName}");
            break;
    }

    var catalogPath = ArgValue(args, "--catalog") ?? FindDefaultCatalogPath();
    if (catalogPath is null)
    {
        Warn("Catalog", CliText.Get("Doctor.CatalogHint"));
    }
    else
    {
        if (!File.Exists(catalogPath))
        {
            Fail("Catalog", CliText.Get("Doctor.NotFound", catalogPath));
        }
        else
        {
            var verification = CatalogTrust.ReadAndVerifyCatalogFile(
                catalogPath,
                requireSigned: true);
            if (verification.CatalogBytes is { } catalogBytes)
            {
                try
                {
                    var catalog = CatalogJson.Parse(catalogBytes.Span);
                    Pass("Catalog JSON", CliText.Get("Doctor.Products", catalog.Products.Count, catalogPath));
                }
                catch (CatalogParseException ex)
                {
                    Fail("Catalog JSON", ex.Message);
                }
            }
            else
            {
                Fail("Catalog JSON", CliText.Get("Doctor.CatalogReadFailed"));
            }

            var trust = verification.TrustResult;
            switch (trust)
            {
                case CatalogTrustResult.Verified:
                    Pass("Catalog signature", CatalogTrust.SignaturePathFor(catalogPath));
                    break;
                case CatalogTrustResult.UnsignedRejected:
                    Fail("Catalog signature", CliText.Get("Doctor.NoSignature"));
                    break;
                case CatalogTrustResult.BadSignature:
                    Fail("Catalog signature", CliText.Get("Doctor.BadSignature"));
                    break;
                case CatalogTrustResult.NoPublicKeyConfigured:
                    Fail("Catalog signature", CliText.Get("Doctor.NoPublicKey"));
                    break;
                case CatalogTrustResult.IoError:
                    Fail("Catalog signature", CliText.Get("Doctor.CatalogReadFailed"));
                    break;
                case CatalogTrustResult.UnsignedAllowed:
                    Fail("Catalog signature", CliText.Get("Doctor.UnexpectedUnsigned"));
                    break;
            }
        }
    }

    var localAppData = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Iskra");
    var localAppDataLabel = OperatingSystem.IsWindows()
        ? "%LOCALAPPDATA%\\Iskra"
        : localAppData;
    if (CanWriteDirectory(localAppData, out var localError))
        Pass(localAppDataLabel, CliText.Get("Doctor.Writable"));
    else
        Fail(localAppDataLabel, localError ?? CliText.Get("Doctor.NotWritable"));

    // WPF/DPAPI intentionally uses machine-wide ProgramData. Unix credentials
    // live in the per-user Keychain/Secret Service, and a packaged station's
    // shared files are normally root-owned/read-only, so requiring write access
    // there would incorrectly fail a healthy locked-down station.
    if (OperatingSystem.IsWindows())
    {
        var programData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Iskra");
        if (CanWriteDirectory(programData, out var programDataError))
            Pass("%PROGRAMDATA%\\Iskra", CliText.Get("Doctor.Writable"));
        else
            Fail("%PROGRAMDATA%\\Iskra", programDataError ?? CliText.Get("Doctor.NotWritable"));
    }

    // Same classification the desktop frontends render, so a doctor report and
    // the Settings tab can never disagree about the session state.
    var authSnapshot = new AuthWorkflow(PlatformTokenStoreFactory.Create()).Evaluate();
    switch (authSnapshot.Status)
    {
        case AuthStatus.SecureStoreUnavailable:
            Warn("GitHub auth", CliText.Get("Doctor.SecureStoreMissing"));
            break;
        case AuthStatus.ClientNotConfigured:
            Warn("GitHub auth", CliText.Get("Doctor.AuthClientMissing"));
            break;
        case AuthStatus.TokenStoreCorrupt:
            Fail("GitHub auth", authSnapshot.Diagnostic ?? "");
            break;
        case AuthStatus.NotSignedIn:
            Warn("GitHub auth", CliText.Get("Doctor.NotSignedIn"));
            break;
        case AuthStatus.SessionExpired:
            Fail("GitHub auth", CliText.Get("Doctor.RefreshExpired"));
            break;
        default:
            Pass("GitHub auth", CliText.Get(
                "Doctor.SignedInUntil",
                authSnapshot.RefreshTokenExpiresAtUtc?.ToString("u") ?? "?"));
            break;
    }

    var cloud = new CloudLogWorkflow().Inspect(AppSettingsStore.Load());
    switch (cloud.Status)
    {
        case CloudLogStatus.Disabled:
            Warn("Cloud log", CliText.Get("Doctor.CloudDisabled"));
            break;
        case CloudLogStatus.NotConfigured:
            Warn("Cloud log", CliText.Get("Doctor.CloudUnconfigured"));
            break;
        case CloudLogStatus.NoDatabase:
            Pass("Cloud log", CliText.Get("Doctor.CloudNoDatabase"));
            break;
        case CloudLogStatus.Synced:
            Pass("Cloud log", CliText.Get("Doctor.CloudSynced"));
            break;
        case CloudLogStatus.Pending:
            Warn("Cloud log", CliText.Get("Doctor.CloudPending", cloud.PendingRows));
            break;
        default:
            Fail("Cloud log", cloud.Diagnostic ?? "");
            break;
    }

    Console.WriteLine();
    if (failures == 0)
    {
        Console.WriteLine(CliText.Get("Doctor.Pass", warnings));
        return 0;
    }

    Console.WriteLine(CliText.Get("Doctor.Fail", failures, warnings));
    return 1;
}

static void WriteDoctorLine(string status, string name, string detail)
{
    var line = $"[{status}] {name}";
    if (!string.IsNullOrWhiteSpace(detail))
        line += $" - {detail}";
    Console.WriteLine(line);
}

static string? FindDefaultCatalogPath()
{
    var candidates = new[]
    {
        Path.Combine(AppContext.BaseDirectory, "examples", "catalog.json"),
        Path.Combine(Environment.CurrentDirectory, "examples", "catalog.json"),
        Path.Combine(Environment.CurrentDirectory, "catalog.json"),
    };
    return candidates.FirstOrDefault(File.Exists);
}

static string? ArgValue(string[] args, string name)
{
    var idx = Array.IndexOf(args, name);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}

static bool CanWriteDirectory(string dir, out string? error)
{
    try
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $".iskra-write-test-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(path, "test");
        File.Delete(path);
        error = null;
        return true;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        error = ex.Message;
        return false;
    }
}

static void PrintUsage()
{
    Console.WriteLine(CliText.Get("Help"));
}

file sealed class DelegateRemoteFirmwareProvider(
    Func<FirmwareRelease, CancellationToken, Task<string>> acquire)
    : IRemoteFirmwareProvider
{
    public Task<string> AcquireAsync(
        FirmwareRelease release,
        CancellationToken cancellationToken) => acquire(release, cancellationToken);
}
