using Iskra.Core;
using Microsoft.Data.Sqlite;

namespace Iskra.Core.Tests;

/// <summary>
/// Sprint 5 cloud-mirror tests: synced_at_utc migration, GetUnsynced,
/// MarkSynced, CountUnsynced.
/// </summary>
public class SqliteLogStoreSyncTests
{
    private static FlashAttemptRecord Sample(
        string station = "BENCH-1",
        string batch = "B-2026-001",
        FlashResult result = FlashResult.Pass,
        string? err = null,
        DateTime? ts = null)
        => new(
            TsUtc:           ts ?? new DateTime(2026, 5, 25, 14, 30, 0, DateTimeKind.Utc),
            Operator:        "Iryna",
            StationId:       station,
            BatchId:         batch,
            ProductId:       "ci-clop",
            FirmwareVersion: "1.0.0",
            FirmwareSha256:  "abcdef",
            TargetBmpMatch:  "PY32Fxxx",
            TargetDetected:  "PY32Fxxx M0+",
            TargetFlashKb:   32,
            ComPort:         "COM30",
            ProbeSerial:     null,
            Power:           PowerMode.External,
            ConnectRst:      false,
            BmpFrequencyHz:  1_000_000,
            Result:          result,
            ErrorCode:       err,
            ErrorMessage:    err is null ? null : "details",
            DurationMs:      820,
            GdbTail:         "Section .text ... matched.");

    [Fact]
    public void Fresh_rows_are_unsynced()
    {
        using var store = new SqliteLogStore(":memory:");
        store.Append(Sample());
        store.Append(Sample(result: FlashResult.Fail, err: "E_TIMEOUT"));
        Assert.Equal(2, store.CountUnsynced());

        var batch = store.GetUnsynced();
        Assert.Equal(2, batch.Count);
        Assert.True(batch[0].Id < batch[1].Id);
        Assert.Equal(FlashResult.Pass, batch[0].Record.Result);
        Assert.Equal(FlashResult.Fail, batch[1].Record.Result);
        Assert.Equal("E_TIMEOUT", batch[1].Record.ErrorCode);
    }

    [Fact]
    public void GetUnsynced_round_trips_all_fields()
    {
        using var store = new SqliteLogStore(":memory:");
        var ts = new DateTime(2026, 5, 25, 14, 30, 7, DateTimeKind.Utc);
        var input = Sample(ts: ts) with { ProbeSerial = "AABB1234", GdbTail = "load OK" };
        var id = store.Append(input);

        var batch = store.GetUnsynced();
        var got = Assert.Single(batch);
        Assert.Equal(id, got.Id);
        Assert.Equal(input.TsUtc, got.Record.TsUtc);
        Assert.Equal(input.Operator, got.Record.Operator);
        Assert.Equal(input.StationId, got.Record.StationId);
        Assert.Equal(input.BatchId, got.Record.BatchId);
        Assert.Equal(input.ProductId, got.Record.ProductId);
        Assert.Equal(input.FirmwareVersion, got.Record.FirmwareVersion);
        Assert.Equal(input.FirmwareSha256, got.Record.FirmwareSha256);
        Assert.Equal(input.TargetBmpMatch, got.Record.TargetBmpMatch);
        Assert.Equal(input.TargetDetected, got.Record.TargetDetected);
        Assert.Equal(input.TargetFlashKb, got.Record.TargetFlashKb);
        Assert.Equal(input.ComPort, got.Record.ComPort);
        Assert.Equal(input.ProbeSerial, got.Record.ProbeSerial);
        Assert.Equal(input.Power, got.Record.Power);
        Assert.Equal(input.ConnectRst, got.Record.ConnectRst);
        Assert.Equal(input.BmpFrequencyHz, got.Record.BmpFrequencyHz);
        Assert.Equal(input.Result, got.Record.Result);
        Assert.Equal(input.DurationMs, got.Record.DurationMs);
        Assert.Equal(input.GdbTail, got.Record.GdbTail);
    }

    [Fact]
    public void MarkSynced_removes_rows_from_unsynced_queue()
    {
        using var store = new SqliteLogStore(":memory:");
        var id1 = store.Append(Sample());
        var id2 = store.Append(Sample(result: FlashResult.Fail, err: "E_TIMEOUT"));
        var id3 = store.Append(Sample());

        var marked = store.MarkSynced(new[] { id1, id3 }, new DateTime(2026, 5, 26, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(2, marked);

        var remaining = store.GetUnsynced();
        var only = Assert.Single(remaining);
        Assert.Equal(id2, only.Id);
        Assert.Equal(1, store.CountUnsynced());
    }

    [Fact]
    public void MarkSynced_is_idempotent_on_already_synced_rows()
    {
        using var store = new SqliteLogStore(":memory:");
        var id = store.Append(Sample());
        var t1 = new DateTime(2026, 5, 26, 0, 0, 0, DateTimeKind.Utc);
        var t2 = t1.AddHours(1);

        Assert.Equal(1, store.MarkSynced(new[] { id }, t1));
        // Second call on an already-synced row updates nothing.
        Assert.Equal(0, store.MarkSynced(new[] { id }, t2));
        Assert.Equal(0, store.CountUnsynced());
    }

    [Fact]
    public void GetUnsynced_respects_batch_size()
    {
        using var store = new SqliteLogStore(":memory:");
        for (int i = 0; i < 10; i++) store.Append(Sample());

        var batch = store.GetUnsynced(batchSize: 3);
        Assert.Equal(3, batch.Count);
    }

    [Fact]
    public void Migration_adds_synced_column_to_pre_sprint5_db()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"flash-log-mig-{Guid.NewGuid():N}.db");
        try
        {
            // Build a "Sprint 4 era" DB that lacks the synced_at_utc column.
            using (var legacy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tmp }.ToString()))
            {
                legacy.Open();
                using var c = legacy.CreateCommand();
                c.CommandText = """
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
                      bmp_frequency_hz, result, duration_ms)
                    VALUES ('2026-05-25T14:30:00Z', 'Iryna', 'BENCH-1', 'B-2026-001',
                            'ci-clop', '1.0.0', 'abcdef', 'PY32Fxxx',
                            32, 'COM30', 'external', 0, 1000000, 'PASS', 800);
                    """;
                c.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            // Opening with the Sprint 5 store should migrate in-place: legacy
            // row becomes an unsynced row, and new rows behave normally.
            using (var store = new SqliteLogStore(tmp))
            {
                Assert.Equal(1, store.CountUnsynced());
                store.Append(Sample());
                Assert.Equal(2, store.CountUnsynced());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void Schema_creates_partial_index_for_unsynced_queue()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"flash-log-index-{Guid.NewGuid():N}.db");
        try
        {
            using (var store = new SqliteLogStore(tmp))
            {
                Assert.Equal(0, store.CountUnsynced());
            }
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tmp }.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT sql FROM sqlite_master
                WHERE type = 'index' AND name = 'idx_flash_attempts_unsynced';
                """;
            var sql = Assert.IsType<string>(cmd.ExecuteScalar());
            Assert.Contains("WHERE synced_at_utc IS NULL", sql, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
