using System.Text;
using FlashlightApp.Core;

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
    Console.Error.WriteLine("Вкажіть шлях через --gdb-path або встановіть Arm GNU Toolchain.");
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
        FlashlightApp.Cli — масова прошивка через Black Magic Probe

        Usage (caталог-driven, рекомендований режим для операторів):
          FlashlightApp.Cli --catalog <path> --product <id>
                            --operator <name> --batch <id>
                            [--firmware-version <ver>]   (інакше default release)
                            [--port <COMxx>]              (авто якщо один BMP)
                            [...]

        Usage (повний ручний режим — для розробки / без каталогу):
          FlashlightApp.Cli --elf <path>
                            --product <id> --target <bmp-match> --flash-kb <N>
                            --operator <name> --batch <id>
                            [--port <COMxx>]
                            [--station-id <id>]
                            [--firmware-version <ver>] [--firmware-sha256 <hex>]
                            [--power {probe|external}] [--freq <hz>]
                            [--connect-reset]
                            [--gdb-path <path>] [--db-path <path>]
                            [--dry-run]

          FlashlightApp.Cli --list-probes    показати підключені програматори
          FlashlightApp.Cli --help           ця довідка

        Підпис каталогу (Sprint 2):
          [--require-signed-catalog]   обовʼязковий Ed25519-підпис .sig поруч
                                       з catalog.json; інакше відмова.

        Каталог підставляє --target / --flash-kb / --firmware-version /
        --firmware-sha256 / --elf якщо вони не вказані явно. Шлях до ELF
        будується відносно директорії catalog-файлу.

        Required без каталогу:
          --elf            Шлях до ELF-файлу прошивки
          --product        ID продукту (наприклад pocket-light)
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
          3 = no probe / gdb not found, 4 = bad ELF.
        """);
}
