using Microsoft.Data.Sqlite;

namespace FlashlightApp.Core;

/// <summary>
/// Read DTO for the history view — a subset of the columns, with id and
/// already-parsed timestamp for display.
/// </summary>
public sealed record FlashAttemptRow(
    long Id,
    DateTime TsUtc,
    string Operator,
    string BatchId,
    string ProductId,
    string FirmwareVersion,
    string Result,
    string? ErrorCode,
    long DurationMs,
    string? TargetDetected);

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

    public IReadOnlyList<FlashAttemptRow> QueryRecent(int limit = 200)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, ts_utc, operator, batch_id, product_id, firmware_version,
                   result, error_code, duration_ms, target_detected
            FROM flash_attempts
            ORDER BY id DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var rows = new List<FlashAttemptRow>();
        while (reader.Read())
        {
            rows.Add(new FlashAttemptRow(
                Id:              reader.GetInt64(0),
                TsUtc:           DateTime.Parse(reader.GetString(1)).ToUniversalTime(),
                Operator:        reader.GetString(2),
                BatchId:         reader.GetString(3),
                ProductId:       reader.GetString(4),
                FirmwareVersion: reader.GetString(5),
                Result:          reader.GetString(6),
                ErrorCode:       reader.IsDBNull(7) ? null : reader.GetString(7),
                DurationMs:      reader.GetInt64(8),
                TargetDetected:  reader.IsDBNull(9) ? null : reader.GetString(9)));
        }
        return rows;
    }

    /// <summary>
    /// First (product_id, firmware_version) ever logged for this batch — the row
    /// that "locks" the batch. Returns null if no rows exist for the batch yet.
    /// Lock-defining rows include both PASS and FAIL attempts (any successful
    /// scan that produced a state-machine outcome). Rows where the lock check
    /// itself refused the attempt (E_BATCH_LOCKED) should NOT be lock-defining,
    /// so this query excludes them.
    /// </summary>
    public (string ProductId, string FirmwareVersion)? GetBatchLock(string batchId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT product_id, firmware_version FROM flash_attempts
            WHERE batch_id = $batch
              AND (error_code IS NULL OR error_code != 'E_BATCH_LOCKED')
            ORDER BY id ASC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$batch", batchId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return (reader.GetString(0), reader.GetString(1));
    }

    /// <summary>
    /// Streams the full <c>flash_attempts</c> table (or one batch's slice) into a
    /// UTF-8 CSV at <paramref name="outputPath"/>. Header row included.
    /// Escaping per RFC 4180 (quotes, commas, newlines).
    /// </summary>
    public int ExportCsv(string outputPath, string? batchId = null)
    {
        var sql = batchId is null
            ? "SELECT * FROM flash_attempts ORDER BY id ASC"
            : "SELECT * FROM flash_attempts WHERE batch_id = $batch ORDER BY id ASC";

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        if (batchId is not null) cmd.Parameters.AddWithValue("$batch", batchId);

        using var reader = cmd.ExecuteReader();
        using var writer = new StreamWriter(outputPath, append: false, new System.Text.UTF8Encoding(false));

        var cols = new string[reader.FieldCount];
        for (int i = 0; i < reader.FieldCount; i++) cols[i] = reader.GetName(i);
        writer.WriteLine(CsvWriter.JoinRow(cols));

        int rows = 0;
        var values = new string[reader.FieldCount];
        while (reader.Read())
        {
            for (int i = 0; i < reader.FieldCount; i++)
                values[i] = reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString() ?? "";
            writer.WriteLine(CsvWriter.JoinRow(values));
            rows++;
        }
        return rows;
    }

    public (int Total, int Pass, int Fail) CountsForBatch(string batchId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT
              SUM(CASE WHEN result = 'PASS' THEN 1 ELSE 0 END) AS pass,
              SUM(CASE WHEN result = 'FAIL' THEN 1 ELSE 0 END) AS fail
            FROM flash_attempts
            WHERE batch_id = $batch;
            """;
        cmd.Parameters.AddWithValue("$batch", batchId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return (0, 0, 0);
        int pass = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
        int fail = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
        return (pass + fail, pass, fail);
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
