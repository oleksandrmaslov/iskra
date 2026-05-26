using System.Net.Http;
using System.Text;
using Iskra.Core;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    PrintUsage();
    return 0;
}

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

bool requireSigned = args.Contains("--require-signed-catalog");
args = args.Where(a => a != "--require-signed-catalog").ToArray();

int catIdx = Array.IndexOf(args, "--catalog");
if (catIdx >= 0 && catIdx + 1 < args.Length)
{
    var catalogPath = args[catIdx + 1];
    var trust = CatalogTrust.VerifyCatalogFile(catalogPath, requireSigned);
    switch (trust)
    {
        case CatalogTrustResult.Verified:
            Console.WriteLine("Підпис каталогу: ✓ (Ed25519)");
            break;
        case CatalogTrustResult.UnsignedAllowed:
            Console.WriteLine("Підпис каталогу: відсутній (для production додайте --require-signed-catalog)");
            break;
        case CatalogTrustResult.UnsignedRejected:
            Console.Error.WriteLine("Помилка: каталог не підписано, --require-signed-catalog активний.");
            return 2;
        case CatalogTrustResult.BadSignature:
            Console.Error.WriteLine("Помилка: підпис каталогу не співпадає з ключем у застосунку.");
            return 2;
        case CatalogTrustResult.NoPublicKeyConfigured:
            Console.Error.WriteLine("Помилка: каталог підписано, але у застосунку немає публічного ключа.");
            return 2;
        case CatalogTrustResult.IoError:
            Console.Error.WriteLine("Помилка: не вдалося прочитати файл підпису каталогу.");
            return 2;
    }
}

var resolution = CatalogResolver.Resolve(args);
if (!resolution.Ok)
{
    Console.Error.WriteLine($"Помилка каталогу: {resolution.Error}");
    return 2;
}
args = resolution.ResolvedArgs!;
if (resolution.Product is not null && resolution.Release is not null)
{
    var p = resolution.Product;
    var r = resolution.Release;
    Console.WriteLine($"Каталог: {p.ProductId} → v{r.Version} ({p.Target.BmpMatch}, {p.Target.FlashKb} KB)");
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
        Console.WriteLine($"  ✓ кеш: {localPath}");
        args = args.Concat(new[] { "--elf", localPath }).ToArray();
    }
    catch (NotSignedInException)
    {
        Console.Error.WriteLine("Помилка: потрібна авторизація GitHub. Виконайте: Iskra.Cli --login");
        return 5;
    }
    catch (RefreshTokenExpiredException)
    {
        Console.Error.WriteLine("Помилка: сесія GitHub застаріла (>6 міс без оновлення). Виконайте: Iskra.Cli --login");
        return 5;
    }
    catch (GitHubAssetNotFoundException ex)
    {
        Console.Error.WriteLine($"Помилка: реліз GitHub не містить файл — {ex.Message}");
        return 5;
    }
    catch (GitHubApiException ex)
    {
        Console.Error.WriteLine($"Помилка GitHub API ({ex.StatusCode}): {ex.Message}");
        return 5;
    }
    catch (FirmwareCacheException ex)
    {
        Console.Error.WriteLine($"Помилка завантаження прошивки: {ex.Message}");
        return 5;
    }
}

bool dryRun = args.Contains("--dry-run");
args = args.Where(a => a != "--dry-run").ToArray();

// Auto-detect --port if omitted and exactly one BMP GDB interface is attached.
if (!args.Contains("--port"))
{
    var probes = ProbeDiscovery.FindGdbPorts();
    switch (probes.Count)
    {
        case 0:
            Console.Error.WriteLine("Помилка: Black Magic Probe не знайдено.");
            Console.Error.WriteLine("Підключіть програматор або вкажіть --port COMxx вручну.");
            return 3;
        case 1:
            Console.WriteLine($"Виявлено програматор: {probes[0].PortName}"
                + (probes[0].FriendlyName is not null ? $" ({probes[0].FriendlyName})" : ""));
            args = args.Concat(new[] { "--port", probes[0].PortName }).ToArray();
            break;
        default:
            Console.Error.WriteLine($"Знайдено {probes.Count} програматорів. Вкажіть --port явно:");
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

switch (ElfPreflight.Check(opts.ElfPath))
{
    case ElfPreflight.CheckResult.NotFound:
        Console.Error.WriteLine($"Помилка: ELF-файл не знайдено: {opts.ElfPath}");
        return 4;
    case ElfPreflight.CheckResult.NotAnElf:
        Console.Error.WriteLine($"Помилка: файл не є ELF (немає 0x7F'ELF' magic): {opts.ElfPath}");
        return 4;
    case ElfPreflight.CheckResult.IoError:
        Console.Error.WriteLine($"Помилка: не вдалося прочитати ELF-файл: {opts.ElfPath}");
        return 4;
}

string? computedSha = null;
bool hashVerified = false;
bool hashWasRequired = FirmwareIntegrity.IsValidSha256Hex(opts.FirmwareSha256);
if (hashWasRequired)
{
    computedSha = FirmwareIntegrity.ComputeSha256Hex(opts.ElfPath);
    hashVerified = FirmwareIntegrity.HashesMatch(computedSha, opts.FirmwareSha256);
}

var gdbExe = GdbDiscovery.Find(opts.GdbPath);
if (gdbExe is null)
{
    Console.Error.WriteLine("Помилка: arm-none-eabi-gdb не знайдено.");
    Console.Error.WriteLine("Вкажіть шлях через --gdb-path або повторно запустіть інсталятор Iskra.");
    return 3;
}

if (dryRun)
{
    Console.WriteLine("=== DRY RUN — gdb не буде запущено ===");
    if (hashWasRequired)
    {
        Console.WriteLine($"ELF SHA-256:     {computedSha}");
        Console.WriteLine($"Каталог SHA-256: {opts.FirmwareSha256.ToLowerInvariant()}");
        Console.WriteLine(hashVerified
            ? "Перевірка цілісності: ✓ співпадає"
            : "Перевірка цілісності: ✗ НЕ СПІВПАДАЄ — у реальному запуску буде відмова");
    }
    else
    {
        Console.WriteLine("Перевірка цілісності: пропущена (немає очікуваного SHA-256)");
    }
    Console.WriteLine($"Виконуваний файл: {gdbExe}");
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
    Console.WriteLine($"  ✗ ПОМИЛКА: {hashFail.ErrorCode}");
    Console.WriteLine($"  {ErrorHints.For(hashFail.ErrorCode)}");
    Console.WriteLine($"  Деталі: {hashFail.ErrorMessage}");
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
            FirmwareSha256:  opts.FirmwareSha256,
            TargetBmpMatch:  opts.TargetBmpMatch,
            TargetDetected:  null,
            TargetFlashKb:   opts.TargetFlashKb,
            ComPort:         opts.Port,
            ProbeSerial:     null,
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
        Console.Error.WriteLine($"  УВАГА: не вдалося записати в журнал: {ex.Message}");
    }
    return 1;
}

var dbPath = opts.DbPath ?? Path.Combine(Environment.CurrentDirectory, "flash_log.db");

Console.WriteLine($"Прошивка: {opts.Product} v{opts.FirmwareVersion} → {opts.TargetBmpMatch} ({opts.Port})");
Console.WriteLine($"Оператор: {opts.Operator} | Партія: {opts.Batch} | Станція: {opts.StationId}");
Console.WriteLine("Виконується...");
Console.WriteLine();

var gdb = new GdbProcess(gdbExe);
var outcome = await FlashStateMachine.RunAsync(
    gdb,
    opts,
    timeout: TimeSpan.FromSeconds(15),
    onLine: line =>
    {
        if (line.Stream == GdbStream.Stderr || !string.IsNullOrWhiteSpace(line.Text))
            Console.WriteLine($"  gdb> {line.Text}");
    });

Console.WriteLine();
if (outcome.IsPass)
{
    Console.WriteLine("============================================");
    Console.WriteLine($"  ✓ ПРОШИВКА УСПІШНА  ({outcome.Duration.TotalMilliseconds:F0} мс)");
    Console.WriteLine($"  Ціль: {outcome.DetectedTarget}");
    Console.WriteLine("============================================");
}
else
{
    Console.WriteLine("============================================");
    Console.WriteLine($"  ✗ ПОМИЛКА: {outcome.ErrorCode}");
    Console.WriteLine($"  {ErrorHints.For(outcome.ErrorCode)}");
    if (!string.IsNullOrEmpty(outcome.ErrorMessage))
        Console.WriteLine($"  Деталі: {outcome.ErrorMessage}");
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
        FirmwareSha256:  opts.FirmwareSha256,
        TargetBmpMatch:  opts.TargetBmpMatch,
        TargetDetected:  outcome.DetectedTarget,
        TargetFlashKb:   opts.TargetFlashKb,
        ComPort:         opts.Port,
        ProbeSerial:     null,
        Power:           opts.Power,
        ConnectRst:      opts.ConnectUnderReset,
        BmpFrequencyHz:  opts.BmpFrequencyHz,
        Result:          outcome.Result,
        ErrorCode:       outcome.ErrorCode,
        ErrorMessage:    outcome.ErrorMessage,
        DurationMs:      (long)outcome.Duration.TotalMilliseconds,
        GdbTail:         outcome.GdbTail));
    Console.WriteLine($"  (записано в журнал: id={rowId}, {dbPath})");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"  УВАГА: не вдалося записати в журнал: {ex.Message}");
}

return outcome.IsPass ? 0 : 1;

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
    File.WriteAllText(pubPath,  pubB64);
    File.WriteAllText(privPath, privB64);
    Console.WriteLine($"public key  → {pubPath}");
    Console.WriteLine($"private key → {privPath}");
    Console.WriteLine();
    Console.WriteLine("Public key (base64) — paste into CatalogTrust.EmbeddedPublicKeyBase64:");
    Console.WriteLine(pubB64);
    return 0;
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

    var targetsDir = args[from + 1];
    var outPath    = args[outIdx + 1];

    List<TargetSidecar> sidecars;
    try { sidecars = CatalogGenerator.ReadTargetsTree(targetsDir); }
    catch (CatalogGeneratorException ex) { Console.Error.WriteLine(ex.Message); return 2; }
    catch (TargetSidecarException ex)    { Console.Error.WriteLine(ex.Message); return 2; }

    Catalog catalog;
    try { catalog = CatalogGenerator.Build(sidecars, owner, DateTime.UtcNow); }
    catch (CatalogGeneratorException ex) { Console.Error.WriteLine(ex.Message); return 2; }
    catch (CatalogParseException ex)     { Console.Error.WriteLine($"generated catalog failed validation: {ex.Message}"); return 2; }

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
    File.WriteAllBytes(outPath, CatalogJson.WriteUtf8(catalog));
    Console.WriteLine($"generated → {outPath}");
    Console.WriteLine($"  {catalog.Products.Count} product(s), {catalog.Products.Sum(p => p.Releases.Count)} release(s)");
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
    if (!GitHubAppConfig.IsConfigured)
    {
        Console.Error.WriteLine("Помилка: GitHub App Client ID не налаштовано (зверніться до розробника).");
        return 2;
    }

    using var http = new HttpClient();
    var flow = new GitHubDeviceFlow(http, GitHubAppConfig.ClientId);

    Console.WriteLine("Запит коду пристрою GitHub...");
    DeviceCodeResponse code;
    try { code = await flow.RequestDeviceCodeAsync(); }
    catch (Exception ex) { Console.Error.WriteLine($"Помилка: {ex.Message}"); return 5; }

    Console.WriteLine();
    Console.WriteLine("============================================");
    Console.WriteLine($"  Відкрийте у браузері: {code.VerificationUri}");
    Console.WriteLine($"  Введіть код:          {code.UserCode}");
    Console.WriteLine("============================================");
    Console.WriteLine();
    Console.WriteLine($"Очікування авторизації... (таймаут ~{code.ExpiresIn / 60} хв, Ctrl+C для скасування)");

    TokenResponse token;
    try { token = await flow.PollForTokenAsync(code); }
    catch (GitHubAuthException ex) when (ex.ErrorCode == "access_denied")
    {
        Console.Error.WriteLine("Авторизацію відхилено користувачем."); return 5;
    }
    catch (GitHubAuthException ex) when (ex.ErrorCode == "expired_token")
    {
        Console.Error.WriteLine("Код пристрою застарів. Запустіть --login знову."); return 5;
    }
    catch (GitHubAuthException ex)
    {
        Console.Error.WriteLine($"Помилка GitHub: {ex.Message}"); return 5;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Скасовано."); return 5;
    }

    var store = new TokenStore();
    try { store.Save(StoredTokens.From(token, DateTime.UtcNow)); }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Помилка збереження токенів у {store.Path}: {ex.Message}");
        Console.Error.WriteLine("Запустіть від імені адміністратора, якщо проблема в правах доступу до %PROGRAMDATA%.");
        return 5;
    }

    Console.WriteLine();
    Console.WriteLine($"✓ Авторизовано. Токени збережено: {store.Path}");
    Console.WriteLine($"  Access token дійсний ~{token.ExpiresIn / 3600} год.");
    Console.WriteLine($"  Refresh token дійсний ~{token.RefreshTokenExpiresIn / 86400} дн.");
    return 0;
}

static int Logout()
{
    var store = new TokenStore();
    if (!store.Exists())
    {
        Console.WriteLine("Токени не знайдено — вже не авторизовано.");
        return 0;
    }
    try { store.Delete(); }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Помилка видалення {store.Path}: {ex.Message}");
        return 5;
    }
    Console.WriteLine($"Токени видалено: {store.Path}");
    return 0;
}

static async Task<int> WhoamiAsync()
{
    var store = new TokenStore();
    StoredTokens? stored;
    try { stored = store.Load(); }
    catch (TokenStoreException ex)
    {
        Console.Error.WriteLine($"Файл токенів пошкоджено: {ex.Message}");
        Console.Error.WriteLine("Видаліть і авторизуйтеся знову: Iskra.Cli --logout && Iskra.Cli --login");
        return 5;
    }

    if (stored is null)
    {
        Console.WriteLine("Не авторизовано. Виконайте: Iskra.Cli --login");
        return 5;
    }

    var now = DateTime.UtcNow;
    Console.WriteLine($"Файл:              {store.Path}");
    Console.WriteLine($"Access token до:   {stored.AccessTokenExpiresAtUtc:yyyy-MM-dd HH:mm} UTC ({FormatFutureDuration(stored.AccessTokenExpiresAtUtc - now)})");
    Console.WriteLine($"Refresh token до:  {stored.RefreshTokenExpiresAtUtc:yyyy-MM-dd HH:mm} UTC ({FormatFutureDuration(stored.RefreshTokenExpiresAtUtc - now)})");

    if (!GitHubAppConfig.IsConfigured)
    {
        Console.WriteLine("(пропускаю перевірку через GitHub — Client ID не налаштовано)");
        return 0;
    }

    // Verify the access token still works server-side and show the login.
    using var http = new HttpClient();
    var flow = new GitHubDeviceFlow(http, GitHubAppConfig.ClientId);
    var provider = new AccessTokenProvider(store, flow);
    string accessToken;
    try { accessToken = await provider.GetFreshAccessTokenAsync(); }
    catch (NotSignedInException)        { Console.Error.WriteLine("(не авторизовано)");                return 5; }
    catch (RefreshTokenExpiredException) { Console.Error.WriteLine("Refresh token застарів — --login"); return 5; }
    catch (Exception ex)                 { Console.Error.WriteLine($"Не вдалося оновити токен: {ex.Message}"); return 5; }

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
        Console.WriteLine($"GitHub користувач: {login.GetString()}");
    return 0;
}

static async Task<string> FetchRemoteFirmwareAsync(GitHubReleaseRef src, string expectedSha)
{
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
    if (d.TotalSeconds <= 0) return "застарів";
    if (d.TotalDays >= 30)   return $"через ~{(int)(d.TotalDays / 30)} міс";
    if (d.TotalDays >= 1)    return $"через {d.Days} дн {d.Hours} год";
    if (d.TotalHours >= 1)   return $"через {d.Hours} год {d.Minutes} хв";
    return $"через {d.Minutes} хв";
}

static int ListProbes()
{
    var all = ProbeDiscovery.FindAll();
    if (all.Count == 0)
    {
        Console.WriteLine("Програматори не знайдено.");
        Console.WriteLine("(шукали USB-пристрої VID 0x1D50 PID 0x6018 — Black Magic Probe)");
        return 0;
    }
    Console.WriteLine($"Знайдено {all.Count} інтерфейс(ів):");
    foreach (var p in all)
    {
        var role = p.Interface switch
        {
            ProbeInterface.Gdb     => "GDB ",
            ProbeInterface.Uart    => "UART",
            _                      => "??? ",
        };
        Console.WriteLine($"  [{role}]  {p.PortName,-8}  {p.FriendlyName}");
    }
    Console.WriteLine();
    var gdb = ProbeDiscovery.FindGdbPorts();
    if (gdb.Count == 1)
        Console.WriteLine($"Стандартний GDB-порт: {gdb[0].PortName}");
    return 0;
}

static void PrintUsage()
{
    Console.WriteLine("""
        Iskra.Cli — масова прошивка через Black Magic Probe

        Usage (caталог-driven, рекомендований режим для операторів):
          Iskra.Cli --catalog <path> --product <id>
                            --operator <name> --batch <id>
                            [--firmware-version <ver>]   (інакше default release)
                            [--port <COMxx>]              (авто якщо один BMP)
                            [...]

        Usage (повний ручний режим — для розробки / без каталогу):
          Iskra.Cli --elf <path>
                            --product <id> --target <bmp-match> --flash-kb <N>
                            --operator <name> --batch <id>
                            [--port <COMxx>]
                            [--station-id <id>]
                            [--firmware-version <ver>] [--firmware-sha256 <hex>]
                            [--power {probe|external}] [--freq <hz>]
                            [--connect-reset]
                            [--gdb-path <path>] [--db-path <path>]
                            [--dry-run]

          Iskra.Cli --list-probes    показати підключені програматори
          Iskra.Cli --help           ця довідка

        Авторизація GitHub (Sprint 3):
          Iskra.Cli --login          OAuth Device Flow: відкрити URL,
                                     ввести код, дочекатися підтвердження.
                                     Токени зберігаються зашифровано (DPAPI)
                                     в %PROGRAMDATA%\Iskra\auth.bin.
          Iskra.Cli --logout         видалити збережені токени.
          Iskra.Cli --whoami         показати GitHub-користувача та строки дії.

        Підпис каталогу (Sprint 2):
          [--require-signed-catalog]   обовʼязковий Ed25519-підпис .sig поруч
                                       з catalog.json; інакше відмова.

        Каталог підставляє --target / --flash-kb / --firmware-version /
        --firmware-sha256 / --elf якщо вони не вказані явно. Шлях до ELF
        будується відносно директорії catalog-файлу.

        Required без каталогу:
          --elf            Шлях до ELF-файлу прошивки
          --product        ID продукту (наприклад ci-clop)
          --target         Сімейство BMP (наприклад PY32Fxxx)
          --flash-kb       Розмір flash цільового MCU в KB
          --operator       Імʼя оператора
          --batch          ID партії

        Defaults:
          --power external, --freq 1000000, --connect-reset off,
          --station-id <hostname>, --gdb-path <auto-detect>,
          --db-path ./flash_log.db

        Exit codes:
          0 = PASS, 1 = FAIL, 2 = bad args / ambiguous probe / catalog error,
          3 = no probe / gdb not found, 4 = bad ELF,
          5 = GitHub auth / firmware download error.
        """);
}
