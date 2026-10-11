"""Read verification databases without creating files or changing schema."""
import argparse
import contextlib
from pathlib import Path
import re
import sqlite3


def read_logs(directory, kind):
    folder = Path(directory) / ("runtime" if kind == "runtime" else "diagnostics")
    query = (
        "SELECT line FROM runtime_logs ORDER BY id"
        if kind == "runtime"
        else "SELECT session_id, request_id, kind, entry_json, COALESCE(request_json, '') FROM diagnostic_events ORDER BY id"
    )
    for path in sorted(folder.glob("*.db")):
        if not re.fullmatch(r"\d{4}-\d{2}-\d{2}\.db", path.name):
            continue
        with contextlib.closing(sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True, timeout=5)) as database:
            for row in database.execute(query):
                yield "|".join("" if value is None else str(value) for value in row)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("kind", choices=("runtime", "diagnostics"))
    parser.add_argument("directory")
    args = parser.parse_args()
    for line in read_logs(args.directory, args.kind):
        print(line)
