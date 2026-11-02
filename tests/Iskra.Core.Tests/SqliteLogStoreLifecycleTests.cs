using Iskra.Core;
using Microsoft.Data.Sqlite;

namespace Iskra.Core.Tests;

public sealed class SqliteLogStoreLifecycleTests
{
    private static FlashAttemptStartRecord StartedAt(DateTime timestamp) => new(
        TsUtc: timestamp,
        Operator: "operator-1",
        StationId: "station-1",
        BatchId: "",
        ProductId: "ci-clop",
        FirmwareVersion: "1.0.0",
        FirmwareSha256: new string('a', 64),
        TargetBmpMatch: "PY32Fxxx",
        TargetFlashKb: 32,
        ComPort: "COM30",
        ProbeSerial: "BMP-001",
        Power: PowerMode.External,
        ConnectRst: false,
        BmpFrequencyHz: 1_000_000);

    [Fact]
    public void Started_row_is_durable_but_not_exportable_until_terminal()
    {
        var path = Path.Combine(Path.GetTempPath(), $"iskra-lifecycle-{Guid.NewGuid():N}.db");
        try
        {
            long id;
            using (var store = new SqliteLogStore(path))
                id = store.BeginAttempt(StartedAt(DateTime.UtcNow), reserveBatchLock: false);

            using var reopened = new SqliteLogStore(path);
            var row = Assert.Single(reopened.QueryStartedAttempts());
            Assert.Equal(id, row.Id);
            Assert.Equal(FlashAttemptStates.Started, row.AttemptState);
            Assert.Equal(FlashAttemptStates.Started, row.Result);
            Assert.Empty(reopened.GetUnsynced());
            Assert.Equal(0, reopened.CountUnsynced());
            Assert.Equal((0, 0, 0), reopened.CountsForBatch(""));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Finalization_updates_the_same_row_once_and_enqueues_it_for_sync()
    {
        using var store = new SqliteLogStore(":memory:");
        var id = store.BeginAttempt(StartedAt(DateTime.UtcNow), reserveBatchLock: false);

        store.FinalizeAttempt(id, new FlashAttemptFinalization(
            CompletedAtUtc: DateTime.UtcNow,
            TargetDetected: "PY32Fxxx M0+",
            Result: FlashResult.Pass,
            ErrorCode: null,
            ErrorMessage: null,
            DurationMs: 321,
            GdbTail: "verified"));

        Assert.Empty(store.QueryStartedAttempts());
        var row = Assert.Single(store.QueryRecent());
        Assert.Equal(id, row.Id);
        Assert.Equal(FlashAttemptStates.Terminal, row.AttemptState);
        Assert.Equal("PASS", row.Result);
        Assert.Equal(321, row.DurationMs);
        Assert.Equal(id, Assert.Single(store.GetUnsynced()).Id);

        Assert.Throws<InvalidOperationException>(() => store.FinalizeAttempt(
            id,
            new FlashAttemptFinalization(
                DateTime.UtcNow,
                null,
                FlashResult.Fail,
                "E_OVERWRITE",
                "must not replace authoritative result",
                0,
                null)));
    }

    [Fact]
    public void Recovery_marks_only_proven_abandoned_rows_terminal()
    {
        using var store = new SqliteLogStore(":memory:");
        var now = DateTime.UtcNow;
        var old = store.BeginAttempt(StartedAt(now.AddHours(-2)), reserveBatchLock: false);
        var active = store.BeginAttempt(StartedAt(now), reserveBatchLock: false);

        Assert.Equal(1, store.RecoverAbandonedAttempts(
            now.AddHours(-1),
            now,
            "startup recovery after an unclean shutdown"));

        var started = Assert.Single(store.QueryStartedAttempts());
        Assert.Equal(active, started.Id);
        var recovered = Assert.Single(store.QueryRecent(), row => row.Id == old);
        Assert.Equal(FlashAttemptStates.Terminal, recovered.AttemptState);
        Assert.Equal("FAIL", recovered.Result);
        Assert.Equal(SqliteLogStore.AbandonedAttemptErrorCode, recovered.ErrorCode);
        Assert.Equal(old, Assert.Single(store.GetUnsynced()).Id);
    }
}
