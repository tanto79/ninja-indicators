"""One-command study runner: python run_study.py

Steps: sessions -> levels -> role replay -> Part D analysis + stop/target grids.
Outputs land in results_v2/ (tables CSVs, signals/episodes parquet, FINDINGS inputs).
"""

from __future__ import annotations

import argparse
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

from level_role.analysis import run_analysis
from level_role.config import Settings
from level_role.study import find_partitions, run_study


def main() -> None:
    ap = argparse.ArgumentParser(description="Level role study — full run")
    ap.add_argument("--no-sim", action="store_true",
                    help="skip the stop/target path simulation (faster)")
    args = ap.parse_args()

    settings = Settings(root=Path(__file__).parent)

    files = find_partitions(settings)
    ticks_dir = settings.ticks_dir
    if not files and ticks_dir.exists() and any(p.is_file() for p in ticks_dir.rglob("*")):
        print("[run] no parquet found; converting raw tick text in data/ticks/ ...")
        from level_role import ticks as ticks_mod
        ticks_mod.convert_folder(settings)
        files = find_partitions(settings)
    if not files:
        raise SystemExit(
            "No tick data found.\n"
            "Put NT8 tick text files in data/ticks/, or tick parquet partitions "
            "(date=YYYY-MM-DD/ticks.parquet) beside this script."
        )

    t0 = time.time()
    paths = run_study(settings)
    print(f"[run] replay done in {time.time() - t0:.0f}s "
          f"-> {paths['events'].name}: {len(find_partitions(settings))} partitions")

    t1 = time.time()
    run_analysis(settings, skip_simulation=args.no_sim)
    print(f"[run] analysis done in {time.time() - t1:.0f}s "
          f"-> {settings.results_dir / 'tables'}")


if __name__ == "__main__":
    main()
