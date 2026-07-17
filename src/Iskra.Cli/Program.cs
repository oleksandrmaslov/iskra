using System.Net.Http;
using System.Text;
using Iskra.Application.Localization;
using Iskra.Core;

Console.OutputEncoding = Encoding.UTF8;

var language = CliLanguage.Resolve(args, AppSettingsStore.Load().LanguageCode);
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

int catIdx = Array.IndexOf(args, "--catalog");
if (catIdx >= 0 && catIdx + 1 < args.Length)
{
    var catalogPath = args[catIdx + 1];
    var trust = CatalogTrust.VerifyCatalogFile(catalogPath, requireSigned);
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

var resolution = CatalogResolver.Resolve(args);
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

// Remote release + no explicit --elf → download from GitHub release asset
// into the local cache, verify SHA, then inject --elf with the cached path.
if (resolution.Release?.IsRemote == true && !args.Contains("--elf"))
{
    var src = resolution.Release.ElfSource!;
    var expectedSha = resolution.Release.ElfSha256;
    Console.WriteLine($"GitHub: {src.Repo}@{src.Tag} → {src.Asset}");
    try
    {
        var localPath = await FetchRemoteFirmwareAsync(src, expectedSha);
        Console.WriteLine(CliText.Get("Firmware.CacheHit", localPath));
        args = args.Concat(new[] { "--elf", localPath }).ToArray();
    }
    catch (NotSignedInException)
    {
        Console.Error.WriteLine(CliText.Get("Auth.Required"));
        return 5;
    }
    catch (RefreshTokenExpiredException)
    {
        Console.Error.WriteLine(CliText.Get("Auth.Expired"));
        return 5;
    }
    catch (GitHubAssetNotFoundException ex)
    {
        Console.Error.WriteLine(CliText.Get("Firmware.AssetMissing", ex.Message));
        return 5;
    }
    catch (GitHubApiException ex)
    {
        Console.Error.WriteLine(CliText.Get("GitHub.ApiError", ex.StatusCode, ex.Message));
        return 5;
    }
    catch (FirmwareCacheException ex)
    {
        Console.Error.WriteLine(CliText.Get("Firmware.DownloadError", ex.Message));
        return 5;
    }
    catch (PlatformNotSupportedException ex)
    {
        Console.Error.WriteLine(CliText.Get("Common.ErrorDetails", ex.Message));
        return 5;
    }
}

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

var firmwareKindName = FirmwarePreflight.DisplayName(opts.FirmwareKind);
switch (FirmwarePreflight.Check(opts.ElfPath, opts.FirmwareKind))
{
    case FirmwarePreflight.CheckResult.NotFound:
        Console.Error.WriteLine(CliText.Get("Firmware.NotFound", firmwareKindName, opts.ElfPath));
        return 4;
    case FirmwarePreflight.CheckResult.InvalidFormat:
        Console.Error.WriteLine(CliText.Get("Firmware.BadFormat", firmwareKindName, opts.ElfPath));
        return 4;
    case FirmwarePreflight.CheckResult.IoError:
        Console.Error.WriteLine(CliText.Get("Firmware.ReadFailed", opts.ElfPath));
        return 4;
}

var computedSha = FirmwareIntegrity.ComputeSha256Hex(opts.ElfPath);
bool hashVerified = false;
bool hashWasRequired = FirmwareIntegrity.IsValidSha256Hex(opts.FirmwareSha256);
if (hashWasRequired)
{
    hashVerified = FirmwareIntegrity.HashesMatch(computedSha, opts.FirmwareSha256);
}

var gdbExe = GdbDiscovery.Find(opts.GdbPath);
if (gdbExe is null)
{
    Console.Error.WriteLine(CliText.Get("Gdb.NotFound"));
    Console.Error.WriteLine(CliText.Get("Gdb.InstallHint"));
    return 3;
}

if (dryRun)
{
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
    Console.WriteLine(CliText.Get("DryRun.Executable", gdbExe));
    var processArgs = GdbCommandBuilder.BuildProcessArgs(
        opts.Port, opts.Power, opts.BmpFrequencyHz, opts.ConnectUnderReset, opts.ElfPath);
    foreach (var a in processArgs)
        Console.WriteLine($"  {a}");
    return 0;
}

if (hashWasRequired && !hashVerified)
{
    var hashFail = new FlashOutcome(
        Result:        FlashResult.Fail,
        ErrorCode:     "E_FW_HASH_MISMATCH",
        ErrorMessage:  $"computed {computedSha}, expected {opts.FirmwareSha256.ToLowerInvariant()}",
        DetectedTarget: null,
        Duration:      TimeSpan.Zero,
        GdbTail:       string.Empty);

    Console.WriteLine();
    Console.WriteLine("============================================");
    Console.WriteLine(CliText.Get("Result.Error", hashFail.ErrorCode));
    Console.WriteLine($"  {OperatorText.ErrorHint(hashFail.ErrorCode)}");
    Console.WriteLine(CliText.Get("Result.Details", hashFail.ErrorMessage));
    Console.WriteLine("============================================");

    var dbPath0 = opts.DbPath ?? Path.Combine(Environment.CurrentDirectory, "flash_log.db");
    try
    {
        using var log = new SqliteLogStore(dbPath0);
        log.Append(new FlashAttemptRecord(
            TsUtc:           DateTime.UtcNow,
            Operator:        opts.Operator,
            StationId:       opts.StationId,
            BatchId:         opts.Batch,
            ProductId:       opts.Product,
            FirmwareVersion: opts.FirmwareVersion,
            FirmwareSha256:  computedSha,
            TargetBmpMatch:  opts.TargetBmpMatch,
            TargetDetected:  null,
            TargetFlashKb:   opts.TargetFlashKb,
            ComPort:         opts.Port,
            ProbeSerial:     probeSerial,
            Power:           opts.Power,
            ConnectRst:      opts.ConnectUnderReset,
            BmpFrequencyHz:  opts.BmpFrequencyHz,
            Result:          hashFail.Result,
            ErrorCode:       hashFail.ErrorCode,
            ErrorMessage:    hashFail.ErrorMessage,
            DurationMs:      0,
            GdbTail:         null));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(CliText.Get("Result.LogWarning", ex.Message));
    }
    return 1;
}

var dbPath = opts.DbPath ?? Path.Combine(Environment.CurrentDirectory, "flash_log.db");

// Atomically reserve the complete firmware identity before GDB touches the
// target. A database/reservation failure is a hard stop, not a warning.
try
{
    using var lockStore = new SqliteLogStore(dbPath);
    var requested = new BatchLockDescriptor(
        opts.Product,
        opts.FirmwareVersion,
        computedSha,
        opts.TargetBmpMatch,
        opts.TargetFlashKb);
    var reservation = lockStore.ReserveBatchLock(opts.Batch, requested);
    if (!reservation.IsAccepted)
    {
        var locked = reservation.Lock;
        var msg = $"locked to {locked.ProductId} v{locked.FirmwareVersion} "
            + $"sha256={ShortSha(locked.FirmwareSha256)}, attempted "
            + $"{opts.Product} v{opts.FirmwareVersion} sha256={ShortSha(computedSha)}";
        Console.Error.WriteLine(CliText.Get("Result.ErrorRaw", "E_BATCH_LOCKED", msg));
        Console.Error.WriteLine(OperatorText.ErrorHint("E_BATCH_LOCKED"));
        lockStore.Append(new FlashAttemptRecord(
            TsUtc:           DateTime.UtcNow,
            Operator:        opts.Operator,
            StationId:       opts.StationId,
            BatchId:         opts.Batch,
            ProductId:       opts.Product,
            FirmwareVersion: opts.FirmwareVersion,
            FirmwareSha256:  computedSha,
            TargetBmpMatch:  opts.TargetBmpMatch,
            TargetDetected:  null,
            TargetFlashKb:   opts.TargetFlashKb,
            ComPort:         opts.Port,
            ProbeSerial:     probeSerial,
            Power:           opts.Power,
            ConnectRst:      opts.ConnectUnderReset,
            BmpFrequencyHz:  opts.BmpFrequencyHz,
            Result:          FlashResult.Fail,
            ErrorCode:       "E_BATCH_LOCKED",
            ErrorMessage:    msg,
            DurationMs:      0,
            GdbTail:         null));
        return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine(CliText.Get("Result.ErrorRaw", "E_BATCH_LOCK_CHECK_FAILED", ex.Message));
    Console.Error.WriteLine(OperatorText.ErrorHint("E_BATCH_LOCK_CHECK_FAILED"));
    return 1;
}

Console.WriteLine(CliText.Get("Flash.Summary", opts.Product, opts.FirmwareVersion, opts.TargetBmpMatch, opts.Port));
Console.WriteLine(CliText.Get("Flash.Operator", opts.Operator, opts.Batch, opts.StationId));
Console.WriteLine(CliText.Get("Flash.Running"));
Console.WriteLine();

var gdb = new GdbProcess(gdbExe);
var outcome = await FlashStateMachine.RunAsync(
    gdb,
    opts,
    timeout: TimeSpan.FromSeconds(opts.TimeoutSeconds),
    onLine: line =>
    {
        if (line.Stream == GdbStream.Stderr || !string.IsNullOrWhiteSpace(line.Text))
            Console.WriteLine($"  gdb> {line.Text}");
    });

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

try
{
    using var log = new SqliteLogStore(dbPath);
    var rowId = log.Append(new FlashAttemptRecord(
        TsUtc:           DateTime.UtcNow,
        Operator:        opts.Operator,
        StationId:       opts.StationId,
        BatchId:         opts.Batch,
        ProductId:       opts.Product,
        FirmwareVersion: opts.FirmwareVersion,
        FirmwareSha256:  computedSha,
        TargetBmpMatch:  opts.TargetBmpMatch,
        TargetDetected:  outcome.DetectedTarget,
        TargetFlashKb:   opts.TargetFlashKb,
        ComPort:         opts.Port,
        ProbeSerial:     probeSerial,
        Power:           opts.Power,
        ConnectRst:      opts.ConnectUnderReset,
        BmpFrequencyHz:  opts.BmpFrequencyHz,
        Result:          outcome.Result,
        ErrorCode:       outcome.ErrorCode,
        ErrorMessage:    outcome.ErrorMessage,
        DurationMs:      (long)outcome.Duration.TotalMilliseconds,
        GdbTail:         outcome.GdbTail));
    Console.WriteLine(CliText.Get("Flash.Logged", rowId, dbPath));
}
catch (Exception ex)
{
    Console.Error.WriteLine(CliText.Get("Result.LogWarning", ex.Message));
}

return outcome.IsPass ? 0 : 1;

static string ShortSha(string value) =>
    string.IsNullOrWhiteSpace(value)
        ? "unknown"
        : value[..Math.Min(12, value.Length)].ToLowerInvariant();

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
    var psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName = "icacls.exe",
        UseShellExecute = false,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        CreateNoWindow = true,
    };
    psi.ArgumentList.Add(path);
    psi.ArgumentList.Add("/inheritance:r");
    psi.ArgumentList.Add("/grant:r");
    psi.ArgumentList.Add(identity);
    using var process = System.Diagnostics.Process.Start(psi)
        ?? throw new IOException("could not start icacls.exe");
    process.WaitForExit();
    if (process.ExitCode != 0)
        throw new IOException($"icacls failed: {process.StandardError.ReadToEnd().Trim()}");
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
    try { catalog = CatalogGenerator.Build(sidecars, owner, DateTime.UtcNow, revoked); }
    catch (CatalogGeneratorException ex) { Console.Error.WriteLine(ex.Message); return 2; }
    catch (CatalogParseException ex)     { Console.Error.WriteLine($"generated catalog failed validation: {ex.Message}"); return 2; }

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
    File.WriteAllBytes(outPath, CatalogJson.WriteUtf8(catalog));
    Console.WriteLine($"generated → {outPath}");
    Console.WriteLine($"  {catalog.Products.Count} product(s), {catalog.Products.Sum(p => p.Releases.Count)} release(s)");
    if (revoked.Count > 0)
        Console.WriteLine($"  · {revoked.Count} revoked release(s)");
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
    var priv  = Convert.FromBase64String(File.ReadAllText(keyPath).Trim());
    var bytes = File.ReadAllBytes(catalogPath);
    var sig   = CatalogSignature.Sign(bytes, priv);
    var sigPath = CatalogTrust.SignaturePathFor(catalogPath);
    File.WriteAllText(sigPath, Convert.ToBase64String(sig));
    Console.WriteLine($"signed → {sigPath}");
    return 0;
}

static async Task<int> LoginAsync()
{
    if (!OperatingSystem.IsWindows())
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

    var store = new TokenStore();
    try { store.Save(StoredTokens.From(token, DateTime.UtcNow)); }
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
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine(CliText.Get("Auth.StoreUnavailable"));
        return 5;
    }

    var store = new TokenStore();
    if (!store.Exists())
    {
        Console.WriteLine(CliText.Get("Auth.AlreadyLoggedOut"));
        return 0;
    }
    try { store.Delete(); }
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
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine(CliText.Get("Auth.StoreUnavailable"));
        return 5;
    }

    var store = new TokenStore();
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
    using var resp = await http.SendAsync(req);
    if (!resp.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"GitHub /user → {(int)resp.StatusCode} {resp.ReasonPhrase}");
        return 5;
    }
    var body = await resp.Content.ReadAsStringAsync();
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

static async Task<string> FetchRemoteFirmwareAsync(GitHubReleaseRef src, string expectedSha)
{
    if (!OperatingSystem.IsWindows())
        throw new PlatformNotSupportedException(CliText.Get("Firmware.PrivateUnsupported"));

    using var http = new HttpClient();
    var flow = new GitHubDeviceFlow(http, GitHubAppConfig.ClientId);
    var store = new TokenStore();
    var provider = new AccessTokenProvider(store, flow);
    var api = new GitHubReleaseAssetClient(http);
    var cache = new FirmwareCache(api, provider.GetFreshAccessTokenAsync);
    return await cache.GetOrDownloadAsync(src, expectedSha);
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
        Fail("Arm GNU Toolchain", CliText.Get("Doctor.GdbMissing"));
    else
        Pass("Arm GNU Toolchain", gdbPath);

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
            try
            {
                var catalog = CatalogJson.ParseFile(catalogPath);
                Pass("Catalog JSON", CliText.Get("Doctor.Products", catalog.Products.Count, catalogPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CatalogParseException)
            {
                Fail("Catalog JSON", ex.Message);
            }

            var trust = CatalogTrust.VerifyCatalogFile(catalogPath, requireSigned: true);
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
    if (CanWriteDirectory(localAppData, out var localError))
        Pass("%LOCALAPPDATA%\\Iskra", CliText.Get("Doctor.Writable"));
    else
        Fail("%LOCALAPPDATA%\\Iskra", localError ?? CliText.Get("Doctor.NotWritable"));

    var programData = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Iskra");
    if (CanWriteDirectory(programData, out var programDataError))
        Pass("%PROGRAMDATA%\\Iskra", CliText.Get("Doctor.Writable"));
    else
        Fail("%PROGRAMDATA%\\Iskra", programDataError ?? CliText.Get("Doctor.NotWritable"));

    if (OperatingSystem.IsWindows())
    {
        var tokenStore = new TokenStore();
        try
        {
            var tokens = tokenStore.Load();
            if (tokens is null)
                Warn("GitHub auth", CliText.Get("Doctor.NotSignedIn"));
            else if (tokens.RefreshTokenIsExpired(DateTime.UtcNow))
                Fail("GitHub auth", CliText.Get("Doctor.RefreshExpired"));
            else
                Pass("GitHub auth", tokenStore.Path);
        }
        catch (Exception ex) when (ex is TokenStoreException or IOException or UnauthorizedAccessException)
        {
            Fail("GitHub auth", ex.Message);
        }
    }
    else
    {
        Warn("GitHub auth", CliText.Get("Doctor.SecureStoreMissing"));
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
