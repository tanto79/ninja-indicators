"""Convert NT8 tick text file(s) to date=YYYY-MM-DD/ticks.parquet folders.

CONVERT ONLY - does not run the study, does not touch results_v2.

Input: the same tick text format the original export used, one trade per line:

    YYYYMMDD HHMMSS fff;last;bid;ask;volume

(date, seconds, and milliseconds are three space-separated fields; then
last;bid;ask;volume separated by semicolons)

Example:
    20260921 083000 123;6105.25;6105.00;6105.50;4

Usage (from the repo folder):
    python convert_only.py                     <- converts every file in data\\ticks\\
    python convert_only.py path\\to\\file.txt   <- converts one file anywhere
"""

import sys
from pathlib import Path

import pandas as pd

from level_role.ticks import _parse_line

ROOT = Path(__file__).resolve().parent


def convert_file(path: Path, out_root: Path) -> int:
    """Stream one text file into per-day parquet partitions. Returns folders written."""
    rows: list[dict] = []
    current_day: str | None = None
    folders = 0
    parsed = 0

    def flush(day: str, buf: list[dict]) -> None:
        nonlocal folders
        if not buf:
            return
        df = pd.DataFrame(buf)
        df["ord"] = range(len(df))
        part = out_root / f"date={day}"
        part.mkdir(parents=True, exist_ok=True)
        out = part / "ticks.parquet"
        if out.exists():
            # Partition already exists (e.g. a Globex evening split across UTC days):
            # merge with existing rows so the day stays complete, then re-sort.
            old = pd.read_parquet(out)
            df = pd.concat([old, df], ignore_index=True)
        df = df.sort_values(["ts_utc", "ord"]).reset_index(drop=True)
        df["ord"] = range(len(df))
        df.to_parquet(out, index=False)
        folders += 1

    with path.open("r", encoding="utf-8", errors="replace") as f:
        for line in f:
            rec = _parse_line(line)
            if rec is None:
                continue
            parsed += 1
            day = rec["ts_utc"].strftime("%Y-%m-%d")
            if current_day is None:
                current_day = day
            if day != current_day:
                flush(current_day, rows)
                rows = []
                current_day = day
            rows.append(rec)

    if current_day and rows:
        flush(current_day, rows)

    if parsed == 0:
        print(f"  !! No lines parsed from {path.name}. Expected lines like:")
        print("     20260921 083000 123;6105.25;6105.00;6105.50;4")
        print("     (date seconds millis;last;bid;ask;volume)")
        print("     If your NT8 file looks different, send its first 2-3 lines.")
    else:
        print(f"  {path.name}: {parsed:,} ticks -> {folders} day folder(s)")
    return folders


def main() -> None:
    if len(sys.argv) > 1:
        sources = [Path(a) for a in sys.argv[1:]]
    else:
        drop = ROOT / "data" / "ticks"
        sources = sorted(p for p in drop.rglob("*")
                         if p.is_file() and p.name != "PUT_TICK_FILES_HERE.txt")
        if not sources:
            print(f"No tick text files found in {drop}")
            print("Drop your NT8 tick text file(s) there (or run: python convert_only.py path\\to\\file.txt)")
            return

    total = 0
    for src in sources:
        if not src.is_file():
            print(f"!! Not a file: {src}")
            continue
        total += convert_file(src, ROOT)

    made = sorted(p.name for p in ROOT.glob("date=*") if (p / "ticks.parquet").is_file())
    print(f"\nDone. {total} new day partition(s) written into {ROOT}")
    print(f"Repo now has {len(made)} date= day folders total.")
    print("To analyze, double-click RUN_STUDY.bat - or upload the repo with git and ask me to run it.")


if __name__ == "__main__":
    main()
