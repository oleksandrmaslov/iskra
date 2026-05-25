using Microsoft.Data.Sqlite;

namespace FlashlightApp.Core;

public sealed record FlashAttemptRecord(
    DateTime TsUtc,
    string Operator,
    string StationId,
    string BatchId,
    string ProductId,
    string FirmwareVersion,
    string FirmwareSha256,
    string TargetBmpMatch,
    string? TargetDetected,
    int TargetFlashKb,
    string ComPort,
    string? ProbeSerial,
    PowerMode Power,
    bool ConnectRst,
    int BmpFrequencyHz,
    FlashResult Result,
    string? ErrorCode,
    string? ErrorMessage,
    long DurationMs,
    string? GdbTail);

/// <summary>
/// Append-only SQLite store for flash attempts. One row per attempt.
/// Schema is created on first connection; safe to call repeatedly.
/// Pass <c>:memory:</c> for unit tests.
/// </summary>
public sealed class SqliteLogStore : IDisposable
{
    private readonly SqliteConnection _conn;

    public SqliteLogStore(string dbPath)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        _conn = new SqliteConnection(cs);
        _conn.Open();
        EnsureSchema();
    }

    public void Dispose() => _conn.Dispose();

    private void EnsureSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = SchemaSql;
        cmd.ExecuteNonQuery();
    }

    public long Append(FlashAttemptRecord r)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = InsertSql;
        cmd.Parameters.AddWithValue("$ts_utc",           r.TsUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$operator",         r.Operator);
        cmd.Parameters.AddWithValue("$station_id",       r.StationId);
        cmd.Parameters.AddWithValue("$batch_id",         r.BatchId);
        cmd.Parameters.AddWithValue("$product_id",       r.ProductId);
        cmd.Parameters.AddWithValue("$firmware_version", r.FirmwareVersion);
        cmd.Parameters.AddWithValue("$firmware_sha256",  r.FirmwareSha256);
        cmd.Parameters.AddWithValue("$target_bmp_match", r.TargetBmpMatch);
        cmd.Parameters.AddWithValue("$target_detected",  (object?)r.TargetDetected ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$target_flash_kb",  r.TargetFlashKb);
        cmd.Parameters.AddWithValue("$com_port",         r.ComPort);
        cmd.Parameters.AddWithValue("$probe_serial",     (object?)r.ProbeSerial ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$power_mode",       r.Power.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$connect_rst",      r.ConnectRst ? 1 : 0);
        cmd.Parameters.AddWithValue("$bmp_frequency_hz", r.BmpFrequencyHz);
        cmd.Parameters.AddWithValue("$result",           r.Result == FlashResult.Pass ? "PASS" : "FAIL");
        cmd.Parameters.AddWithValue("$error_code",       (object?)r.ErrorCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$error_message",    (object?)r.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$duration_ms",      r.DurationMs);
        cmd.Parameters.AddWithValue("$gdb_tail",         (object?)r.GdbTail ?? DBNull.Value);
        cmd.ExecuteNonQuery();

        using var idCmd = _conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid();";
        return (long)(idCmd.ExecuteScalar() ?? 0L);
    }

    public int Count()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM flash_attempts;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS flash_attempts (
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
        CREATE INDEX IF NOT EXISTS idx_flash_attempts_batch ON flash_attempts(batch_id);
        CREATE INDEX IF NOT EXISTS idx_flash_attempts_ts    ON flash_attempts(ts_utc);
        """;

    private const string InsertSql = """
        INSERT INTO flash_attempts (
          ts_utc, operator, station_id, batch_id, product_id,
          firmware_version, firmware_sha256,
          target_bmp_match, target_detected, target_flash_kb,
          com_port, probe_serial,
          power_mode, connect_rst, bmp_frequency_hz,
          result, error_code, error_message, duration_ms, gdb_tail
        ) VALUES (
          $ts_utc, $operator, $station_id, $batch_id, $product_id,
          $firmware_version, $firmware_sha256,
          $target_bmp_match, $target_detected, $target_flash_kb,
          $com_port, $probe_serial,
          $power_mode, $connect_rst, $bmp_frequency_hz,
          $result, $error_code, $error_message, $duration_ms, $gdb_tail
        );
        """;
}
