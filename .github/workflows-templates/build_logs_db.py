#!/usr/bin/env python3
"""
Template for: oleksandrmaslov/iskra-logs
Goes into:    .github/scripts/build_logs_db.py

Walks <stations-root>/<station_id>/<YYYY-MM-DD>.jsonl and ingests every line
into <out>.db. Each row carries `schema_version`; only schema_version == 1
is recognized. Unknown versions are skipped with a warning so the workflow
keeps producing useful output instead of failing the whole rebuild on one
forward-incompatible row.

The schema mirrors Iskra's local flash_attempts SQLite table 1:1 plus:
  · `local_id`           — the id the row had on its originating station
  · `source_path`        — the JSONL file the row came from (debug aid)
  · `(station_id, local_id)` PRIMARY KEY for idempotent re-ingest
"""
from __future__ import annotations

import json
import sqlite3
import sys
from pathlib import Path

SCHEMA = """
CREATE TABLE IF NOT EXISTS flash_attempts (
  station_id        TEXT    NOT NULL,
  local_id          INTEGER NOT NULL,
  ts_utc            TEXT    NOT NULL,
  operator          TEXT    NOT NULL,
  batch_id          TEXT    NOT NULL,
  product_id        TEXT    NOT NULL,
  firmware_version  TEXT    NOT NULL,
  firmware_sha256   TEXT    NOT NULL,
  target_bmp_match  TEXT    NOT NULL,
  target_detected   TEXT,
  target_flash_kb   INTEGER NOT NULL,
  com_port          TEXT    NOT NULL,
  probe_serial      TEXT,
  power_mode        TEXT    NOT NULL,
  connect_rst       INTEGER NOT NULL,
  bmp_frequency_hz  INTEGER NOT NULL,
  result            TEXT    NOT NULL,
  error_code        TEXT,
  error_message     TEXT,
  duration_ms       INTEGER NOT NULL,
  gdb_tail          TEXT,
  source_path       TEXT    NOT NULL,
  PRIMARY KEY (station_id, local_id)
);
CREATE INDEX IF NOT EXISTS idx_flash_attempts_batch ON flash_attempts(batch_id);
CREATE INDEX IF NOT EXISTS idx_flash_attempts_ts    ON flash_attempts(ts_utc);
CREATE INDEX IF NOT EXISTS idx_flash_attempts_prod  ON flash_attempts(product_id, firmware_version);
"""

INSERT = """
INSERT OR REPLACE INTO flash_attempts (
  station_id, local_id, ts_utc, operator, batch_id, product_id,
  firmware_version, firmware_sha256, target_bmp_match, target_detected,
  target_flash_kb, com_port, probe_serial, power_mode, connect_rst,
  bmp_frequency_hz, result, error_code, error_message, duration_ms,
  gdb_tail, source_path
) VALUES (?, ?, ?, ?, ?, ?,  ?, ?, ?, ?,  ?, ?, ?, ?, ?,  ?, ?, ?, ?, ?,  ?, ?);
"""


def ingest(conn: sqlite3.Connection, jsonl_path: Path) -> tuple[int, int]:
    ok, skipped = 0, 0
    cur = conn.cursor()
    with jsonl_path.open("r", encoding="utf-8") as f:
        for lineno, raw in enumerate(f, start=1):
            raw = raw.strip()
            if not raw:
                continue
            try:
                row = json.loads(raw)
            except json.JSONDecodeError as e:
                print(f"  warn {jsonl_path}:{lineno}: bad json — {e}", file=sys.stderr)
                skipped += 1
                continue
            if row.get("schema_version") != 1:
                print(f"  warn {jsonl_path}:{lineno}: unknown schema_version {row.get('schema_version')!r}", file=sys.stderr)
                skipped += 1
                continue
            try:
                cur.execute(INSERT, (
                    row["station_id"],
                    row["local_id"],
                    row["ts_utc"],
                    row["operator"],
                    row["batch_id"],
                    row["product_id"],
                    row["firmware_version"],
                    row["firmware_sha256"],
                    row["target_bmp_match"],
                    row.get("target_detected"),
                    row["target_flash_kb"],
                    row["com_port"],
                    row.get("probe_serial"),
                    row["power_mode"],
                    1 if row["connect_rst"] else 0,
                    row["bmp_frequency_hz"],
                    row["result"],
                    row.get("error_code"),
                    row.get("error_message"),
                    row["duration_ms"],
                    row.get("gdb_tail"),
                    str(jsonl_path.as_posix()),
                ))
                ok += 1
            except KeyError as e:
                print(f"  warn {jsonl_path}:{lineno}: missing field {e}", file=sys.stderr)
                skipped += 1
    return ok, skipped


def main() -> int:
    if len(sys.argv) != 3:
        print(f"usage: {sys.argv[0]} <out.db> <stations-root>", file=sys.stderr)
        return 2
    out_path = Path(sys.argv[1])
    root = Path(sys.argv[2])
    if not root.is_dir():
        print(f"{root} is not a directory (no stations yet?)", file=sys.stderr)
        # Don't fail — just produce an empty db so downstream "commit if changed"
        # is the source of truth.
        if out_path.exists():
            out_path.unlink()
        conn = sqlite3.connect(out_path)
        conn.executescript(SCHEMA)
        conn.commit()
        conn.close()
        return 0

    # Rebuild from scratch each run — JSONL files are append-only and idempotent.
    if out_path.exists():
        out_path.unlink()
    conn = sqlite3.connect(out_path)
    conn.executescript(SCHEMA)

    total_ok = total_skipped = files = 0
    for jsonl in sorted(root.rglob("*.jsonl")):
        files += 1
        ok, skipped = ingest(conn, jsonl)
        total_ok += ok
        total_skipped += skipped
    conn.commit()
    conn.close()

    print(f"ingested {total_ok} rows from {files} file(s); {total_skipped} skipped")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
