from __future__ import annotations

import json
import sqlite3
from pathlib import Path

import pytest

from tll_metrics import importer


def write(folder: Path, table: str, records: list[dict[str, object]], tail: str = "") -> None:
    folder.mkdir(parents=True, exist_ok=True)
    text = "".join(json.dumps(r) + "\n" for r in records) + tail
    with (folder / f"{table}.jsonl").open("a", encoding="utf-8") as file:
        file.write(text)


def count(database: Path, table: str) -> int:
    with sqlite3.connect(database) as connection:
        row = connection.execute(f'SELECT COUNT(*) FROM "{table}"').fetchone()
        return int(row[0])


def test_every_record_becomes_a_row(tmp_path: Path) -> None:
    session = tmp_path / "root" / "20260928-031804"
    write(session, "sessions", [{"session": "20260928-031804", "frame": 1, "commit": "abc1234"}])
    write(session, "rounds", [{"session": "20260928-031804", "frame": 4096, "node": 7, "vehicles": 12, "wait_s": 30.5, "backlog": True}])
    database = tmp_path / "m.sqlite"

    result = importer.import_all(tmp_path / "root", database)

    assert result.sessions == ["20260928-031804"]
    with sqlite3.connect(database) as connection:
        row = connection.execute("SELECT node, vehicles, wait_s, backlog, session FROM rounds").fetchone()
    assert row == (7, 12, 30.5, 1, "20260928-031804")


def test_importing_again_does_not_double_the_rows(tmp_path: Path) -> None:
    session = tmp_path / "root" / "s1"
    write(session, "rounds", [{"frame": 1, "node": 1}, {"frame": 2, "node": 1}])
    database = tmp_path / "m.sqlite"
    importer.import_all(tmp_path / "root", database)

    unchanged = importer.import_all(tmp_path / "root", database)
    assert unchanged.sessions == []

    # The game writes on; the session is loaded again in full.
    write(session, "rounds", [{"frame": 3, "node": 1}])
    importer.import_all(tmp_path / "root", database)
    assert count(database, "rounds") == 3


def test_a_line_being_written_waits_for_the_next_import(tmp_path: Path) -> None:
    session = tmp_path / "root" / "s1"
    write(session, "rounds", [{"frame": 1, "node": 1}], tail='{"frame": 2, "no')
    database = tmp_path / "m.sqlite"

    result = importer.import_all(tmp_path / "root", database)
    assert count(database, "rounds") == 1
    assert result.skipped_lines == 0

    with (session / "rounds.jsonl").open("a", encoding="utf-8") as file:
        file.write('de": 1}\n')
    importer.import_all(tmp_path / "root", database)
    assert count(database, "rounds") == 2


def test_a_broken_line_in_between_is_skipped_and_counted(tmp_path: Path) -> None:
    session = tmp_path / "root" / "s1"
    write(session, "rounds", [{"frame": 1}])
    with (session / "rounds.jsonl").open("a", encoding="utf-8") as file:
        file.write("not json\n")
    write(session, "rounds", [{"frame": 3}])

    result = importer.import_all(tmp_path / "root", tmp_path / "m.sqlite")

    assert result.skipped_lines == 1
    assert count(tmp_path / "m.sqlite", "rounds") == 2


def test_a_new_field_becomes_a_new_column(tmp_path: Path) -> None:
    database = tmp_path / "m.sqlite"
    write(tmp_path / "root" / "s1", "rounds", [{"frame": 1, "node": 1}])
    importer.import_all(tmp_path / "root", database)
    write(tmp_path / "root" / "s2", "rounds", [{"frame": 1, "node": 2, "flowing": 5}])

    importer.import_all(tmp_path / "root", database)

    with sqlite3.connect(database) as connection:
        rows = connection.execute("SELECT session, flowing FROM rounds ORDER BY session").fetchall()
    assert rows == [("s1", None), ("s2", 5)]


def test_a_field_name_that_is_no_plain_name_is_refused(tmp_path: Path) -> None:
    write(tmp_path / "root" / "s1", "rounds", [{"frame": 1, 'x"); DROP TABLE rounds; --': 1}])
    with pytest.raises(ValueError, match="not a plain column name"):
        importer.import_all(tmp_path / "root", tmp_path / "m.sqlite")
