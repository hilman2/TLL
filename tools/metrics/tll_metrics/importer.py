"""Loads the mod's metrics log into SQLite.

The mod writes one folder per game session, each with one JSON Lines file per
table (see README.md). Every record becomes a row of the table of the same
name; every field a column, added when it first appears. A session is loaded
again in full when one of its files has grown, so the database can be
refreshed while the game is still writing.
"""

from __future__ import annotations

import json
import re
import sqlite3
from dataclasses import dataclass, field
from pathlib import Path

TABLES = ("sessions", "rounds", "phase_rounds", "reviews", "decisions")

# Indexes for the queries in report.py.
INDEXES = {
    "rounds": ("session", "node", "round"),
    "phase_rounds": ("session", "node", "phase"),
    "reviews": ("session", "node"),
    "decisions": ("session", "kind", "node"),
}

# Field names come from a file and become column names: anything but a plain
# name is refused rather than quoted.
_NAME = re.compile(r"^[a-z_][a-z0-9_]*$")


@dataclass
class ImportResult:
    """What an import did, for the command line."""

    sessions: list[str] = field(default_factory=list)
    rows: dict[str, int] = field(default_factory=dict)
    skipped_lines: int = 0


def default_root() -> Path:
    """The folder the mod writes to, in the game's user data on Windows."""
    return Path.home() / "AppData" / "LocalLow" / "Colossal Order" / "Cities Skylines II" / "ModsData" / "TLL" / "Metrics"


def import_all(root: Path, database: Path) -> ImportResult:
    """Loads every session folder under ``root`` into ``database``, skipping files unchanged since the last import."""
    result = ImportResult()
    with sqlite3.connect(database) as connection:
        connection.execute(
            'CREATE TABLE IF NOT EXISTS "_imports" ("session" TEXT, "tbl" TEXT, "size" INTEGER, PRIMARY KEY ("session", "tbl"))'
        )
        for folder in sorted(p for p in root.iterdir() if p.is_dir()):
            if _import_session(connection, folder, result):
                result.sessions.append(folder.name)
        for table, columns in INDEXES.items():
            if _exists(connection, table):
                present = _columns(connection, table)
                wanted = [c for c in columns if c in present]
                if wanted:
                    joined = ", ".join(f'"{c}"' for c in wanted)
                    connection.execute(f'CREATE INDEX IF NOT EXISTS "ix_{table}" ON "{table}" ({joined})')
    return result


def _import_session(connection: sqlite3.Connection, folder: Path, result: ImportResult) -> bool:
    """Loads one session if any of its files changed; returns whether it did."""
    session = folder.name
    sizes: dict[str, int] = {}
    for table in TABLES:
        path = folder / f"{table}.jsonl"
        if path.exists():
            sizes[table] = path.stat().st_size
    known = dict(connection.execute('SELECT "tbl", "size" FROM "_imports" WHERE "session" = ?', (session,)).fetchall())
    if sizes == known:
        return False

    for table in sizes:
        rows, complete, skipped = read_records(folder / f"{table}.jsonl")
        result.skipped_lines += skipped
        if rows:
            _ensure_table(connection, table, rows)
        if _exists(connection, table):
            connection.execute(f'DELETE FROM "{table}" WHERE "session" = ?', (session,))
        for row in rows:
            row["session"] = session
            names = list(row)
            placeholders = ", ".join("?" for _ in names)
            joined = ", ".join(f'"{n}"' for n in names)
            connection.execute(f'INSERT INTO "{table}" ({joined}) VALUES ({placeholders})', [row[n] for n in names])
        result.rows[table] = result.rows.get(table, 0) + len(rows)
        # Only the complete lines count as imported: a line the game is
        # still writing makes the file look changed next time.
        connection.execute('INSERT OR REPLACE INTO "_imports" ("session", "tbl", "size") VALUES (?, ?, ?)', (session, table, complete))
    return True


def read_records(path: Path) -> tuple[list[dict[str, object]], int, int]:
    """Reads a JSON Lines file.

    Returns the records, the number of bytes up to the end of the last
    complete line, and the number of lines skipped. A last line without its
    line break is being written and is left for the next import; a broken
    line elsewhere is skipped and counted.
    """
    data = path.read_bytes()
    end = data.rfind(b"\n") + 1
    records: list[dict[str, object]] = []
    skipped = 0
    for line in data[:end].splitlines():
        if not line.strip():
            continue
        try:
            record = json.loads(line)
        except json.JSONDecodeError:
            skipped += 1
            continue
        if isinstance(record, dict):
            records.append(record)
        else:
            skipped += 1
    return records, end, skipped


def _ensure_table(connection: sqlite3.Connection, table: str, rows: list[dict[str, object]]) -> None:
    """Creates the table, or adds the columns it lacks, for the fields of ``rows``."""
    names = ["session"]
    for row in rows:
        for name in row:
            if name not in names:
                names.append(name)
    for name in names:
        if not _NAME.match(name):
            raise ValueError(f"{table}: field name {name!r} is not a plain column name")
    if not _exists(connection, table):
        # No declared types: SQLite keeps each value as it came, a number as
        # a number and text as text.
        columns = ", ".join(f'"{n}"' for n in names)
        connection.execute(f'CREATE TABLE "{table}" ({columns})')
        return
    present = _columns(connection, table)
    for name in names:
        if name not in present:
            connection.execute(f'ALTER TABLE "{table}" ADD COLUMN "{name}"')


def _exists(connection: sqlite3.Connection, table: str) -> bool:
    row = connection.execute("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = ?", (table,)).fetchone()
    return row is not None


def _columns(connection: sqlite3.Connection, table: str) -> set[str]:
    return {row[1] for row in connection.execute(f'PRAGMA table_info("{table}")')}
