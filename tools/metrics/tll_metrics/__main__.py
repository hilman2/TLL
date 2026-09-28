"""Command line: import the metrics log, report on it, or query it.

    python -m tll_metrics import [--root DIR] [--db FILE]
    python -m tll_metrics report [--session latest|all|NAME] [--db FILE]
    python -m tll_metrics sql "SELECT ..." [--db FILE]

Without --root, the folder the mod writes to on Windows; without --db,
metrics.sqlite in that folder.
"""

from __future__ import annotations

import argparse
import sqlite3
import sys
from pathlib import Path

from tll_metrics import importer, report


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="tll_metrics", description="The TLL mod's metrics log in SQLite.")
    parser.add_argument("--root", type=Path, default=importer.default_root(), help="folder with one folder per game session")
    parser.add_argument("--db", type=Path, default=None, help="SQLite file; default: metrics.sqlite in --root")
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("import", help="load new or grown session files")
    reporting = commands.add_parser("report", help="the standard questions, after an import")
    reporting.add_argument("--session", default="latest", help='"latest", "all", or a session name (default: latest)')
    querying = commands.add_parser("sql", help="run one query, after an import")
    querying.add_argument("query")
    args = parser.parse_args(argv)

    database: Path = args.db if args.db is not None else args.root / "metrics.sqlite"
    if args.command == "import" or args.command == "report":
        if not args.root.is_dir():
            print(f"no metrics folder at {args.root}; switch on 'Record metrics' in the mod's settings", file=sys.stderr)
            return 1
        result = importer.import_all(args.root, database)
        if args.command == "import":
            print(f"imported {len(result.sessions)} session(s) into {database}: {result.rows}")
            if result.skipped_lines:
                print(f"skipped {result.skipped_lines} unreadable line(s)")
            return 0

    with sqlite3.connect(database) as connection:
        if args.command == "report":
            print(report.render(report.report(connection, args.session)))
            return 0
        cursor = connection.execute(args.query)
        headers = [d[0] for d in cursor.description] if cursor.description else []
        print(report.render([report.Answer("query", headers, cursor.fetchall())]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
