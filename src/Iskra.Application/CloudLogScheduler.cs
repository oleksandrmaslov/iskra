using System.Threading.Channels;
using Iskra.Core;

namespace Iskra.Application;

/// <summary>
/// Process-lifetime scheduler for the durable SQLite shipping queue. It runs
/// immediately at startup, then at the persisted interval, never overlaps two
/// uploads, reacts to saved settings, and cancels in-flight HTTP on shutdown.
/// Queue durability remains in SQLite, so closing/restarting the app does not
/// lose work.
/// </summary>
public sealed class CloudLogScheduler : IDisposable
{
    public const int MinimumIntervalMinutes = 1;
    public const int MaximumIntervalMinutes = AppSettings.MaxLogShipIntervalMinutes;

    private readonly Func<AppSettings> _settingsSnapshot;
    private readonly Func<AppSettings, CancellationToken, Task<CloudShipResult>> _ship;
    private readonly Action<CloudShipResult>? _completed;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    });
    private Task? _loop;
    private int _started;
    private int _disposed;

    public CloudLogScheduler(
        Func<AppSettings> settingsSnapshot,
        Func<AppSettings, CancellationToken, Task<CloudShipResult>> ship,
        Action<CloudShipResult>? completed = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _settingsSnapshot = settingsSnapshot ?? throw new ArgumentNullException(nameof(settingsSnapshot));
        _ship = ship ?? throw new ArgumentNullException(nameof(ship));
        _completed = completed;
        _delay = delay ?? Task.Delay;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _loop = RunAsync(_shutdown.Token);
    }

    /// <summary>Wake the loop after settings are durably saved.</summary>
    public void NotifySettingsChanged()
    {
        if (Volatile.Read(ref _disposed) == 0)
            _wake.Writer.TryWrite(true);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            AppSettings settings;
            try
            {
                settings = _settingsSnapshot().Clone();
                var result = await _ship(settings, cancellationToken).ConfigureAwait(false);
                try { _completed?.Invoke(result); }
                catch { /* presentation callbacks cannot stop the scheduler */ }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var failed = new CloudShipResult(
                    CloudShipStatus.Failed,
                    0,
                    0,
                    0,
                    0,
                    null,
                    ex.Message);
                try { _completed?.Invoke(failed); }
                catch { }
                settings = new AppSettings();
            }

            var minutes = Math.Clamp(
                settings.LogShipIntervalMinutes,
                MinimumIntervalMinutes,
                MaximumIntervalMinutes);
            using var cycle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delayTask = _delay(TimeSpan.FromMinutes(minutes), cycle.Token);
            var wakeTask = _wake.Reader.ReadAsync(cycle.Token).AsTask();
            try
            {
                await Task.WhenAny(delayTask, wakeTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            finally
            {
                cycle.Cancel();
                try { await delayTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
                try { await wakeTask.ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException) { }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        _wake.Writer.TryComplete();
        var loop = _loop;
        if (loop is null)
        {
            _shutdown.Dispose();
        }
        else
        {
            _ = loop.ContinueWith(
                _ => _shutdown.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
