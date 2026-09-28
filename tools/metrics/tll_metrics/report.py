"""Standard questions to the metrics database, as plain-text tables.

Each section answers one question about the control (see README.md). A
section whose table or column is not in the database yet, because the game
has not written such records, reports that instead of failing.
"""

from __future__ import annotations

import sqlite3
from collections.abc import Sequence
from dataclasses import dataclass

# 60 simulation frames make one second of the vehicles' time.
FRAMES_PER_HOUR = 60 * 3600


@dataclass(frozen=True)
class Question:
    """One section of the report: a title and the query answering it.

    ``{sessions}`` in the query stands for the sessions chosen; queries
    without it look at every session.
    """

    title: str
    sql: str


@dataclass
class Answer:
    title: str
    headers: list[str]
    rows: list[tuple[object, ...]]
    missing: str | None = None


QUESTIONS = (
    Question(
        "Sessions: traffic, waiting and flow per session",
        f"""
        SELECT s.session, s."commit",
               COUNT(DISTINCT r.node) AS junctions,
               ROUND((MAX(r.frame) - MIN(r.frame)) * 1.0 / {FRAMES_PER_HOUR}, 1) AS sim_hours,
               SUM(r.vehicles) AS vehicles,
               ROUND(SUM(r.wait_s) / NULLIF(SUM(r.vehicles), 0), 1) AS wait_per_vehicle_s,
               ROUND(100.0 * SUM(r.free_wait_s) / NULLIF(SUM(r.wait_s), 0)) AS exit_free_pct,
               ROUND(100.0 * SUM(r.flowing) / NULLIF(SUM(r.vehicles), 0), 1) AS no_stop_pct,
               ROUND(100.0 * AVG(r.backlog), 1) AS backlog_rounds_pct
        FROM sessions s LEFT JOIN rounds r ON r.session = s.session
        WHERE s.session IN ({{sessions}})
        GROUP BY s.session ORDER BY s.session
        """,
    ),
    Question(
        "Junctions with the most waiting",
        """
        SELECT node, GROUP_CONCAT(DISTINCT layout) AS layouts, GROUP_CONCAT(DISTINCT mode) AS modes,
               COUNT(*) AS rounds, SUM(vehicles) AS vehicles,
               ROUND(SUM(wait_s) / 3600.0, 1) AS wait_hours,
               ROUND(SUM(wait_s) / NULLIF(SUM(vehicles), 0), 1) AS wait_per_vehicle_s,
               ROUND(100.0 * SUM(free_wait_s) / NULLIF(SUM(wait_s), 0)) AS exit_free_pct,
               ROUND(100.0 * AVG(backlog)) AS backlog_pct,
               ROUND(100.0 * AVG(blocked_share_max)) AS exit_blocked_pct
        FROM rounds WHERE session IN ({sessions})
        GROUP BY node ORDER BY SUM(wait_s) DESC LIMIT 15
        """,
    ),
    Question(
        "Greens too short: ended with vehicles standing and the exit free",
        """
        SELECT node, phase, MAX(flags) AS flags, SUM(greens) AS greens, SUM(failures) AS too_short,
               ROUND(100.0 * SUM(failures) / NULLIF(SUM(end_empty + end_maximum + end_starved + end_outweighed
                     + end_blocked + end_emergency + end_schedule), 0)) AS too_short_pct,
               ROUND(SUM(green_s) / NULLIF(SUM(greens), 0), 1) AS avg_green_s,
               ROUND(AVG(max_green_s)) AS max_green_s,
               ROUND(SUM(residual_queue) / NULLIF(SUM(failures), 0), 1) AS left_standing,
               SUM(end_maximum) AS at_max, SUM(end_starved) AS starved, SUM(end_outweighed) AS outweighed,
               SUM(end_empty) AS empty, SUM(end_blocked) AS exit_blocked
        FROM phase_rounds WHERE session IN ({sessions})
        GROUP BY node, phase HAVING SUM(greens) >= 5
        ORDER BY SUM(failures) DESC LIMIT 15
        """,
    ),
    Question(
        "Greens with the least traffic per second: candidates for too long",
        """
        SELECT node, phase, MAX(flags) AS flags, SUM(greens) AS greens,
               ROUND(SUM(green_s) / NULLIF(SUM(greens), 0), 1) AS avg_green_s,
               ROUND(SUM(served) / NULLIF(SUM(green_s), 0), 2) AS vehicles_per_green_s,
               SUM(end_maximum) AS at_max, SUM(end_empty) AS empty
        FROM phase_rounds WHERE session IN ({sessions})
        GROUP BY node, phase HAVING SUM(green_s) >= 300 AND SUM(served) > 0
        ORDER BY SUM(served) / SUM(green_s) ASC LIMIT 15
        """,
    ),
    Question(
        "Layout changes per junction",
        """
        SELECT node, COUNT(*) AS changes, SUM(jammed) AS for_jams, SUM(tried) AS to_tried,
               GROUP_CONCAT("to", ' > ') AS sequence
        FROM decisions WHERE kind = 'layout' AND session IN ({sessions})
        GROUP BY node ORDER BY COUNT(*) DESC LIMIT 15
        """,
    ),
    Question(
        "Model against measurement, per layout",
        """
        SELECT layout, wave, COUNT(*) AS periods,
               ROUND(AVG(measured_wait_s), 1) AS measured_s, ROUND(AVG(modelled_wait_s), 1) AS modelled_s,
               ROUND(AVG(measured_wait_s / modelled_wait_s), 2) AS ratio,
               ROUND(100.0 * AVG(period_backlog)) AS backlog_pct
        FROM reviews WHERE recorded = 1 AND session IN ({sessions})
        GROUP BY layout, wave ORDER BY layout, wave
        """,
    ),
    Question(
        "Green waves: what the rounds did",
        """
        SELECT action, COUNT(*) AS times, ROUND(AVG(junctions), 1) AS junctions,
               ROUND(AVG(cycle_s)) AS cycle_s, ROUND(AVG(band_a_s)) AS band_a_s, ROUND(AVG(band_b_s)) AS band_b_s
        FROM decisions WHERE kind = 'wave' AND session IN ({sessions})
        GROUP BY action ORDER BY COUNT(*) DESC
        """,
    ),
    Question(
        "Green waves: junctions in a wave against alone (traffic differs between the two)",
        """
        SELECT node,
               SUM(mode = 'Coordinated') AS wave_rounds,
               SUM(mode NOT IN ('Coordinated', 'Flashing')) AS alone_rounds,
               ROUND(SUM(CASE WHEN mode = 'Coordinated' THEN wait_s END)
                     / NULLIF(SUM(CASE WHEN mode = 'Coordinated' THEN vehicles END), 0), 1) AS wait_wave_s,
               ROUND(SUM(CASE WHEN mode NOT IN ('Coordinated', 'Flashing') THEN wait_s END)
                     / NULLIF(SUM(CASE WHEN mode NOT IN ('Coordinated', 'Flashing') THEN vehicles END), 0), 1) AS wait_alone_s,
               ROUND(100.0 * SUM(CASE WHEN mode = 'Coordinated' THEN flowing END)
                     / NULLIF(SUM(CASE WHEN mode = 'Coordinated' THEN vehicles END), 0)) AS no_stop_wave_pct,
               ROUND(100.0 * SUM(CASE WHEN mode NOT IN ('Coordinated', 'Flashing') THEN flowing END)
                     / NULLIF(SUM(CASE WHEN mode NOT IN ('Coordinated', 'Flashing') THEN vehicles END), 0)) AS no_stop_alone_pct
        FROM rounds WHERE session IN ({sessions})
        GROUP BY node HAVING SUM(mode = 'Coordinated') > 0
        ORDER BY SUM(mode = 'Coordinated') DESC LIMIT 15
        """,
    ),
    Question(
        "Rebuilds: who asked, and whether the controller carried on",
        """
        SELECT "trigger", carries_on, COUNT(*) AS times, COUNT(DISTINCT node) AS junctions
        FROM decisions WHERE kind = 'rebuild' AND session IN ({sessions})
        GROUP BY "trigger", carries_on ORDER BY COUNT(*) DESC
        """,
    ),
    Question(
        "Flashing yellow: starts and ends, and why",
        """
        SELECT "to" AS flashing, reason, COUNT(*) AS times, COUNT(DISTINCT node) AS junctions,
               ROUND(AVG(worst_free_queue), 1) AS queue_per_lane
        FROM decisions WHERE kind = 'flash' AND session IN ({sessions})
        GROUP BY "to", reason ORDER BY COUNT(*) DESC
        """,
    ),
    Question(
        "What the player did",
        """
        SELECT action, value, COUNT(*) AS times
        FROM decisions WHERE kind = 'user' AND session IN ({sessions})
        GROUP BY action, value ORDER BY COUNT(*) DESC
        """,
    ),
    Question(
        "Builds compared, over all sessions (traffic differs between sessions)",
        """
        SELECT s."commit", COUNT(DISTINCT s.session) AS sessions, SUM(r.vehicles) AS vehicles,
               ROUND(SUM(r.wait_s) / NULLIF(SUM(r.vehicles), 0), 1) AS wait_per_vehicle_s,
               ROUND(100.0 * SUM(r.flowing) / NULLIF(SUM(r.vehicles), 0), 1) AS no_stop_pct,
               ROUND(100.0 * AVG(r.backlog), 1) AS backlog_rounds_pct
        FROM rounds r JOIN sessions s ON s.session = r.session
        GROUP BY s."commit" ORDER BY MIN(s.session)
        """,
    ),
)


def choose_sessions(connection: sqlite3.Connection, which: str) -> list[str]:
    """The sessions ``which`` names: "latest", "all", or a session's name."""
    known = [row[0] for row in connection.execute("SELECT session FROM sessions ORDER BY session")]
    if which == "all":
        return known
    if which == "latest":
        return known[-1:]
    return [which] if which in known else []


def answer(connection: sqlite3.Connection, question: Question, sessions: Sequence[str]) -> Answer:
    """Runs one question for the chosen sessions."""
    placeholders = ", ".join("?" for _ in sessions) or "NULL"
    sql = question.sql.replace("{sessions}", placeholders)
    params = list(sessions) if "{sessions}" in question.sql else []
    try:
        cursor = connection.execute(sql, params)
    except sqlite3.OperationalError as error:
        # A table or column the game has not written yet.
        return Answer(question.title, [], [], missing=str(error))
    headers = [d[0] for d in cursor.description]
    return Answer(question.title, headers, cursor.fetchall())


def report(connection: sqlite3.Connection, which: str) -> list[Answer]:
    """Answers every question for the sessions ``which`` names (see choose_sessions)."""
    sessions = choose_sessions(connection, which)
    return [answer(connection, q, sessions) for q in QUESTIONS]


def render(answers: Sequence[Answer]) -> str:
    """The answers as plain-text tables."""
    parts: list[str] = []
    for a in answers:
        parts.append(f"== {a.title}")
        if a.missing is not None:
            parts.append(f"   no data yet ({a.missing})")
        elif not a.rows:
            parts.append("   none")
        else:
            cells = [[_cell(v) for v in row] for row in a.rows]
            widths = [max(len(h), *(len(r[i]) for r in cells)) for i, h in enumerate(a.headers)]
            parts.append("   " + "  ".join(h.ljust(w) for h, w in zip(a.headers, widths, strict=True)))
            for row in cells:
                parts.append("   " + "  ".join(v.ljust(w) for v, w in zip(row, widths, strict=True)))
        parts.append("")
    return "\n".join(parts)


def _cell(value: object) -> str:
    if value is None:
        return "-"
    if isinstance(value, float):
        return f"{value:g}"
    return str(value)
