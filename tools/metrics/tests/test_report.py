from __future__ import annotations

import json
import sqlite3
from pathlib import Path

from tll_metrics import importer, report


def session(root: Path, name: str, tables: dict[str, list[dict[str, object]]]) -> None:
    folder = root / name
    folder.mkdir(parents=True)
    for table, records in tables.items():
        (folder / f"{table}.jsonl").write_text("".join(json.dumps(r) + "\n" for r in records), encoding="utf-8")


def ends(**counts: int) -> dict[str, int]:
    names = ("empty", "maximum", "starved", "outweighed", "blocked", "emergency", "schedule")
    return {f"end_{n}": counts.get(n, 0) for n in names}


def phase(node: int, phase: int, greens: int, failures: int, residual: float, green_s: float, served: int, **end: int) -> dict[str, object]:
    return {
        "node": node,
        "round": 1,
        "phase": phase,
        "flags": "None",
        "greens": greens,
        "failures": failures,
        "residual_queue": residual,
        "green_s": green_s,
        "served": served,
        "max_green_s": 45.0,
        **ends(**end),
    }


def answer_for(answers: list[report.Answer], title_start: str) -> report.Answer:
    return next(a for a in answers if a.title.startswith(title_start))


def test_greens_too_short_ranks_by_failures_and_gives_their_share(tmp_path: Path) -> None:
    tables: dict[str, list[dict[str, object]]] = {
        "sessions": [{"frame": 0, "commit": "abc"}],
        "phase_rounds": [
            # Node 1 phase 0: 8 of 10 ended greens left 4 vehicles standing each.
            phase(1, 0, 10, 8, 32.0, 300.0, 150, maximum=8, empty=2),
            # Node 2 phase 1: never too short.
            phase(2, 1, 10, 0, 0.0, 200.0, 100, empty=10),
        ],
    }
    session(tmp_path / "root", "s1", tables)
    database = tmp_path / "m.sqlite"
    importer.import_all(tmp_path / "root", database)

    with sqlite3.connect(database) as connection:
        short = answer_for(report.report(connection, "latest"), "Greens too short")

    first = dict(zip(short.headers, short.rows[0], strict=True))
    assert (first["node"], first["phase"]) == (1, 0)
    assert first["too_short"] == 8
    assert first["too_short_pct"] == 80.0
    assert first["left_standing"] == 4.0
    assert first["avg_green_s"] == 30.0


def test_waiting_per_vehicle_is_the_sum_over_the_sum(tmp_path: Path) -> None:
    # 10 s for 1 vehicle and 100 s for 99: the mean over vehicles is 1.1 s,
    # not the mean of the rounds' means (5.5 s).
    tables: dict[str, list[dict[str, object]]] = {
        "sessions": [{"frame": 0, "commit": "abc"}],
        "rounds": [
            {"frame": 0, "node": 1, "vehicles": 1, "wait_s": 10.0, "free_wait_s": 10.0, "flowing": 0, "backlog": False},
            {"frame": 216000, "node": 1, "vehicles": 99, "wait_s": 100.0, "free_wait_s": 100.0, "flowing": 50, "backlog": False},
        ],
    }
    session(tmp_path / "root", "s1", tables)
    database = tmp_path / "m.sqlite"
    importer.import_all(tmp_path / "root", database)

    with sqlite3.connect(database) as connection:
        overview = answer_for(report.report(connection, "latest"), "Sessions")

    row = dict(zip(overview.headers, overview.rows[0], strict=True))
    assert row["wait_per_vehicle_s"] == 1.1
    assert row["sim_hours"] == 1.0
    assert row["no_stop_pct"] == 50.0


def test_a_question_without_its_table_says_so(tmp_path: Path) -> None:
    session(tmp_path / "root", "s1", {"sessions": [{"frame": 0, "commit": "abc"}]})
    database = tmp_path / "m.sqlite"
    importer.import_all(tmp_path / "root", database)

    with sqlite3.connect(database) as connection:
        answers = report.report(connection, "latest")

    waves = answer_for(answers, "Green waves: what")
    assert waves.missing is not None
    assert "no data yet" in report.render(answers)


def test_latest_is_the_last_session_by_name(tmp_path: Path) -> None:
    for name in ("20260928-031804", "20260928-041000"):
        session(tmp_path / "root", name, {"sessions": [{"frame": 0, "commit": name}]})
    database = tmp_path / "m.sqlite"
    importer.import_all(tmp_path / "root", database)

    with sqlite3.connect(database) as connection:
        assert report.choose_sessions(connection, "latest") == ["20260928-041000"]
        assert len(report.choose_sessions(connection, "all")) == 2
        assert report.choose_sessions(connection, "unknown") == []
