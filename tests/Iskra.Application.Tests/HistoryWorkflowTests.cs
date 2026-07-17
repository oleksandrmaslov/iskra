using Iskra.Application;
using Iskra.Core;

namespace Iskra.Application.Tests;

public sealed class HistoryWorkflowTests
{
    [Fact]
    public void Missing_database_returns_structured_empty_state_without_creating_file()
    {
        using var scope = new TempScope();
        var settings = new AppSettings { DbPath = scope.DatabasePath };

        var result = new HistoryWorkflow().Load(settings, null);

        Assert.Equal(HistoryLoadStatus.DatabaseMissing, result.Status);
        Assert.Empty(result.Rows);
        Assert.False(File.Exists(scope.DatabasePath));
    }

    [Fact]
    public void Load_returns_recent_rows_and_current_batch_counts()
    {
        using var scope = new TempScope();
        using (var store = new SqliteLogStore(scope.DatabasePath))
        {
            store.Append(Attempt("LOT-1", FlashResult.Pass), reserveBatchLock: false);
            store.Append(Attempt("LOT-1", FlashResult.Fail), reserveBatchLock: false);
            store.Append(Attempt("LOT-2", FlashResult.Pass), reserveBatchLock: false);
        }

        var settings = new AppSettings { DbPath = scope.DatabasePath, BatchesEnabled = true };
        var result = new HistoryWorkflow().Load(settings, "  LOT-1  ");

        Assert.Equal(HistoryLoadStatus.Loaded, result.Status);
        Assert.Equal(3, result.Rows.Count);
        Assert.Equal("LOT-1", result.BatchId);
        Assert.Equal(new HistoryBatchCounts(2, 1, 1), result.BatchCounts);
        Assert.Equal(0.5, result.BatchCounts!.PassRate);
    }

    [Fact]
    public void Corrupt_database_returns_failure_instead_of_throwing()
    {
        using var scope = new TempScope();
        File.WriteAllText(scope.DatabasePath, "not sqlite");

        var result = new HistoryWorkflow().Load(
            new AppSettings { DbPath = scope.DatabasePath },
            null);

        Assert.Equal(HistoryLoadStatus.Failed, result.Status);
        Assert.NotEmpty(result.Diagnostic!);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void Batch_export_guards_run_before_output_creation()
    {
        using var scope = new TempScope();
        var output = Path.Combine(scope.DirectoryPath, "batch.csv");
        var workflow = new HistoryWorkflow();

        var disabled = workflow.Export(
            new AppSettings { DbPath = scope.DatabasePath, BatchesEnabled = false },
            "LOT-1",
            HistoryExportScope.CurrentBatch,
            output);
        var missingId = workflow.Export(
            new AppSettings { DbPath = scope.DatabasePath, BatchesEnabled = true },
            " ",
            HistoryExportScope.CurrentBatch,
            output);

        Assert.Equal(HistoryExportStatus.BatchesDisabled, disabled.Status);
        Assert.Equal(HistoryExportStatus.BatchRequired, missingId.Status);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Export_writes_requested_batch_only()
    {
        using var scope = new TempScope();
        using (var store = new SqliteLogStore(scope.DatabasePath))
        {
            store.Append(Attempt("LOT-1", FlashResult.Pass), reserveBatchLock: false);
            store.Append(Attempt("LOT-2", FlashResult.Fail), reserveBatchLock: false);
        }
        var output = Path.Combine(scope.DirectoryPath, "batch.csv");

        var result = new HistoryWorkflow().Export(
            new AppSettings { DbPath = scope.DatabasePath, BatchesEnabled = true },
            "LOT-1",
            HistoryExportScope.CurrentBatch,
            output);

        Assert.Equal(HistoryExportStatus.Exported, result.Status);
        Assert.Equal(1, result.RowsWritten);
        var csv = File.ReadAllText(output);
        Assert.Contains("LOT-1", csv);
        Assert.DoesNotContain("LOT-2", csv);
    }

    [Fact]
    public void Revoked_or_conflicting_rows_do_not_become_batch_locks_on_lookup()
    {
        using var scope = new TempScope();
        using (var store = new SqliteLogStore(scope.DatabasePath))
        {
            store.Append(
                Attempt("LOT-1", FlashResult.Fail) with { ErrorCode = "E_RELEASE_REVOKED" },
                reserveBatchLock: false);
        }

        var result = new HistoryWorkflow().LookupBatchLock(
            new AppSettings { DbPath = scope.DatabasePath, BatchesEnabled = true },
            "LOT-1");

        Assert.Equal(BatchLockLookupStatus.NotReserved, result.Status);
        Assert.Null(result.Lock);
    }

    private static FlashAttemptRecord Attempt(string batch, FlashResult result) => new(
        DateTime.UtcNow,
        "operator",
        "station",
        batch,
        "ci-clop",
        "1.0.0",
        new string('a', 64),
        "PY32Fxxx",
        "PY32Fxxx M0+",
        32,
        "COM30",
        "BMP-1",
        PowerMode.External,
        false,
        1_000_000,
        result,
        result == FlashResult.Fail ? "E_VERIFY_MISMATCH" : null,
        null,
        50,
        null);

    private sealed class TempScope : IDisposable
    {
        public TempScope()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), $"iskra-history-{Guid.NewGuid():N}");
            Directory.CreateDirectory(DirectoryPath);
            DatabasePath = Path.Combine(DirectoryPath, "history.db");
        }

        public string DirectoryPath { get; }
        public string DatabasePath { get; }

        public void Dispose()
        {
            try { Directory.Delete(DirectoryPath, recursive: true); } catch { }
        }
    }
}
