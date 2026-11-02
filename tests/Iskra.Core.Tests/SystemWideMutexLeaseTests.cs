using System.Diagnostics;
using Iskra.Core;

namespace Iskra.Core.Tests;

/// <summary>
/// Cross-process credential locking has to behave identically on Windows,
/// Linux, and macOS — every Device Flow sign-in, refresh, and sign-out goes
/// through it. These tests are behavioural on purpose: the bug they were
/// written for (waiting on a named mutex together with a cancellation handle,
/// which throws <see cref="PlatformNotSupportedException"/> on Unix) compiled
/// and passed on Windows while disabling authentication entirely on Linux and
/// macOS. CI runs this suite natively on all three, so the platform difference
/// is caught here rather than by an operator.
/// </summary>
public class SystemWideMutexLeaseTests
{
    private static string UniqueName() => $"Iskra.Tests.{Guid.NewGuid():N}";

    [Fact]
    public async Task AcquireAsync_acquires_releases_and_can_be_reacquired()
    {
        var name = UniqueName();

        await using (var lease = await SystemWideMutexLease.AcquireAsync(name, currentUserOnly: true))
        {
            Assert.NotNull(lease);
        }

        await using var again = await SystemWideMutexLease.AcquireAsync(name, currentUserOnly: true);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task Second_waiter_enters_only_after_the_first_releases()
    {
        var name = UniqueName();
        var first = await SystemWideMutexLease.AcquireAsync(name, currentUserOnly: true);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = Task.Run(async () =>
        {
            await using var lease = await SystemWideMutexLease.AcquireAsync(name, currentUserOnly: true);
            entered.SetResult();
        });

        var enteredWhileHeld =
            await Task.WhenAny(entered.Task, Task.Delay(TimeSpan.FromMilliseconds(400)));
        Assert.NotSame(entered.Task, enteredWhileHeld);

        await first.DisposeAsync();

        var completed = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(second, completed);
        await second;
    }

    [Fact]
    public async Task Cancelling_a_blocked_waiter_throws_and_hands_out_no_lease()
    {
        var name = UniqueName();
        await using var held = await SystemWideMutexLease.AcquireAsync(name, currentUserOnly: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await using var blocked = await SystemWideMutexLease.AcquireAsync(
                name, currentUserOnly: true, cts.Token);
        });

        // Cancellation must be observed promptly rather than waiting out the
        // holder, which is what a non-cancellable blocking wait would do.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Already_cancelled_token_never_acquires()
    {
        var name = UniqueName();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await using var lease = await SystemWideMutexLease.AcquireAsync(
                name, currentUserOnly: true, cts.Token);
        });

        // The cancelled attempt must not have left the mutex owned.
        await using var after = await SystemWideMutexLease.AcquireAsync(name, currentUserOnly: true);
        Assert.NotNull(after);
    }

    [Fact]
    public async Task TryAcquire_reports_contention_instead_of_blocking()
    {
        var name = UniqueName();
        using var held = SystemWideMutexLease.TryAcquire(name, currentUserOnly: true);
        Assert.NotNull(held);

        // Contend from another thread: mutex ownership is thread-affine, so a
        // second attempt on the owning thread would re-enter rather than block.
        var contended = await Task.Run(() =>
            SystemWideMutexLease.TryAcquire(name, currentUserOnly: true));
        Assert.Null(contended);

        held!.Dispose();

        var afterRelease = await Task.Run(() =>
            SystemWideMutexLease.TryAcquire(name, currentUserOnly: true));
        Assert.NotNull(afterRelease);
        afterRelease!.Dispose();
    }

    [Fact]
    public async Task Credential_mutations_serialize_through_the_token_store_lock()
    {
        // The caller that actually broke on Unix. Exercised through a stub store
        // so no real secret backend is touched.
        var store = new FakeTokenStore($"iskra-tests://{Guid.NewGuid():N}");
        var concurrent = 0;
        var maxConcurrent = 0;
        var gate = new object();

        async Task<int> Mutate()
        {
            return await TokenStoreOperationLock.RunAsync(store, async _ =>
            {
                lock (gate) maxConcurrent = Math.Max(maxConcurrent, ++concurrent);
                await Task.Delay(25);
                lock (gate) concurrent--;
                return 1;
            });
        }

        var results = await Task.WhenAll(Mutate(), Mutate(), Mutate(), Mutate());

        Assert.Equal(4, results.Sum());
        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public void Synchronous_credential_mutations_run_on_every_platform()
    {
        var store = new FakeTokenStore($"iskra-tests://{Guid.NewGuid():N}");

        var value = TokenStoreOperationLock.Run(store, () => 42);

        Assert.Equal(42, value);
    }

    private sealed class FakeTokenStore(string path) : ITokenStore
    {
        public string Path { get; } = path;
        public bool Exists() => false;
        public StoredTokens? Load() => null;
        public void Save(StoredTokens tokens) { }
        public void Delete() { }
    }
}
