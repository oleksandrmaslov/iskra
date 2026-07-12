using Iskra.Core;

namespace Iskra.Application;

/// <summary>
/// Result of applying the optional production-batch policy to operator input.
/// The application layer, rather than either desktop toolkit, owns these
/// semantics so WPF, Avalonia, and future clients cannot drift.
/// </summary>
public sealed record BatchPolicyResult(
    bool BatchesEnabled,
    bool IsValid,
    string EffectiveBatchId,
    string? ErrorCode)
{
    /// <summary>Whether the durable batch reservation must run before flashing.</summary>
    public bool ShouldReserve => BatchesEnabled && IsValid;
}

public static class BatchPolicy
{
    public const string RequiredErrorCode = "E_BATCH_REQUIRED";

    /// <summary>
    /// Disabled mode always produces a blank effective ID and never reserves a
    /// batch, even if stale text remains in a hidden input. Enabled mode requires
    /// a non-empty ID and canonicalises it by trimming surrounding whitespace.
    /// </summary>
    public static BatchPolicyResult Resolve(bool batchesEnabled, string? enteredBatchId)
    {
        if (!batchesEnabled)
        {
            return new BatchPolicyResult(false, true, string.Empty, null);
        }

        var trimmed = enteredBatchId?.Trim() ?? string.Empty;
        return trimmed.Length == 0
            ? new BatchPolicyResult(true, false, string.Empty, RequiredErrorCode)
            : new BatchPolicyResult(true, true, trimmed, null);
    }

    public static BatchPolicyResult Resolve(AppSettings settings, string? enteredBatchId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Resolve(settings.BatchesEnabled, enteredBatchId);
    }
}
