using Microsoft.Data.Sqlite;

namespace Iskra.Core;

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

/// <summary>
/// One unsynced row, ready to be shipped to the cloud mirror. The
/// <see cref="FlashAttemptRecord"/> payload is identical to what
/// <see cref="SqliteLogStore.Append"/> received; only <see cref="Id"/> is
/// added so the shipper can call back into <see cref="SqliteLogStore.MarkSynced"/>
/// once the row reaches GitHub.
/// </summary>
public sealed record UnsyncedFlashAttempt(long Id, FlashAttemptRecord Record);

/// <summary>
/// The complete firmware/target identity permanently assigned to a production
/// batch. Product and version alone are not sufficient: a catalog can publish
/// rebuilt bytes or corrected target metadata under the same version string.
/// </summary>
public sealed record BatchLockDescriptor(
    string ProductId,
    string FirmwareVersion,
    string FirmwareSha256,
    string TargetBmpMatch,
    int TargetFlashKb)
{
    /// <summary>
    /// Compares semantic catalog identity. Catalog identifiers, versions, BMP
    /// match strings, and hexadecimal hashes are case-insensitive; flash size
    /// must match exactly.
    /// </summary>
    public bool Matches(BatchLockDescriptor other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(ProductId, other.ProductId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(FirmwareVersion, other.FirmwareVersion, StringComparison.OrdinalIgnoreCase)
            && string.Equals(FirmwareSha256, other.FirmwareSha256, StringComparison.OrdinalIgnoreCase)
            && string.Equals(TargetBmpMatch, other.TargetBmpMatch, StringComparison.OrdinalIgnoreCase)
            && TargetFlashKb == other.TargetFlashKb;
    }
}

public enum BatchLockReservationStatus
{
    Created,
    AlreadyReserved,
    Conflict,
}

/// <summary>
/// Result of an atomic batch reservation. <see cref="IsAccepted"/> is true for
/// both the process that created the lock and later callers requesting the
/// exact same descriptor. On conflict, <see cref="Lock"/> is the durable winner.
/// </summary>
public sealed record BatchLockReservation(
    BatchLockReservationStatus Status,
    BatchLockDescriptor Lock)
{
    public bool IsAccepted => Status != BatchLockReservationStatus.Conflict;
}

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
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            DefaultTimeout = 30,
        }.ToString();
        _conn = new SqliteConnection(cs);
        _conn.Open();
        EnsureSchema();
    }

    public void Dispose() => _conn.Dispose();

    private void EnsureSchema()
    {
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = SchemaSql;
            cmd.ExecuteNonQuery();
        }
        // Sprint 5 migration: add synced_at_utc to pre-existing DBs that were
        // created before the cloud-mirror column existed. ALTER TABLE ADD COLUMN
        // can't be guarded by IF NOT EXISTS, so check PRAGMA first.
        if (!ColumnExists("flash_attempts", "synced_at_utc"))
        {
            using var alter = _conn.CreateCommand();
            alter.CommandText = "ALTER TABLE flash_attempts ADD COLUMN synced_at_utc TEXT;";
            alter.ExecuteNonQuery();
        }
        using (var idx = _conn.CreateCommand())
        {
            idx.CommandText =
                "CREATE INDEX IF NOT EXISTS idx_flash_attempts_unsynced " +
                "ON flash_attempts(id) WHERE synced_at_utc IS NULL;";
            idx.ExecuteNonQuery();
        }

        // Local batch-lock hardening migration. The earliest historical row
        // that was not itself refused by a safety gate becomes the durable
        // winner. INSERT OR IGNORE preserves any reservation already made by a
        // newer process and makes repeated/re-entrant migrations harmless.
        using (var backfill = _conn.CreateCommand())
        {
            backfill.CommandText = """
                INSERT OR IGNORE INTO batch_locks (
                  batch_id, product_id, firmware_version, firmware_sha256,
                  target_bmp_match, target_flash_kb, reserved_at_utc
                )
                SELECT fa.batch_id, fa.product_id, fa.firmware_version,
                       fa.firmware_sha256, fa.target_bmp_match,
                       fa.target_flash_kb, fa.ts_utc
                FROM flash_attempts AS fa
                WHERE trim(fa.batch_id) != ''
                  AND (fa.error_code IS NULL OR fa.error_code NOT IN (
                    'E_BATCH_LOCKED', 'E_RELEASE_REVOKED'
                  ))
                  AND NOT EXISTS (
                    SELECT 1
                    FROM flash_attempts AS earlier
                    WHERE earlier.batch_id = fa.batch_id
                      AND (earlier.error_code IS NULL OR earlier.error_code NOT IN (
                        'E_BATCH_LOCKED', 'E_RELEASE_REVOKED'
                      ))
                      AND earlier.id < fa.id
                  );
                """;
            backfill.ExecuteNonQuery();
        }
    }

    private bool ColumnExists(string table, string column)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public long Append(FlashAttemptRecord r, bool reserveBatchLock = true)
    {
        using var tx = _conn.BeginTransaction();

        // Preserve the original "first real attempt locks the batch" behavior
        // for callers that have not yet adopted ReserveBatchLock. New flashing
        // paths must reserve before touching hardware; this compatibility path
        // is deliberately in the same transaction as the attempt row.
        if (reserveBatchLock
            && !string.IsNullOrWhiteSpace(r.BatchId)
            && !string.Equals(r.ErrorCode, "E_BATCH_LOCKED", StringComparison.Ordinal)
            && !string.Equals(r.ErrorCode, "E_RELEASE_REVOKED", StringComparison.Ordinal))
        {
            InsertBatchLock(
                tx,
                r.BatchId,
                new BatchLockDescriptor(
                    r.ProductId,
                    r.FirmwareVersion,
                    r.FirmwareSha256,
                    r.TargetBmpMatch,
                    r.TargetFlashKb),
                r.TsUtc);
        }

        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
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
        idCmd.Transaction = tx;
        idCmd.CommandText = "SELECT last_insert_rowid();";
        var id = (long)(idCmd.ExecuteScalar() ?? 0L);
        tx.Commit();
        return id;
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
    /// Returns the durable firmware/target identity reserved for this batch, or
    /// null if no reservation or qualifying legacy attempt exists.
    /// </summary>
    public BatchLockDescriptor? GetBatchLock(string batchId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        return GetBatchLock(batchId, transaction: null);
    }

    /// <summary>
    /// Atomically creates a durable local reservation or returns the reservation
    /// that won a concurrent race. A repeated request for the same full identity
    /// is accepted; a difference in product, version, firmware hash, BMP match,
    /// or flash size is a conflict.
    /// </summary>
    public BatchLockReservation ReserveBatchLock(
        string batchId,
        BatchLockDescriptor requested)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentNullException.ThrowIfNull(requested);

        using var tx = _conn.BeginTransaction();
        var created = InsertBatchLock(tx, batchId, requested, DateTime.UtcNow) == 1;
        var locked = GetBatchLock(batchId, tx)
            ?? throw new InvalidOperationException("Batch reservation was not persisted.");
        tx.Commit();

        var status = created
            ? BatchLockReservationStatus.Created
            : locked.Matches(requested)
                ? BatchLockReservationStatus.AlreadyReserved
                : BatchLockReservationStatus.Conflict;
        return new BatchLockReservation(status, locked);
    }

    private BatchLockDescriptor? GetBatchLock(
        string batchId,
        SqliteTransaction? transaction)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            SELECT product_id, firmware_version, firmware_sha256,
                   target_bmp_match, target_flash_kb
            FROM batch_locks
            WHERE batch_id = $batch;
            """;
        cmd.Parameters.AddWithValue("$batch", batchId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new BatchLockDescriptor(
            ProductId:       reader.GetString(0),
            FirmwareVersion: reader.GetString(1),
            FirmwareSha256:  reader.GetString(2),
            TargetBmpMatch:  reader.GetString(3),
            TargetFlashKb:   reader.GetInt32(4));
    }

    private int InsertBatchLock(
        SqliteTransaction transaction,
        string batchId,
        BatchLockDescriptor descriptor,
        DateTime reservedAtUtc)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT OR IGNORE INTO batch_locks (
              batch_id, product_id, firmware_version, firmware_sha256,
              target_bmp_match, target_flash_kb, reserved_at_utc
            ) VALUES (
              $batch_id, $product_id, $firmware_version, $firmware_sha256,
              $target_bmp_match, $target_flash_kb, $reserved_at_utc
            );
            """;
        cmd.Parameters.AddWithValue("$batch_id", batchId);
        cmd.Parameters.AddWithValue("$product_id", descriptor.ProductId);
        cmd.Parameters.AddWithValue("$firmware_version", descriptor.FirmwareVersion);
        cmd.Parameters.AddWithValue("$firmware_sha256", descriptor.FirmwareSha256);
        cmd.Parameters.AddWithValue("$target_bmp_match", descriptor.TargetBmpMatch);
        cmd.Parameters.AddWithValue("$target_flash_kb", descriptor.TargetFlashKb);
        cmd.Parameters.AddWithValue("$reserved_at_utc", reservedAtUtc.ToUniversalTime().ToString("o"));
        return cmd.ExecuteNonQuery();
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

    /// <summary>
    /// Returns up to <paramref name="batchSize"/> rows whose <c>synced_at_utc</c>
    /// is still NULL, ordered by id ascending. Sprint 5: this is the queue
    /// <c>LogShipper</c> drains into <c>iskra-logs</c> JSONL files.
    /// </summary>
    public IReadOnlyList<UnsyncedFlashAttempt> GetUnsynced(int batchSize = 500)
    {
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, ts_utc, operator, station_id, batch_id, product_id,
                   firmware_version, firmware_sha256,
                   target_bmp_match, target_detected, target_flash_kb,
                   com_port, probe_serial,
                   power_mode, connect_rst, bmp_frequency_hz,
                   result, error_code, error_message, duration_ms, gdb_tail
            FROM flash_attempts
            WHERE synced_at_utc IS NULL
            ORDER BY id ASC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", batchSize);
        using var reader = cmd.ExecuteReader();
        var rows = new List<UnsyncedFlashAttempt>();
        while (reader.Read())
        {
            var record = new FlashAttemptRecord(
                TsUtc:            DateTime.Parse(reader.GetString(1)).ToUniversalTime(),
                Operator:         reader.GetString(2),
                StationId:        reader.GetString(3),
                BatchId:          reader.GetString(4),
                ProductId:        reader.GetString(5),
                FirmwareVersion:  reader.GetString(6),
                FirmwareSha256:   reader.GetString(7),
                TargetBmpMatch:   reader.GetString(8),
                TargetDetected:   reader.IsDBNull(9)  ? null : reader.GetString(9),
                TargetFlashKb:    reader.GetInt32(10),
                ComPort:          reader.GetString(11),
                ProbeSerial:      reader.IsDBNull(12) ? null : reader.GetString(12),
                Power:            ParsePower(reader.GetString(13)),
                ConnectRst:       reader.GetInt32(14) != 0,
                BmpFrequencyHz:   reader.GetInt32(15),
                Result:           reader.GetString(16) == "PASS" ? FlashResult.Pass : FlashResult.Fail,
                ErrorCode:        reader.IsDBNull(17) ? null : reader.GetString(17),
                ErrorMessage:     reader.IsDBNull(18) ? null : reader.GetString(18),
                DurationMs:       reader.GetInt64(19),
                GdbTail:          reader.IsDBNull(20) ? null : reader.GetString(20));
            rows.Add(new UnsyncedFlashAttempt(reader.GetInt64(0), record));
        }
        return rows;
    }

    /// <summary>
    /// Stamps the given row ids with <paramref name="syncedAtUtc"/>. Idempotent —
    /// re-marking an already-synced row leaves the existing timestamp untouched
    /// (we filter on <c>synced_at_utc IS NULL</c>), so a shipper that crashes
    /// after the cloud commit but before this call will not double-stamp.
    /// </summary>
    public int MarkSynced(IEnumerable<long> ids, DateTime syncedAtUtc)
    {
        if (ids is null) throw new ArgumentNullException(nameof(ids));
        var list = ids.ToList();
        if (list.Count == 0) return 0;

        using var tx = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE flash_attempts
            SET synced_at_utc = $ts
            WHERE id = $id AND synced_at_utc IS NULL;
            """;
        var tsParam = cmd.Parameters.Add("$ts", SqliteType.Text);
        var idParam = cmd.Parameters.Add("$id", SqliteType.Integer);
        tsParam.Value = syncedAtUtc.ToUniversalTime().ToString("o");

        int updated = 0;
        foreach (var id in list)
        {
            idParam.Value = id;
            updated += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return updated;
    }

    /// <summary>Count of rows still pending upload.</summary>
    public int CountUnsynced()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM flash_attempts WHERE synced_at_utc IS NULL;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static PowerMode ParsePower(string s) => s.ToLowerInvariant() switch
    {
        "external" => PowerMode.External,
        "probe"    => PowerMode.Probe,
        _          => PowerMode.External,
    };

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
          gdb_tail         TEXT,
          synced_at_utc    TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_flash_attempts_batch ON flash_attempts(batch_id);
        CREATE INDEX IF NOT EXISTS idx_flash_attempts_ts    ON flash_attempts(ts_utc);

        CREATE TABLE IF NOT EXISTS batch_locks (
          batch_id          TEXT    PRIMARY KEY,
          product_id        TEXT    NOT NULL,
          firmware_version  TEXT    NOT NULL,
          firmware_sha256   TEXT    NOT NULL,
          target_bmp_match  TEXT    NOT NULL,
          target_flash_kb   INTEGER NOT NULL,
          reserved_at_utc   TEXT    NOT NULL
        );
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
