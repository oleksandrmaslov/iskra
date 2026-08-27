using System.Collections.Concurrent;
using Iskra.Application;
using Iskra.Core;

namespace Iskra.Application.Tests;

public sealed class CloudLogSchedulerTests
{
    [Fact]
    public async Task Starts_immediately_never_overlaps_and_wakes_after_settings_change()
    {
        var settings = new AppSettings { LogShipIntervalMinutes = 7 };
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var intervals = new ConcurrentQueue<TimeSpan>();
        var active = 0;
        var maximumActive = 0;
        var calls = 0;

        Task Delay(TimeSpan interval, CancellationToken cancellationToken)
        {
            intervals.Enqueue(interval);
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        async Task<CloudShipResult> Ship(AppSettings _, CancellationToken cancellationToken)
        {
            var nowActive = Interlocked.Increment(ref active);
            maximumActive = Math.Max(maximumActive, nowActive);
            var call = Interlocked.Increment(ref calls);
            try
            {
                if (call == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }
                if (call == 2) secondFinished.TrySetResult();
                return Shipped();
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }

        using var scheduler = new CloudLogScheduler(
            () => settings,
            Ship,
            delay: Delay);
        scheduler.Start();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        scheduler.NotifySettingsChanged();
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref calls));
        releaseFirst.TrySetResult();
        await secondFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, maximumActive);
        Assert.Contains(TimeSpan.FromMinutes(7), intervals);
    }

    [Fact]
    public async Task Dispose_cancels_an_inflight_ship()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new CloudLogScheduler(
            () => new AppSettings(),
            async (_, cancellationToken) =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                    throw;
                }
                return Shipped();
            });
        scheduler.Start();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        scheduler.Dispose();

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(0, CloudLogScheduler.MinimumIntervalMinutes)]
    [InlineData(1441, CloudLogScheduler.MaximumIntervalMinutes)]
    public async Task Defensive_interval_clamp_bounds_the_scheduler_delay(
        int configuredMinutes,
        int expectedMinutes)
    {
        var observed = new TaskCompletionSource<TimeSpan>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task Delay(TimeSpan interval, CancellationToken cancellationToken)
        {
            observed.TrySetResult(interval);
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        using var scheduler = new CloudLogScheduler(
            () => new AppSettings { LogShipIntervalMinutes = configuredMinutes },
            (_, _) => Task.FromResult(Shipped()),
            delay: Delay);

        scheduler.Start();

        Assert.Equal(
            TimeSpan.FromMinutes(expectedMinutes),
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Start_is_idempotent()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var scheduler = new CloudLogScheduler(
            () => new AppSettings(),
            async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref calls);
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Shipped();
            });

        scheduler.Start();
        scheduler.Start();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, Volatile.Read(ref calls));
    }

    private static CloudShipResult Shipped() => new(
        CloudShipStatus.Shipped,
        0,
        0,
        0,
        0,
        null,
        null);
}
