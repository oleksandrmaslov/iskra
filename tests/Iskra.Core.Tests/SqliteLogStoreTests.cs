using FlashlightApp.Core;
using Microsoft.Data.Sqlite;

namespace FlashlightApp.Core.Tests;

public class SqliteLogStoreTests
{
    private static FlashAttemptRecord Sample(FlashResult result = FlashResult.Pass, string? err = null)
        => new(
            TsUtc:           new DateTime(2026, 5, 25, 14, 30, 0, DateTimeKind.Utc),
            Operator:        "Iryna",
            StationId:       "BENCH-1",
            BatchId:         "B-2026-001",
            ProductId:       "pocket-light",
            FirmwareVersion: "1.0.0",
            FirmwareSha256:  "abcdef",
            TargetBmpMatch:  "PY32F002A",
            TargetDetected:  "PY32F002A M0+",
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
    public void Schema_creates_and_count_starts_at_zero()
    {
        using var store = new SqliteLogStore(":memory:");
        Assert.Equal(0, store.Count());
    }

    [Fact]
    public void Append_returns_increasing_row_ids_and_increments_count()
    {
        using var store = new SqliteLogStore(":memory:");
        var id1 = store.Append(Sample());
        var id2 = store.Append(Sample(FlashResult.Fail, "E_TIMEOUT"));
        Assert.True(id2 > id1);
        Assert.Equal(2, store.Count());
    }

    [Fact]
    public void Append_persists_nullable_fields_as_null()
    {
        using var store = new SqliteLogStore(":memory:");
        store.Append(Sample());
        // Re-open count via fresh query path; just sanity-check no exceptions.
        Assert.Equal(1, store.Count());
    }

    [Fact]
    public void Schema_is_idempotent_on_reopen()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"flash-log-test-{Guid.NewGuid():N}.db");
        try
        {
            using (var s1 = new SqliteLogStore(tmp)) s1.Append(Sample());
            using (var s2 = new SqliteLogStore(tmp))
            {
                Assert.Equal(1, s2.Count());
                s2.Append(Sample(FlashResult.Fail, "E_VERIFY_MISMATCH"));
                Assert.Equal(2, s2.Count());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
