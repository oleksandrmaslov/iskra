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
/// Spawns <c>arm-none-eabi-gdb.exe --batch</c> and captures stdout/stderr line-by-line.
/// Knows nothing about target families or product IDs — pure process wrapper.
/// The state machine layer interprets the captured lines.
/// </summary>
public class GdbProcess
{
    public const int MaxCapturedLines = 10_000;
    public const int MaxCapturedLineChars = 16_384;

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

        var lines = new Queue<GdbLine>();
        var sync = new object();

        void Capture(string? text, GdbStream stream)
        {
            if (text is null) return;
            if (text.Length > MaxCapturedLineChars)
                text = text[..MaxCapturedLineChars] + "…[truncated]";
            var line = new GdbLine(DateTime.UtcNow, stream, text);
            lock (sync)
            {
                if (lines.Count == MaxCapturedLines) lines.Dequeue();
                lines.Enqueue(line);
            }
            onLine?.Invoke(line);
        }

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => Capture(e.Data, GdbStream.Stdout);
        proc.ErrorDataReceived  += (_, e) => Capture(e.Data, GdbStream.Stderr);

        var sw = Stopwatch.StartNew();
        if (!proc.Start())
            throw new InvalidOperationException($"Failed to start gdb: {_gdbExe}");

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        bool timedOut = false;
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var callerCancelled = ct.IsCancellationRequested;
            timedOut = !callerCancelled;
            await TerminateProcessTreeAsync(proc).ConfigureAwait(false);
            if (callerCancelled) throw;
        }
        catch
        {
            // Any exceptional exit must release the probe. In particular, app
            // shutdown/caller cancellation must not leave GDB running against
            // a target in the background.
            await TerminateProcessTreeAsync(proc).ConfigureAwait(false);
            throw;
        }

        sw.Stop();

        // Drain any buffered async output before snapshotting.
        try { proc.WaitForExit(); } catch { /* already exited */ }

        IReadOnlyList<GdbLine> snapshot;
        lock (sync) snapshot = lines.ToArray();

        return new GdbRunResult(
            ExitCode: timedOut ? -1 : proc.ExitCode,
            TimedOut: timedOut,
            Duration: sw.Elapsed,
            Output: snapshot);
    }

    private static async Task TerminateProcessTreeAsync(Process proc)
    {
        try
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        }
        catch { /* best-effort termination */ }

        try
        {
            if (!proc.HasExited)
                await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch { /* process may already be gone */ }
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
