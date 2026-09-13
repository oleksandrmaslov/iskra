using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Iskra.Core;

/// <summary>
/// Serializes every credential mutation for one secure-store identity, both
/// within this process and across processes for the current OS user. Device
/// Flow refresh tokens rotate, and a concurrent logout or new login must not
/// be overwritten by an older refresh completing later.
/// </summary>
internal static class TokenStoreOperationLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessLocks =
        new(StringComparer.Ordinal);

    internal static async Task<T> RunAsync<T>(
        ITokenStore store,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(operation);

        var mutexName = MutexName(store.Path);
        var processLock = ProcessLocks.GetOrAdd(
            mutexName, static _ => new SemaphoreSlim(1, 1));
        await processLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var systemLock = await AcquireSystemLockAsync(
                mutexName, cancellationToken).ConfigureAwait(false);
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            processLock.Release();
        }
    }

    internal static T Run<T>(ITokenStore store, Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(operation);

        var mutexName = MutexName(store.Path);
        var processLock = ProcessLocks.GetOrAdd(
            mutexName, static _ => new SemaphoreSlim(1, 1));
        processLock.Wait();
        try
        {
            using var systemLock = AcquireSystemLockAsync(
                    mutexName, CancellationToken.None)
                .GetAwaiter().GetResult();
            return operation();
        }
        finally
        {
            processLock.Release();
        }
    }

    internal static string MutexName(string storeIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeIdentity);

        var normalized = storeIdentity.Trim();
        if (Path.IsPathRooted(normalized))
        {
            normalized = Path.GetFullPath(normalized);
            if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"Iskra.TokenMutation.v1.{Convert.ToHexString(digest)}";
    }

    private static async Task<SystemWideMutexLease> AcquireSystemLockAsync(
        string mutexName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SystemWideMutexLease.AcquireAsync(
                mutexName,
                currentUserOnly: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // Name the underlying cause. Without it this surfaces as an
            // unactionable wall on the operator's screen and in support logs --
            // the platform-specific reason is the only thing that identifies
            // whether the store, the OS, or contention is at fault.
            var cause = (ex.InnerException ?? ex).Message;
            throw new TokenStoreException(
                "could not establish the cross-process GitHub credential lock; " +
                "authentication is disabled to protect rotating credentials " +
                $"({cause})",
                ex);
        }
    }
}
