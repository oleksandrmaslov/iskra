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

        var lines = new List<GdbLine>();
        var sync = new object();

        void Capture(string? text, GdbStream stream)
        {
            if (text is null) return;
            var line = new GdbLine(DateTime.UtcNow, stream, text);
            lock (sync) lines.Add(line);
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
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            try { await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* swallow */ }
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
