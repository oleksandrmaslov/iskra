using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Iskra.Core;

/// <summary>
/// Result from one held GDB connection. <see cref="Flash"/> is null when the
/// caller's scan gate refused the target, the scan timed out, or GDB could not
/// finish the scan commands. No attach/load command is sent in those cases.
/// </summary>
public sealed record GdbGuardedRunResult(GdbRunResult Scan, GdbRunResult? Flash);

/// <summary>
/// Drives GDB/MI while retaining one target connection from <c>swdp_scan</c>
/// through attach/load/verify. The target is safely attached (halted, but not
/// written) before the application gate runs, so a board cannot be swapped and
/// silently reselected between identity classification and the first flash
/// write. Firmware is opened and <c>load</c> is sent only after that gate.
/// </summary>
internal static class GdbMiSession
{
    private static readonly string[] SafeProcessArguments =
    [
        "-nx",
        "--quiet",
        "--interpreter=mi2",
        "-iex",
        "set auto-load off",
        "-iex",
        "set debuginfod enabled off",
    ];

    public static async Task<GdbGuardedRunResult> RunAsync(
        string gdbExe,
        string endpoint,
        PowerMode power,
        int frequencyHz,
        bool connectUnderReset,
        string firmwarePath,
        TimeSpan scanTimeout,
        TimeSpan flashTimeout,
        Func<GdbRunResult, bool> scanGate,
        Action<GdbLine>? onLine,
        CancellationToken ct,
        string? probeLockIdentity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gdbExe);
        ArgumentException.ThrowIfNullOrWhiteSpace(firmwarePath);
        ArgumentNullException.ThrowIfNull(scanGate);
        if (frequencyHz <= 0) throw new ArgumentOutOfRangeException(nameof(frequencyHz));
        if (scanTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(scanTimeout));
        if (flashTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(flashTimeout));

        var normalizedEndpoint = GdbCommandBuilder.NormalizeProbeEndpoint(endpoint);
        probeLockIdentity ??= ProbeDiscovery.ResolveProbeLockIdentity(normalizedEndpoint);
        await using var probeLock = TryAcquireProbeLock(normalizedEndpoint, probeLockIdentity: probeLockIdentity);
        if (probeLock is null)
        {
            var busy = SyntheticRun(
                "Device or resource busy: another Iskra process owns this probe",
                exitCode: 1);
            onLine?.Invoke(busy.Output[0]);
            return new GdbGuardedRunResult(busy, null);
        }

        var psi = new ProcessStartInfo
        {
            FileName = gdbExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var argument in SafeProcessArguments) psi.ArgumentList.Add(argument);
        psi.Environment["LC_ALL"] = "C";
        psi.Environment["LANG"] = "C";
        psi.Environment["DEBUGINFOD_URLS"] = string.Empty;
        psi.Environment["GDBHISTFILE"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var scan = new PhaseCapture(onLine);
        var flash = new PhaseCapture(onLine);
        var current = scan;
        var waiters = new ConcurrentDictionary<int, TaskCompletionSource<MiCommandResult>>();
        var nextToken = 100;

        void CompleteWaiter(string text)
        {
            var separator = text.IndexOf('^');
            if (separator <= 0 || !int.TryParse(text.AsSpan(0, separator), out var token)) return;
            if (!waiters.TryRemove(token, out var waiter)) return;

            var result = text[(separator + 1)..];
            waiter.TrySetResult(new MiCommandResult(
                Success: result.StartsWith("done", StringComparison.Ordinal)
                    || result.StartsWith("running", StringComparison.Ordinal)
                    || result.StartsWith("connected", StringComparison.Ordinal)
                    || result.StartsWith("exit", StringComparison.Ordinal),
                Record: result));
        }

        void CaptureMiLine(BoundedProcessLine captured, GdbStream stream)
        {
            var text = captured.Text;
            if (captured.WasTruncated)
            {
                if (stream == GdbStream.Stderr)
                    current.Add(text, stream);
                else
                    current.Add(
                        $"GDB/MI record exceeded {GdbProcess.MaxCapturedLineChars} characters and was discarded",
                        GdbStream.Stderr);
                return;
            }
            CompleteWaiter(text);

            if (text.Length >= 2 && (text[0] is '~' or '@' or '&') && text[1] == '"')
            {
                var decoded = DecodeMiCString(text.AsSpan(1));
                var parts = decoded.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                var count = decoded.EndsWith('\n') ? parts.Length - 1 : parts.Length;
                for (var i = 0; i < count; i++) current.Add(parts[i], stream);
                return;
            }

            // Result/notification records are protocol framing, except errors:
            // retain those as diagnostics so the existing classifier can map a
            // failed attach or transport operation without parsing UI chatter.
            if (text.Contains("^error", StringComparison.Ordinal)) current.Add(text, stream);
        }

        process.Exited += (_, _) =>
        {
            foreach (var pair in waiters)
            {
                if (waiters.TryRemove(pair.Key, out var waiter))
                    waiter.TrySetException(new IOException("gdb exited before replying to an MI command"));
            }
        };

        async Task<MiCommandResult> SendAsync(string command, CancellationToken commandCt)
        {
            var token = Interlocked.Increment(ref nextToken);
            var waiter = new TaskCompletionSource<MiCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!waiters.TryAdd(token, waiter)) throw new InvalidOperationException("duplicate GDB/MI token");
            try
            {
                await process.StandardInput.WriteLineAsync($"{token}{command}".AsMemory(), commandCt)
                    .ConfigureAwait(false);
                await process.StandardInput.FlushAsync(commandCt).ConfigureAwait(false);
                var result = await waiter.Task.WaitAsync(commandCt).ConfigureAwait(false);
                if (!result.Success) current.Add($"GDB/MI error: {result.Record}", GdbStream.Stderr);
                return result;
            }
            finally
            {
                waiters.TryRemove(token, out _);
            }
        }

        async Task<bool> ConsoleAsync(string command, CancellationToken commandCt)
        {
            var escaped = EscapeMiCString(command);
            return (await SendAsync($"-interpreter-exec console \"{escaped}\"", commandCt)
                .ConfigureAwait(false)).Success;
        }

        async Task DetachBestEffortAsync()
        {
            if (HasExited(process)) return;
            using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { _ = await SendAsync("-target-detach", cleanupCts.Token).ConfigureAwait(false); }
            catch { }
        }

        var total = Stopwatch.StartNew();
        var processStarted = false;
        using var readerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task stdoutTask = Task.CompletedTask;
        Task stderrTask = Task.CompletedTask;
        try
        {
            if (!process.Start()) throw new InvalidOperationException($"Failed to start gdb: {gdbExe}");
            processStarted = true;
            stdoutTask = BoundedProcessLineReader.ReadAsync(
                process.StandardOutput,
                line => CaptureMiLine(line, GdbStream.Stdout),
                readerCts.Token);
            stderrTask = BoundedProcessLineReader.ReadAsync(
                process.StandardError,
                line => CaptureMiLine(line, GdbStream.Stderr),
                readerCts.Token);

            using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            scanCts.CancelAfter(scanTimeout);
            var scanOk = true;
            var scanTimedOut = false;
            var attached = false;
            try
            {
                scanOk &= (await SendAsync("-gdb-set confirm off", scanCts.Token).ConfigureAwait(false)).Success;
                scanOk &= (await SendAsync("-gdb-set pagination off", scanCts.Token).ConfigureAwait(false)).Success;
                if (scanOk)
                {
                    scanOk &= (await SendAsync(
                        BuildTargetSelectCommand(normalizedEndpoint),
                        scanCts.Token).ConfigureAwait(false)).Success;
                }
                if (scanOk && power == PowerMode.Probe)
                    scanOk &= await ConsoleAsync("monitor tpwr enable", scanCts.Token).ConfigureAwait(false);
                if (scanOk)
                    scanOk &= await ConsoleAsync($"monitor frequency {frequencyHz}", scanCts.Token).ConfigureAwait(false);
                if (scanOk && connectUnderReset)
                    scanOk &= await ConsoleAsync("monitor connect_rst enable", scanCts.Token).ConfigureAwait(false);
                if (scanOk)
                    scanOk &= await ConsoleAsync("monitor swdp_scan", scanCts.Token).ConfigureAwait(false);
                if (scanOk)
                {
                    // Attaching halts the selected target but does not write
                    // firmware. Binding target #1 before the application gate
                    // removes the physical swap/reselect window; a later
                    // disconnect now breaks this held session and fails closed.
                    attached = await ConsoleAsync("attach 1", scanCts.Token).ConfigureAwait(false);
                    if (!attached)
                        scan.Add("Cannot attach target #1 (GDB/MI attach failed)", GdbStream.Stderr);
                    scanOk &= attached;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                scanTimedOut = true;
                scanOk = false;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                scan.Add($"GDB/MI error: {ex.Message}", GdbStream.Stderr);
                scanOk = false;
            }

            var scanRun = scan.Snapshot(scanOk ? 0 : 1, scanTimedOut, total.Elapsed);
            if (scanTimedOut || !scanOk || !scanGate(scanRun))
            {
                if (attached) await DetachBestEffortAsync().ConfigureAwait(false);
                await ExitOrTerminateAsync(process).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return new GdbGuardedRunResult(scanRun, null);
            }

            current = flash;
            var flashStarted = Stopwatch.StartNew();
            var flashOk = true;
            var flashTimedOut = false;
            using var flashCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            flashCts.CancelAfter(flashTimeout);
            try
            {
                flashOk &= (await SendAsync(
                    $"-file-exec-and-symbols \"{EscapeMiCString(Path.GetFullPath(firmwarePath))}\"",
                    flashCts.Token).ConfigureAwait(false)).Success;
                if (flashOk) flashOk &= await ConsoleAsync("load", flashCts.Token).ConfigureAwait(false);
                if (flashOk) flashOk &= await ConsoleAsync("compare-sections", flashCts.Token).ConfigureAwait(false);
                if (flashOk) _ = await ConsoleAsync("kill", flashCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                flashTimedOut = true;
                flashOk = false;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                flash.Add($"GDB/MI error: {ex.Message}", GdbStream.Stderr);
                flashOk = false;
            }

            if (flashTimedOut)
                await TerminateProcessTreeAsync(process, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            else
                await ExitOrTerminateAsync(process).ConfigureAwait(false);

            if (!await DrainReadersAsync(stdoutTask, stderrTask, readerCts).ConfigureAwait(false))
            {
                flash.Add("GDB output capture did not terminate cleanly", GdbStream.Stderr);
                flashOk = false;
            }

            flashStarted.Stop();
            ct.ThrowIfCancellationRequested();

            var exitCode = !flashOk || flashTimedOut || !process.HasExited ? 1 : process.ExitCode;
            var flashRun = flash.Snapshot(exitCode, flashTimedOut, flashStarted.Elapsed);
            return new GdbGuardedRunResult(scanRun, flashRun);
        }
        finally
        {
            foreach (var pair in waiters)
            {
                if (waiters.TryRemove(pair.Key, out var waiter))
                    waiter.TrySetCanceled();
            }

            // Process.Dispose does not terminate a running child. Every path,
            // including a throwing scan gate or caller cancellation, must make
            // one bounded process-tree termination attempt before releasing
            // the interprocess probe lock.
            if (processStarted && !HasExited(process))
                await TerminateProcessTreeAsync(process, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            readerCts.Cancel();
            await ObserveReadersAsync(stdoutTask, stderrTask).ConfigureAwait(false);
        }
    }

    private static async Task<bool> DrainReadersAsync(
        Task stdoutTask,
        Task stderrTask,
        CancellationTokenSource readerCts)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask)
                .WaitAsync(timeout.Token)
                .ConfigureAwait(false);
            return true;
        }
        catch
        {
            readerCts.Cancel();
            await ObserveReadersAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            return false;
        }
    }

    private static async Task ObserveReadersAsync(params Task[] readers)
    {
        try { await Task.WhenAll(readers).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    internal static ProbeLock? TryAcquireProbeLock(
        string endpoint,
        string? lockRoot = null,
        string? probeLockIdentity = null)
    {
        var identity = probeLockIdentity
            ?? ProbeDiscovery.ResolveProbeLockIdentity(endpoint);
        var namespaceSalt = string.IsNullOrWhiteSpace(lockRoot) ? string.Empty : $"test:{lockRoot}:";
        var digest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(namespaceSalt + identity)))[..32];
        return ProbeLock.TryAcquire($"Iskra.Probe.v1.{digest}");
    }

    private static GdbRunResult SyntheticRun(string message, int exitCode)
    {
        var line = new GdbLine(DateTime.UtcNow, GdbStream.Stderr, message);
        return new GdbRunResult(exitCode, false, TimeSpan.Zero, [line]);
    }

    private static async Task ExitOrTerminateAsync(Process process)
    {
        if (HasExited(process)) return;
        try
        {
            await process.StandardInput.WriteLineAsync("999999-gdb-exit").ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            if (await WaitForExitBoundedAsync(process, TimeSpan.FromSeconds(2)).ConfigureAwait(false))
                return;
        }
        catch { }

        await TerminateProcessTreeAsync(process, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
    }

    internal static async Task TerminateProcessTreeAsync(Process process, TimeSpan grace)
    {
        try
        {
            if (!HasExited(process)) process.Kill(entireProcessTree: true);
        }
        catch { }
        _ = await WaitForExitBoundedAsync(process, grace).ConfigureAwait(false);
    }

    private static async Task<bool> WaitForExitBoundedAsync(Process process, TimeSpan grace)
    {
        if (HasExited(process)) return true;
        using var cts = new CancellationTokenSource(grace);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { return HasExited(process); }
        catch (InvalidOperationException) { return HasExited(process); }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    /// <summary>
    /// gdb hands <c>-target-select</c> arguments to its CLI <c>target</c> command
    /// verbatim, without MI c-string unescaping, so a quoted and escaped endpoint
    /// reaches the serial layer with its quotes and doubled backslashes intact
    /// and cannot be opened. The endpoint is therefore passed bare, which is safe
    /// only because <see cref="GdbCommandBuilder.NormalizeProbeEndpoint"/> admits
    /// no whitespace, quotes, or control characters.
    /// </summary>
    internal static string BuildTargetSelectCommand(string endpoint) =>
        $"-target-select extended-remote {GdbCommandBuilder.NormalizeProbeEndpoint(endpoint)}";

    internal static string EscapeMiCString(string value)
    {
        var builder = new StringBuilder(value.Length + 16);
        foreach (var c in value)
        {
            builder.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when char.IsControl(c) => $"\\x{(int)c:x2}",
                _ => c.ToString(),
            });
        }
        return builder.ToString();
    }

    internal static string DecodeMiCString(ReadOnlySpan<char> encoded)
    {
        if (encoded.Length < 2 || encoded[0] != '"') return encoded.ToString();
        var builder = new StringBuilder(encoded.Length);
        for (var i = 1; i < encoded.Length; i++)
        {
            var c = encoded[i];
            if (c == '"' && i == encoded.Length - 1) break;
            if (c != '\\' || i + 1 >= encoded.Length)
            {
                builder.Append(c);
                continue;
            }

            var escape = encoded[++i];
            switch (escape)
            {
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 't': builder.Append('\t'); break;
                case 'b': builder.Append('\b'); break;
                case 'f': builder.Append('\f'); break;
                case 'v': builder.Append('\v'); break;
                case 'a': builder.Append('\a'); break;
                case '\\': builder.Append('\\'); break;
                case '"': builder.Append('"'); break;
                case 'x':
                {
                    var value = 0;
                    var digits = 0;
                    while (i + 1 < encoded.Length && digits < 2)
                    {
                        var hex = HexValue(encoded[i + 1]);
                        if (hex < 0) break;
                        value = value * 16 + hex;
                        i++;
                        digits++;
                    }
                    if (digits == 0) builder.Append("\\x");
                    else builder.Append((char)value);
                    break;
                }
                case >= '0' and <= '7':
                {
                    var value = escape - '0';
                    var digits = 1;
                    while (i + 1 < encoded.Length && digits < 3 && encoded[i + 1] is >= '0' and <= '7')
                    {
                        value = value * 8 + (encoded[++i] - '0');
                        digits++;
                    }
                    builder.Append((char)value);
                    break;
                }
                default:
                    // Preserve malformed/unknown input rather than silently
                    // changing a diagnostic or device path.
                    builder.Append('\\').Append(escape);
                    break;
            }
        }
        return builder.ToString();
    }

    private static int HexValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => -1,
    };

    private sealed record MiCommandResult(bool Success, string Record);

    private sealed class PhaseCapture(Action<GdbLine>? onLine)
    {
        private readonly BoundedGdbLineBuffer _lines = new();

        public void Add(string text, GdbStream stream)
        {
            text = BoundedGdbLineBuffer.TruncateLine(text);
            var line = new GdbLine(DateTime.UtcNow, stream, text);
            _lines.Add(line);
            try { onLine?.Invoke(line); }
            catch { }
        }

        public GdbRunResult Snapshot(int exitCode, bool timedOut, TimeSpan duration) =>
            new(exitCode, timedOut, duration, _lines.Snapshot());
    }
}
