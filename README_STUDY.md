# How to run the Level Role Study on your PC (stupid easy)

You don't need PowerShell. You need Python on the machine once.

## One-time setup (5 minutes)

1. Install Python from https://www.python.org/downloads/ if you don't have it.
   On the first screen, **check "Add python.exe to PATH"** before clicking Install Now.
2. Put this repo folder anywhere, e.g. `C:\ninja-indicators`. (Your tick data must already live in
   the repo as `date=YYYY-MM-DD/ticks.parquet` folders — that's how you uploaded it.)

## Every time you want fresh results

3. **Double-click `RUN_STUDY.bat`.** That's it.
   - First run: it installs the three Python packages it needs (pandas, pyarrow, pytz) and then
     runs the study. Expect roughly **10–20 minutes** for 2 months of ES tick data.
   - Later runs: same double-click, same wait.
4. When it finishes, results are in `results_v2/`:
   - `FINDINGS.md` — the interpretation
   - `tables/*.csv` — every rate table and stop/target grid
   - `signals.parquet`, `episodes.parquet` — raw tradeable signals if you want to dig deeper

## Optional: skip the slow parts while experimenting

Open a Command Prompt in the repo folder and run:

```
python run_study.py --no-sim
```

That does the replay + rate tables but skips the stop/target simulation (fine to sanity-check that
new data loaded correctly; the grids won't be updated).

## What "dates" control the study

The study uses every `date=YYYY-MM-DD/ticks.parquet` folder in the repo. Drop new date folders in
(today's data exported from NinjaTrader the same way as before), double-click, done.

## Converting a new NT8 tick text file (convert only, no analysis)

If you download more data from NinjaTrader 8 as one big tick text file, you only need the
conversion step — not the full study:

1. Double-click `RUN_CONVERT.bat`. It converts every tick text file sitting in `data\ticks\`
   (drop them there first), or run it on one file anywhere:
   `python convert_only.py C:\path\to\yourfile.txt`
2. Each day found in the file becomes its own `date=YYYY-MM-DD/ticks.parquet` folder in the
   repo root, in exactly the format the study reads. Lines that don't parse are skipped.
3. Then either double-click `RUN_STUDY.bat` to analyze on this PC, or push the repo to GitHub
   with git and ask me to run the analysis.

Expected line format (same as the original export): `YYYYMMDD HHMMSS fff;last;bid;ask;volume`
e.g. `20260921 083000 123;6105.25;6105.00;6105.50;4`. If your NT8 export looks different,
send the first 2-3 lines and the converter will be adapted.

If a `date=` folder already exists (e.g. a Globex evening split across days), the new rows are
merged into it and re-sorted — nothing is lost or duplicated.

## Adding more data (e.g., the Sep 2026 contract)

Your current dataset (Dec-26 contract) starts 2026-09-21. To extend the study back into Jun–Sep
2026 with the **Sep-26 contract** (front month until the quarterly roll, ~Sep 11–18):

1. Export each day the same way you did for Dec-26: a folder `date=YYYY-MM-DD/` containing
   `ticks.parquet` with the same columns (naive UTC `ts_utc`, `last`, `bid`, `ask`, `volume`, `ord`).
2. Only include days when the **Sep contract was the active/liquid contract** — stop the export at
   the roll date (don't pad with post-roll thin volume, and don't mix contracts in one day folder).
3. Make sure the days don't overlap the existing dataset (keep `date > 2026-09-20` folders Dec-only;
   Sep-export last day should be on/before the roll, i.e., well before 2026-09-21).
4. Drop the folders into the same repo directory as the existing `date=...` folders.
5. Double-click `RUN_STUDY.bat`. It picks up everything found; expect ~1.5–2x the runtime.

The walk-forward in/out-of-sample split rebalances automatically with the new day count.
