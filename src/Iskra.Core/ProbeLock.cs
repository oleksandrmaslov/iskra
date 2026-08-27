namespace Iskra.Core;

/// <summary>
/// Holds one machine-wide named mutex for a physical probe.
/// </summary>
internal sealed class ProbeLock : IDisposable, IAsyncDisposable
{
    private readonly SystemWideMutexLease _lease;

    private ProbeLock(SystemWideMutexLease lease)
    {
        _lease = lease;
    }

    public static ProbeLock? TryAcquire(string mutexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);

        try
        {
            // Machine-wide (not per-user) is intentional: a second OS account
            // must not be able to open the same physical probe concurrently.
            var lease = SystemWideMutexLease.TryAcquire(mutexName, currentUserOnly: false);
            return lease is null ? null : new ProbeLock(lease);
        }
        catch
        {
            // An ACL/platform/backing-store failure must never fall back to a
            // weaker per-user lock.  Report unavailable to the caller instead.
            return null;
        }
    }

    public void Dispose() => _lease.Dispose();

    public ValueTask DisposeAsync() => _lease.DisposeAsync();
}
