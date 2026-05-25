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
    Console.WriteLine($"Виконуваний файл: {gdbExe}");
    var processArgs = GdbCommandBuilder.BuildProcessArgs(
        opts.Port, opts.Power, opts.BmpFrequencyHz, opts.ConnectUnderReset, opts.ElfPath);
    foreach (var a in processArgs)
        Console.WriteLine($"  {a}");
    return 0;
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

        Usage:
          FlashlightApp.Cli --elf <path>
                            --product <id> --target <bmp-match> --flash-kb <N>
                            --operator <name> --batch <id>
                            [--port <COMxx>]            (авто-визначення якщо один BMP)
                            [--station-id <id>]
                            [--firmware-version <ver>] [--firmware-sha256 <hex>]
                            [--power {probe|external}]
                            [--freq <hz>]
                            [--connect-reset]
                            [--gdb-path <path-to-arm-none-eabi-gdb.exe>]
                            [--db-path <flash_log.db>]
                            [--dry-run]

          FlashlightApp.Cli --list-probes    показати підключені програматори
          FlashlightApp.Cli --help           ця довідка

        Required:
          --elf            Шлях до ELF-файлу прошивки
          --product        ID продукту з каталогу (наприклад pocket-light)
          --target         Очікувана підстрока target з swdp_scan (наприклад PY32F002A)
          --flash-kb       Розмір flash цільового MCU в KB
          --operator       Імʼя оператора
          --batch          ID партії

        Defaults:
          --power external, --freq 1000000, --connect-reset off,
          --station-id <hostname>, --gdb-path <auto-detect>,
          --db-path ./flash_log.db

        Exit codes:
          0 = PASS, 1 = FAIL, 2 = bad args / ambiguous probe,
          3 = no probe / gdb not found, 4 = bad ELF.
        """);
}
