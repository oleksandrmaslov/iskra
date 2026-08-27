using System.Diagnostics;
using System.Text;

namespace Iskra.Core;

public enum GdbStream { Stdout, Stderr }

public sealed record GdbLine(DateTime UtcTimestamp, GdbStream Stream, string Text);

public sealed record GdbRunResult(
    int ExitCode,
    bool TimedOut,
    TimeSpan Duration,
    IReadOnlyList<GdbLine> Output)
{
    public string Tail(int maxLines = 40)
    {
        var slice = Output.Count <= maxLines
            ? Output
            : Output.Skip(Output.Count - maxLines).ToList();
        return string.Join("\n", slice.Select(l => l.Text));
    }
}

/// <summary>
/// Owns an <c>arm-none-eabi-gdb</c> process and captures stdout/stderr
/// line-by-line. The production flash path uses <see cref="RunGuardedAsync"/>
/// so scan and flash share one GDB/remote connection; the batch helpers remain
/// for diagnostics and compatibility only.
/// </summary>
internal class GdbProcess
{
    public const int MaxCapturedLines = 4_096;
    public const int MaxCapturedLineChars = 8_192;
    public const int MaxCapturedChars = 1_048_576;

    private readonly string _gdbExe;

    public GdbProcess(string gdbExe)
    {
        if (string.IsNullOrWhiteSpace(gdbExe))
            throw new ArgumentException("gdbExe required", nameof(gdbExe));
        _gdbExe = gdbExe;
    }

    public async Task<GdbRunResult> RunAsync(
        IEnumerable<string> processArgs,
        TimeSpan timeout,
        Action<GdbLine>? onLine = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _gdbExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in processArgs) psi.ArgumentList.Add(a);

        var lines = new BoundedGdbLineBuffer();

        void Capture(BoundedProcessLine captured, GdbStream stream)
        {
            var line = new GdbLine(DateTime.UtcNow, stream, captured.Text);
            lines.Add(line);
            try { onLine?.Invoke(line); }
            catch { }
        }

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var sw = Stopwatch.StartNew();
        if (!proc.Start())
            throw new InvalidOperationException($"Failed to start gdb: {_gdbExe}");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var stdoutTask = BoundedProcessLineReader.ReadAsync(
            proc.StandardOutput,
            line => Capture(line, GdbStream.Stdout),
            cts.Token);
        var stderrTask = BoundedProcessLineReader.ReadAsync(
            proc.StandardError,
            line => Capture(line, GdbStream.Stderr),
            cts.Token);

        bool timedOut = false;
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            cts.CancelAfter(Timeout.InfiniteTimeSpan);
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var callerCancelled = ct.IsCancellationRequested;
            timedOut = !callerCancelled;
            await GdbMiSession.TerminateProcessTreeAsync(proc, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await ObserveReadersAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            if (callerCancelled) throw;
        }
        catch
        {
            // Any exceptional exit must release the probe. In particular, app
            // shutdown/caller cancellation must not leave GDB running against
            // a target in the background.
            await GdbMiSession.TerminateProcessTreeAsync(proc, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            cts.Cancel();
            await ObserveReadersAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }

        sw.Stop();

        var snapshot = lines.Snapshot();

        return new GdbRunResult(
            ExitCode: timedOut ? -1 : proc.ExitCode,
            TimedOut: timedOut,
            Duration: sw.Elapsed,
            Output: snapshot);
    }

    private static async Task ObserveReadersAsync(params Task[] readers)
    {
        try { await Task.WhenAll(readers).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Runs the production guarded transaction in one GDB/MI process. The
    /// supplied gate sees the completed <c>swdp_scan</c> snapshot after target
    /// #1 has been safely attached and held on that same remote connection.
    /// Firmware is not opened and no load/verify command is sent unless the
    /// gate returns true.
    /// </summary>
    public virtual Task<GdbGuardedRunResult> RunGuardedAsync(
        string endpoint,
        PowerMode power,
        int frequencyHz,
        bool connectUnderReset,
        string firmwarePath,
        TimeSpan scanTimeout,
        TimeSpan flashTimeout,
        Func<GdbRunResult, bool> scanGate,
        Action<GdbLine>? onLine = null,
        CancellationToken ct = default,
        string? probeLockIdentity = null)
    {
        return GdbMiSession.RunAsync(
            _gdbExe,
            endpoint,
            power,
            frequencyHz,
            connectUnderReset,
            firmwarePath,
            scanTimeout,
            flashTimeout,
            scanGate,
            onLine,
            ct,
            probeLockIdentity);
    }

    /// <summary>
    /// Convenience: build args from flash options and run.
    /// </summary>
    public virtual Task<GdbRunResult> RunFlashAsync(
        string comPort,
        PowerMode power,
        int frequencyHz,
        bool connectUnderReset,
        string elfPath,
        TimeSpan timeout,
        Action<GdbLine>? onLine = null,
        CancellationToken ct = default)
    {
        var args = GdbCommandBuilder.BuildProcessArgs(
            comPort, power, frequencyHz, connectUnderReset, elfPath);
        return RunAsync(args, timeout, onLine, ct);
    }

    /// <summary>
    /// Scan-only phase: connect, set probe options, run <c>swdp_scan</c>, quit.
    /// Does not touch flash on the target. Used before <see cref="RunFlashAsync"/>
    /// to abort safely on wrong-target-family boards.
    /// </summary>
    public virtual Task<GdbRunResult> RunScanAsync(
        string comPort,
        PowerMode power,
        int frequencyHz,
        bool connectUnderReset,
        TimeSpan timeout,
        Action<GdbLine>? onLine = null,
        CancellationToken ct = default)
    {
        var args = GdbCommandBuilder.BuildScanProcessArgs(
            comPort, power, frequencyHz, connectUnderReset);
        return RunAsync(args, timeout, onLine, ct);
    }
}

/// <summary>
/// Thread-safe recent-output retention shared by batch and MI process capture.
/// Both line count and aggregate characters are bounded, so many maximum-size
/// child-process lines cannot consume hundreds of MiB.
/// </summary>
internal sealed class BoundedGdbLineBuffer
{
    private const string TruncationSuffix = "…[truncated]";
    private readonly Queue<GdbLine> _lines = new();
    private readonly object _sync = new();
    private int _retainedChars;

    public void Add(GdbLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        line = line with { Text = TruncateLine(line.Text) };
        lock (_sync)
        {
            while (_lines.Count > 0
                && (_lines.Count >= GdbProcess.MaxCapturedLines
                    || _retainedChars + line.Text.Length > GdbProcess.MaxCapturedChars))
            {
                _retainedChars -= _lines.Dequeue().Text.Length;
            }
            _lines.Enqueue(line);
            _retainedChars += line.Text.Length;
        }
    }

    public IReadOnlyList<GdbLine> Snapshot()
    {
        lock (_sync) return _lines.ToArray();
    }

    internal int RetainedChars
    {
        get { lock (_sync) return _retainedChars; }
    }

    internal static string TruncateLine(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= GdbProcess.MaxCapturedLineChars) return text;
        return text[..(GdbProcess.MaxCapturedLineChars - TruncationSuffix.Length)]
            + TruncationSuffix;
    }

    internal static string MarkTruncated(string retainedPrefix)
    {
        ArgumentNullException.ThrowIfNull(retainedPrefix);
        var prefixLength = Math.Min(
            retainedPrefix.Length,
            GdbProcess.MaxCapturedLineChars - TruncationSuffix.Length);
        return retainedPrefix[..prefixLength] + TruncationSuffix;
    }
}
