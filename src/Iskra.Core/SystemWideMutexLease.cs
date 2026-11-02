namespace Iskra.Core;

/// <summary>
/// Owns a named operating-system mutex on a dedicated thread.
///
/// <para><see cref="Mutex"/> ownership is thread-affine, while callers are
/// asynchronous and can resume on any pool thread. The dedicated owner thread
/// therefore performs both acquisition and release. Callers only hold this
/// lease and signal when their protected operation is complete.</para>
/// </summary>
internal sealed class SystemWideMutexLease : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// How long each ownership attempt blocks before cancellation is rechecked.
    /// Short enough that a cancelled sign-in returns promptly, long enough that
    /// an uncontended wait is a single blocking call.
    /// </summary>
    private static readonly TimeSpan OwnershipPollInterval = TimeSpan.FromMilliseconds(50);

    private readonly ManualResetEventSlim _release = new(initialState: false);
    private readonly TaskCompletionSource<bool> _ready = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _ownerThread;
    private readonly string _mutexName;
    private readonly bool _currentUserOnly;
    private readonly bool _waitForOwnership;
    private readonly CancellationToken _cancellationToken;
    private int _disposed;

    private SystemWideMutexLease(
        string mutexName,
        bool currentUserOnly,
        bool waitForOwnership,
        CancellationToken cancellationToken)
    {
        _mutexName = mutexName;
        _currentUserOnly = currentUserOnly;
        _waitForOwnership = waitForOwnership;
        _cancellationToken = cancellationToken;
        _ownerThread = new Thread(OwnMutex)
        {
            IsBackground = true,
            Name = "Iskra system-wide mutex owner",
        };
    }

    /// <summary>
    /// Attempts immediate acquisition. Returns <c>null</c> when another owner
    /// holds the mutex. Creation or ACL failures are surfaced to the caller.
    /// </summary>
    internal static SystemWideMutexLease? TryAcquire(
        string mutexName,
        bool currentUserOnly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);

        var lease = new SystemWideMutexLease(
            mutexName,
            currentUserOnly,
            waitForOwnership: false,
            CancellationToken.None);
        lease._ownerThread.Start();

        try
        {
            return lease._ready.Task.GetAwaiter().GetResult() ? lease : DisposeAndReturnNull(lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Waits until the named mutex is acquired or cancellation is requested.
    /// </summary>
    internal static async Task<SystemWideMutexLease> AcquireAsync(
        string mutexName,
        bool currentUserOnly,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        cancellationToken.ThrowIfCancellationRequested();

        var lease = new SystemWideMutexLease(
            mutexName,
            currentUserOnly,
            waitForOwnership: true,
            cancellationToken);
        lease._ownerThread.Start();

        try
        {
            if (!await lease._ready.Task.ConfigureAwait(false))
                throw new InvalidOperationException($"named mutex '{mutexName}' was not acquired");

            // Cancellation may race with ownership transfer. Never return a
            // lease to a caller that has already abandoned the operation.
            cancellationToken.ThrowIfCancellationRequested();
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static SystemWideMutexLease? DisposeAndReturnNull(SystemWideMutexLease lease)
    {
        lease.Dispose();
        return null;
    }

    private void OwnMutex()
    {
        Mutex? mutex = null;
        var acquired = false;
        try
        {
            var options = new NamedWaitHandleOptions
            {
                CurrentSessionOnly = false,
                CurrentUserOnly = _currentUserOnly,
            };
            mutex = new Mutex(initiallyOwned: false, _mutexName, options, out _);

            try
            {
                if (_waitForOwnership)
                {
                    if (!WaitForOwnership(mutex))
                    {
                        _ready.TrySetCanceled(_cancellationToken);
                        return;
                    }

                    acquired = true;
                }
                else
                {
                    acquired = mutex.WaitOne(millisecondsTimeout: 0);
                }
            }
            catch (AbandonedMutexException)
            {
                // The OS transfers an abandoned mutex to this thread.
                acquired = true;
            }

            _ready.TrySetResult(acquired);
            if (!acquired) return;

            _release.Wait();
            mutex.ReleaseMutex();
        }
        catch (Exception ex)
        {
            _ready.TrySetException(new InvalidOperationException(
                $"could not acquire named mutex '{_mutexName}'", ex));
        }
        finally
        {
            mutex?.Dispose();
            _ready.TrySetResult(acquired);
        }
    }

    /// <summary>
    /// Waits for the mutex while staying responsive to cancellation. Returns
    /// <c>false</c> only when cancellation was requested first.
    /// <para>This polls rather than waiting on the mutex and the cancellation
    /// handle together, because a combined wait is not portable:
    /// <c>WaitHandle.WaitAny</c> over a set that contains a *named* primitive
    /// throws <see cref="PlatformNotSupportedException"/> on Unix. Doing that
    /// here disabled every credential mutation on Linux and macOS — sign-in,
    /// refresh, and sign-out all failed with the generic
    /// "could not establish the cross-process GitHub credential lock".</para>
    /// <para>The poll costs nothing in practice: this lock is contended only
    /// when two Iskra processes mutate the same stored credential at once, and
    /// the operation it guards is a network round-trip.</para>
    /// </summary>
    private bool WaitForOwnership(Mutex mutex)
    {
        while (!_cancellationToken.IsCancellationRequested)
        {
            // An AbandonedMutexException here means the OS handed us ownership
            // of a mutex whose previous owner died; the caller's catch treats
            // that as acquired, exactly as it did for the combined wait.
            if (mutex.WaitOne(OwnershipPollInterval)) return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _release.Set();
        if (_ownerThread.IsAlive && Thread.CurrentThread != _ownerThread)
            _ownerThread.Join();
        _release.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
