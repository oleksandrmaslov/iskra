using Iskra.Core;

namespace Iskra.Application;

public enum HistoryLoadStatus
{
    Loaded,
    DatabaseMissing,
    Failed,
}

public sealed record HistoryBatchCounts(int Total, int Pass, int Fail)
{
    public double PassRate => Total == 0 ? 0 : (double)Pass / Total;
}

public sealed record HistorySnapshot(
    HistoryLoadStatus Status,
    string DatabasePath,
    IReadOnlyList<FlashAttemptRow> Rows,
    bool BatchesEnabled,
    string? BatchId,
    HistoryBatchCounts? BatchCounts,
    string? Diagnostic);

public enum BatchLockLookupStatus
{
    BatchesDisabled,
    BatchRequired,
    DatabaseMissing,
    NotReserved,
    Reserved,
    Failed,
}

public sealed record BatchLockSnapshot(
    BatchLockLookupStatus Status,
    string DatabasePath,
    string? BatchId,
    BatchLockDescriptor? Lock,
    string? Diagnostic);

public enum HistoryExportScope
{
    All,
    CurrentBatch,
}

public enum HistoryExportStatus
{
    Exported,
    BatchesDisabled,
    BatchRequired,
    DatabaseMissing,
    Failed,
}

public sealed record HistoryExportResult(
    HistoryExportStatus Status,
    string OutputPath,
    string? BatchId,
    int RowsWritten,
    string? Diagnostic);

/// <summary>
/// Read/export orchestration shared by WPF and the read-only Avalonia alpha.
/// File dialogs and localized presentation remain frontend responsibilities.
/// </summary>
public sealed class HistoryWorkflow
{
    public HistorySnapshot Load(AppSettings settings, string? enteredBatchId, int limit = 200)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));

        var databasePath = ApplicationPaths.ResolveDatabasePath(settings);
        var batchId = settings.BatchesEnabled ? Normalize(enteredBatchId) : null;
        if (!File.Exists(databasePath))
        {
            return new HistorySnapshot(
                HistoryLoadStatus.DatabaseMissing,
                databasePath,
                Array.Empty<FlashAttemptRow>(),
                settings.BatchesEnabled,
                batchId,
                null,
                null);
        }

        try
        {
            using var store = new SqliteLogStore(databasePath);
            var rows = store.QueryRecent(limit);
            HistoryBatchCounts? counts = null;
            if (batchId is not null)
            {
                var (total, pass, fail) = store.CountsForBatch(batchId);
                counts = new HistoryBatchCounts(total, pass, fail);
            }

            return new HistorySnapshot(
                HistoryLoadStatus.Loaded,
                databasePath,
                rows,
                settings.BatchesEnabled,
                batchId,
                counts,
                null);
        }
        catch (Exception ex)
        {
            return new HistorySnapshot(
                HistoryLoadStatus.Failed,
                databasePath,
                Array.Empty<FlashAttemptRow>(),
                settings.BatchesEnabled,
                batchId,
                null,
                ex.Message);
        }
    }

    public BatchLockSnapshot LookupBatchLock(AppSettings settings, string? enteredBatchId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var databasePath = ApplicationPaths.ResolveDatabasePath(settings);
        if (!settings.BatchesEnabled)
            return new(BatchLockLookupStatus.BatchesDisabled, databasePath, null, null, null);

        var batchId = Normalize(enteredBatchId);
        if (batchId is null)
            return new(BatchLockLookupStatus.BatchRequired, databasePath, null, null, null);
        if (!File.Exists(databasePath))
            return new(BatchLockLookupStatus.DatabaseMissing, databasePath, batchId, null, null);

        try
        {
            using var store = new SqliteLogStore(databasePath);
            var locked = store.GetBatchLock(batchId);
            return locked is null
                ? new(BatchLockLookupStatus.NotReserved, databasePath, batchId, null, null)
                : new(BatchLockLookupStatus.Reserved, databasePath, batchId, locked, null);
        }
        catch (Exception ex)
        {
            return new(BatchLockLookupStatus.Failed, databasePath, batchId, null, ex.Message);
        }
    }

    public HistoryExportResult Export(
        AppSettings settings,
        string? enteredBatchId,
        HistoryExportScope scope,
        string outputPath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var batchId = scope == HistoryExportScope.CurrentBatch
            ? Normalize(enteredBatchId)
            : null;
        if (scope == HistoryExportScope.CurrentBatch && !settings.BatchesEnabled)
            return new(HistoryExportStatus.BatchesDisabled, outputPath, null, 0, null);
        if (scope == HistoryExportScope.CurrentBatch && batchId is null)
            return new(HistoryExportStatus.BatchRequired, outputPath, null, 0, null);

        var databasePath = ApplicationPaths.ResolveDatabasePath(settings);
        if (!File.Exists(databasePath))
            return new(HistoryExportStatus.DatabaseMissing, outputPath, batchId, 0, null);

        try
        {
            using var store = new SqliteLogStore(databasePath);
            var rows = store.ExportCsv(outputPath, batchId);
            return new(HistoryExportStatus.Exported, outputPath, batchId, rows, null);
        }
        catch (Exception ex)
        {
            return new(HistoryExportStatus.Failed, outputPath, batchId, 0, ex.Message);
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
