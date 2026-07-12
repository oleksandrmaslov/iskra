using Iskra.Core;
using Microsoft.Data.Sqlite;

namespace Iskra.Core.Tests;

public class SqliteLogStoreBatchLockTests
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static BatchLockDescriptor Descriptor(
        string hash = HashA,
        string bmpMatch = "PY32Fxxx",
        int flashKb = 32)
        => new("ci-clop", "1.0.0", hash, bmpMatch, flashKb);

    [Fact]
    public void ReserveBatchLock_treats_sha_change_under_same_version_as_conflict()
    {
        using var store = new SqliteLogStore(":memory:");

        var first = store.ReserveBatchLock("B-1", Descriptor(HashA));
        var conflicting = store.ReserveBatchLock("B-1", Descriptor(HashB));

        Assert.Equal(BatchLockReservationStatus.Created, first.Status);
        Assert.True(first.IsAccepted);
        Assert.Equal(BatchLockReservationStatus.Conflict, conflicting.Status);
        Assert.False(conflicting.IsAccepted);
        Assert.Equal(HashA, conflicting.Lock.FirmwareSha256);
        Assert.Equal(Descriptor(HashA), store.GetBatchLock("B-1"));
    }

    [Theory]
    [InlineData("STM32F4", 32)]
    [InlineData("PY32Fxxx", 64)]
    public void ReserveBatchLock_treats_target_metadata_change_as_conflict(
        string bmpMatch,
        int flashKb)
    {
        using var store = new SqliteLogStore(":memory:");
        store.ReserveBatchLock("B-1", Descriptor());

        var conflicting = store.ReserveBatchLock(
            "B-1",
            Descriptor(bmpMatch: bmpMatch, flashKb: flashKb));

        Assert.Equal(BatchLockReservationStatus.Conflict, conflicting.Status);
        Assert.Equal(Descriptor(), conflicting.Lock);
    }

    [Fact]
    public void ReserveBatchLock_is_idempotent_for_same_semantic_identity()
    {
        using var store = new SqliteLogStore(":memory:");
        var requested = Descriptor();

        var first = store.ReserveBatchLock("B-1", requested);
        var repeated = store.ReserveBatchLock(
            "B-1",
            requested with
            {
                ProductId = "CI-CLOP",
                FirmwareSha256 = HashA.ToUpperInvariant(),
                TargetBmpMatch = "py32fXXX",
            });

        Assert.Equal(BatchLockReservationStatus.Created, first.Status);
        Assert.Equal(BatchLockReservationStatus.AlreadyReserved, repeated.Status);
        Assert.True(repeated.IsAccepted);
        Assert.Equal(requested, repeated.Lock);
    }

    [Fact]
    public async Task Concurrent_conflicting_reservations_have_exactly_one_winner()
    {
        var path = Path.Combine(Path.GetTempPath(), $"batch-lock-race-{Guid.NewGuid():N}.db");
        try
        {
            using (var initialize = new SqliteLogStore(path))
                Assert.Null(initialize.GetBatchLock("RACE"));

            using var firstStore = new SqliteLogStore(path);
            using var secondStore = new SqliteLogStore(path);
            using var start = new Barrier(3);

            var firstTask = Task.Run(() =>
            {
                start.SignalAndWait();
                return firstStore.ReserveBatchLock("RACE", Descriptor(HashA));
            });
            var secondTask = Task.Run(() =>
            {
                start.SignalAndWait();
                return secondStore.ReserveBatchLock("RACE", Descriptor(HashB));
            });

            start.SignalAndWait();
            var results = await Task.WhenAll(firstTask, secondTask);

            var winner = Assert.Single(
                results,
                result => result.Status == BatchLockReservationStatus.Created);
            var loser = Assert.Single(
                results,
                result => result.Status == BatchLockReservationStatus.Conflict);
            Assert.True(winner.IsAccepted);
            Assert.False(loser.IsAccepted);
            Assert.Equal(winner.Lock, loser.Lock);
            Assert.Equal(winner.Lock, firstStore.GetBatchLock("RACE"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Opening_legacy_database_backfills_earliest_eligible_attempt_durably()
    {
        var path = Path.Combine(Path.GetTempPath(), $"batch-lock-migration-{Guid.NewGuid():N}.db");
        try
        {
            CreateLegacyDatabase(path);

            using (var migrated = new SqliteLogStore(path))
            {
                var locked = Assert.IsType<BatchLockDescriptor>(migrated.GetBatchLock("LEGACY"));
                Assert.Equal("ci-clop", locked.ProductId);
                Assert.Equal("1.0.0", locked.FirmwareVersion);
                Assert.Equal(HashA, locked.FirmwareSha256);
                Assert.Equal("PY32Fxxx", locked.TargetBmpMatch);
                Assert.Equal(32, locked.TargetFlashKb);
            }

            // The lock is a migrated durable record, not a live query over the
            // history table. Removing history cannot change the winner.
            using (var raw = Open(path))
            {
                using var count = raw.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM batch_locks WHERE batch_id = 'LEGACY';";
                Assert.Equal(1L, (long)(count.ExecuteScalar() ?? 0L));

                using var delete = raw.CreateCommand();
                delete.CommandText = "DELETE FROM flash_attempts;";
                delete.ExecuteNonQuery();
            }

            using var reopened = new SqliteLogStore(path);
            Assert.Equal(Descriptor(), reopened.GetBatchLock("LEGACY"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Append_logs_unbatched_attempts_without_creating_phantom_lock()
    {
        var path = Path.Combine(Path.GetTempPath(), $"unbatched-{Guid.NewGuid():N}.db");
        try
        {
            using (var store = new SqliteLogStore(path))
            {
                store.Append(Attempt("ci-clop", HashA), reserveBatchLock: false);
                store.Append(Attempt("venovisor", HashB), reserveBatchLock: false);
                Assert.Equal(2, store.Count());
            }

            using var raw = Open(path);
            using var locks = raw.CreateCommand();
            locks.CommandText = "SELECT COUNT(*) FROM batch_locks;";
            Assert.Equal(0L, (long)(locks.ExecuteScalar() ?? -1L));

            using var unbatched = raw.CreateCommand();
            unbatched.CommandText = "SELECT COUNT(*) FROM flash_attempts WHERE batch_id = '';";
            Assert.Equal(2L, (long)(unbatched.ExecuteScalar() ?? -1L));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static FlashAttemptRecord Attempt(string productId, string hash) => new(
        TsUtc: DateTime.UtcNow,
        Operator: "Iryna",
        StationId: "BENCH-1",
        BatchId: "",
        ProductId: productId,
        FirmwareVersion: "1.0.0",
        FirmwareSha256: hash,
        TargetBmpMatch: "PY32Fxxx",
        TargetDetected: "PY32Fxxx M0+",
        TargetFlashKb: 32,
        ComPort: "COM30",
        ProbeSerial: "BMP-1",
        Power: PowerMode.External,
        ConnectRst: false,
        BmpFrequencyHz: 1_000_000,
        Result: FlashResult.Pass,
        ErrorCode: null,
        ErrorMessage: null,
        DurationMs: 800,
        GdbTail: null);

    private static void CreateLegacyDatabase(string path)
    {
        using var legacy = Open(path);
        using var command = legacy.CreateCommand();
        command.CommandText = $$"""
            CREATE TABLE flash_attempts (
              id               INTEGER PRIMARY KEY AUTOINCREMENT,
              ts_utc           TEXT    NOT NULL,
              operator         TEXT    NOT NULL,
              station_id       TEXT    NOT NULL,
              batch_id         TEXT    NOT NULL,
              product_id       TEXT    NOT NULL,
              firmware_version TEXT    NOT NULL,
              firmware_sha256  TEXT    NOT NULL,
              target_bmp_match TEXT    NOT NULL,
              target_detected  TEXT,
              target_flash_kb  INTEGER NOT NULL,
              com_port         TEXT    NOT NULL,
              probe_serial     TEXT,
              power_mode       TEXT    NOT NULL,
              connect_rst      INTEGER NOT NULL,
              bmp_frequency_hz INTEGER NOT NULL,
              result           TEXT    NOT NULL,
              error_code       TEXT,
              error_message    TEXT,
              duration_ms      INTEGER NOT NULL,
              gdb_tail         TEXT
            );

            INSERT INTO flash_attempts (
              ts_utc, operator, station_id, batch_id, product_id,
              firmware_version, firmware_sha256, target_bmp_match,
              target_flash_kb, com_port, power_mode, connect_rst,
              bmp_frequency_hz, result, error_code, duration_ms)
            VALUES
              ('2026-05-25T14:00:00Z', 'Iryna', 'BENCH-1', 'LEGACY',
               'wrong-product', '9.9.9', '{{HashB}}', 'STM32F4',
               2048, 'COM30', 'external', 0, 1000000, 'FAIL', 'E_BATCH_LOCKED', 1),
              ('2026-05-25T14:01:00Z', 'Iryna', 'BENCH-1', 'LEGACY',
               'ci-clop', '1.0.0', '{{HashA}}', 'PY32Fxxx',
               32, 'COM30', 'external', 0, 1000000, 'PASS', NULL, 800),
              ('2026-05-25T14:02:00Z', 'Iryna', 'BENCH-1', 'LEGACY',
               'ci-clop', '1.0.0', '{{HashB}}', 'PY32Fxxx',
               32, 'COM30', 'external', 0, 1000000, 'PASS', NULL, 800);
            """;
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        return connection;
    }
}
