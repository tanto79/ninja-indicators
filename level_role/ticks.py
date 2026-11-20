"""NT8 tick text → Parquet (streamed, partitioned by day)."""

from __future__ import annotations

from pathlib import Path

import pandas as pd
from tqdm import tqdm

from .config import Settings


def _parse_line(line: str) -> dict | None:
    line = line.strip()
    if not line or line.startswith("#"):
        return None
    try:
        head, last, bid, ask, volume = line.split(";")
        date_part, time_part, frac = head.split()
        frac = frac.ljust(7, "0")[:7]
        ts = pd.Timestamp(
            f"{date_part[0:4]}-{date_part[4:6]}-{date_part[6:8]} "
            f"{time_part[0:2]}:{time_part[2:4]}:{time_part[4:6]}.{frac}"
        )
        return {
            "ts_utc": ts,
            "last": float(last),
            "bid": float(bid),
            "ask": float(ask),
            "volume": int(float(volume)),
        }
    except Exception:
        return None


def convert_file(path: Path, out_dir: Path, contract: str = "ES") -> int:
    rows: list[dict] = []
    current_day: str | None = None
    n_written = 0

    def flush(day: str, buf: list[dict]) -> None:
        nonlocal n_written
        if not buf:
            return
        df = pd.DataFrame(buf)
        df["ord"] = range(len(df))
        part = out_dir / f"contract={contract}" / f"date={day}"
        part.mkdir(parents=True, exist_ok=True)
        out = part / "ticks.parquet"
        if out.exists():
            old = pd.read_parquet(out)
            df = pd.concat([old, df], ignore_index=True)
        df.to_parquet(out, index=False)
        n_written += 1

    with path.open("r", encoding="utf-8", errors="replace") as f:
        for line in f:
            rec = _parse_line(line)
            if rec is None:
                continue
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
    return n_written


def convert_folder(settings: Settings) -> int:
    settings.ensure_dirs()
    src = settings.ticks_dir
    files = sorted(p for p in src.rglob("*") if p.is_file() and p.name != "PUT_TICK_FILES_HERE.txt")
    if not files:
        print(f"  No tick files in {src}")
        print("  Drop NT8 tick text files there and re-run.")
        return 0
    n = 0
    for path in tqdm(files, desc="convert"):
        n += convert_file(path, settings.parquet_dir)
    return n


def load_all_ticks(settings: Settings) -> pd.DataFrame:
    """Load all parquet partitions sorted by time (for smaller sample weeks)."""
    parts = sorted(settings.parquet_dir.rglob("ticks.parquet"))
    if not parts:
        return pd.DataFrame()
    frames = []
    for p in parts:
        df = pd.read_parquet(p)
        frames.append(df)
    df = pd.concat(frames, ignore_index=True)
    sort_cols = ["ts_utc", "ord"] if "ord" in df.columns else ["ts_utc"]
    return df.sort_values(sort_cols).reset_index(drop=True)
