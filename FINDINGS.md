# Level Role Study v2 — Findings

Data: ES RTH sessions (8:30–16:00 CT), 2026-09-21 → 2026-11-19 (65 RTH sessions), 9 level types
(VPOC, TPOPOC, VVAH, VVAL, TPOVAH, TPOVAL, HI, LO, RTHC), tick size 0.25, tolerance 0 ticks,
accept threshold 180 s, signals de-clustered at 8 ticks (spec 7.5).

Signals analyzed (20-min max look-ahead, plus fixed 10-min and 30-min horizons):

| Scenario   | n      | What it is |
|------------|--------|------------|
| hold       | 44,781 | clean_reject 38,232 / flipped_retest 4,494 / reclaim 2,055 |
| flip       | 6,777  | role flip accepted by the state machine |
| break      | 42,890 | independent break episodes (≥8 ticks re-separated) |
| control    | 9,750  | 150 random entries per RTH session per direction (the "is this just drift?" baseline) |

All rates below use the **10-minute horizon** unless stated. Everything is compared against the
CONTROL row, because at 10 minutes a random ES entry reaches 8 ticks 74% of the time and 20 ticks
44% of the time on drift alone. A signal is only interesting if it beats that.

**CONTROL (random entry, 10-min):** 4t 87.7% · 8t 74.4% · 12t 63.2% · 20t 44.2% · traded-through-level-4t 88.3%

---

## 1. Does "held xN" matter? — Mostly no, but a small mid-count bump

10-min follow-through away from the level, by held count:

| bucket | n      | 8t    | 12t   | 20t   | control 20t |
|--------|--------|-------|-------|-------|-------------|
| x1     | 3,005  | .816  | .706  | .552  | .442        |
| x2–x3  |   605  | .843  | .772  | .610  |             |
| x4–x5  |   580  | .847  | .745  | .593  |             |
| x6–x10 | 1,487  | .829  | .728  | .564  |             |
| x11–x20| 3,091  | .833  | .743  | .590  |             |
| x21+   | 36,013 | .814  | .709  | .534  |             |

- There is a **mild hump at x2–x10** (~55–61% vs 55% at x1 and 53% at x21+), but it is not strong
  or monotone enough to filter on by itself.
- What actually discriminates is **fresh_visit** (de-clustered visit number) — same shape, so the
  chart's "held x45" mostly measures chop density in the session, not increasing importance.
- Very high counts (x20+) are slightly *worse* than low counts: late-session levels that have
  already absorbed 20+ rejections follow through less.
- Every hold bucket beats the control's 20-tick rate by 9–17 points, so **fresh levels you watch do
  have better immediate follow-through than random spots** — but as shown in §4, that does not
  survive as a standalone fade trade.

## 2. Breaking: snap back or flip?

From independent break episodes (n=42,890): **~98% snap back** (return to the role side) within the
episode window; ~1.8% flip at the 180 s accept threshold. The flip probability depends almost
entirely on how long you let the break run (the accept setting):

| accept | flip prob (all 9 level types) |
|--------|-------------------------------|
| 30 s   | ~4.6–5.2% |
| 60 s   | ~3.3–4.0% |
| 120 s  | ~2.3–2.9% |
| 180 s  | ~1.9–2.5% |
| 300 s  | ~1.4–1.9% |

- Break durations are extremely short: median 0.2 s, p90 6.6 s, p95 28 s, p99 ~17 min.
- No level type is meaningfully more flip-prone than another (all 1.4–2.5% at 180 s).
- Practical read: **snapping back is the base rate; flips are rare events**, so trade the flip, not
  the break's failure (see §4). If you want to detect flips faster, lower the accept setting — but
  that multiplies flip count (~4x at 30 s) which trades signal frequency for noise.

## 3. Direction asymmetry (a real, if modest, pattern)

- Breaks that move **down** follow through better: 20t reach 57.5% (breaks down) vs 50.4% (up).
- Same for flips: role flipped to short side 51.0% vs long side 47.2% (10-min).
- Matches the sample period's drift; recheck in different regimes.

## 4. Stop/target grids — the actual answer

Path-simulated on every tick after entry, tie scored as a loss, n-weighted across level types.
Expectancy is ticks per trade (×$12.50 per tick on 1 contract).

**Key comparison at 10-min horizon (same stop/target cell):**

| scenario | best cell (stop/target) | expectancy/tick | PF | win rate | **CONTROL same shape** (38/4) |
|----------|------------------------|-----------------|----|----------|-------------------------------|
| flip     | 32 / 4                 | **+0.99**       | 1.40 | 88%   | +0.22 (PF 1.07) |
| break    | 32 / 40                | +0.11           | 1.01 | 23%   | +0.22 |
| hold     | 36 / 60                | +0.02           | 1.00 | 11%   | +0.22 |

Even random entries make ~+0.2 ticks with a wide stop and 4-tick target in this sample (drift +
timeout accounting). So the honest read is **edge over control ≈ +0.75 ticks/trade for flips**, and
≈ **0 for holds and breaks**.

**Flip scenario — the only scenario that passed out-of-sample validation.** Walk-forward
(best cell chosen on the first 30 days, evaluated blind on the last 14):

- 22 of 27 flip cells OOS-positive (median +0.66 ticks).
- Consistently OOS-positive small-target scalp family: **stop 26–34, target 4**, across
  HI, TPOVAL, VVAL, LO — OOS +1.0 to +1.8, PF 1.4–1.9.
- Larger-sample variants: VPOC 30-min horizon 12/60 → IS +2.12 → OOS +2.07 (PF 1.24, n=156).
- Best single OOS: HI 34/16 → +5.37 (n=81, PF 1.75 — treat with caution, small n).
- CONTROL walk-forward by the same method: OOS +0.22–0.77. The flip cells are ~2–10× the control.

**Hold scenario — no tradeable edge as a fade.** 0 of 27 hold cells were OOS-positive; typical OOS
−0.3 to −2.4 ticks. Fading every clean reject loses.

**Break scenario — mixed, not yet tradeable.** 15 of 27 cells OOS-positive but the median is
+0.10 and several of the "best" OOS cells had near-zero in-sample values (i.e., luck). Best
in-sample-consistent: VVAH 14/60 → OOS +4.50 (PF 1.45, n=888) and LO 32/24 → OOS +3.79; the
two-horizon-consistent (both 10- and 30-min positive) cells were TPOPOC 36/4 and TPOVAL 10/4.
Treat break continuation as a follow-up research thread with more data, not a live setup.

## 5. Failure metrics (what "held" fails at)

- Within 10 minutes, 87.4% of holds get traded through by ≥4 ticks at some point (control: 88.3%)
  — being traded *near* is normal; what matters is whether the follow-through call comes first.
- clean_reject holds reached 12 ticks before the next independent break 12.5% of the time.
- Spec-window (until next touch/break) numbers are huge (93% reach 8 ticks) because the window
  ends at the next event — median window is under 1 second. **Never base sizing on spec-window
  reach rates; use the 10/30-min columns.**

## 6. Recommended starting playbook (numbers, not advice)

1. **Flip scalp = your A+ setup.** Enter on accepted flip, in flip direction; stop 28–36 ticks
   (~7–9 pts), target 4 ticks (~1 pt) to start — or the VPOC-style 12/60 on the 30-min horizon for
   fewer/bigger trades. Validated OOS; ~+1 tick/trade gross before costs (~+0.6 net of control).
2. **Do not fade holds** as a stand-alone system, regardless of held-xN label.
3. **Break continuation** is on watch only until more months confirm.
4. Expect short-side signals to perform better in this sample.

## 7. Context filters: what survives out-of-sample, what doesn't (added after first findings)

### 7.1 Upside check (long-side first flips)

Same walk-forward grid run split by flip direction; every cell also compared to a
**same-direction, same-context random control** (random long entries on days trading above the
open, etc.). 10-min horizon, first flip of the level on its side of the open only:

| context | signal n | edge vs same-context control (r8 / r20) |
|---------|----------|------------------------------------------|
| LONG first flip, day up >5 pts | 991 | +0.116 / +0.076 |
| LONG first flip, day flat ±5 pts | 161 | +0.100 / +0.075 |
| SHORT first flip, day down >5 pts | 795 | +0.134 / +0.139 |
| SHORT first flip, day flat ±5 pts | 170 | +0.176 / +0.253 |

So the long side has a **real but smaller edge** (~half the short side's at the 20-tick horizon).
Two additional structural facts:

- **First flips are always direction-aligned with the day.** In 2,117 first flips, not one
  counter-trend first flip existed: a level's first flip up never happened with the market more
  than 2 ticks above-zero on the wrong side, and vice versa (today's profile levels are built from
  the same tape, so their first flip follows the day's flow). Counter-trend flips only exist as 2nd+
  flips of a level (chop), which is consistent with "first flip" being the quality filter.
- Because of that, the practical long-side setup is just the mirror: first flip up on a day already
  trading above its open → buy continuation. It's tradeable, expect roughly half the short side's
  follow-through in this sample (and the sample leaned down).

All flip signals were filtered on context and re-validated walk-forward (best cell chosen on the
first 30 days, judged blind on the last 14). Summary across all cells of each subset:

| subset | signals | OOS-positive cells | median OOS (ticks) | mean OOS (ticks) |
|--------|---------|--------------------|--------------------|------------------|
| all flips                       | 6,777 | 22/27 | +0.66 | +0.67 |
| flip after deep break (13t+)    |   737 |  4/15 | −0.76 | −1.16 |
| flip after long break (>180 s)  |   763 |  3/15 | −0.76 | −1.08 |
| **first flip on that level**    | 2,117 | 22/27 | +1.03 | +1.64 |
| flips in first 90 min           | 1,713 | 21/27 | +0.97 | +2.84 |
| short-side flips                | 3,325 | 22/27 | +1.62 | +1.33 |
| **short-side + first flip**     |   965 | 13/15 | **+3.00** | **+3.21** |

Reads:

- **Deep/long prior-break filters are traps.** They looked +10 points better on pooled 10-min
  reach rates, then went negative out-of-sample. Classic data-mining artifact — this is precisely
  how the "edge" disappears for other people's research, too.
- **First flip on a level** (a level that flips back and forth repeatedly is a fake or exhausted
  level) improves OOS performance and keeps decent sample size.
- **Short side** follow-through held up and matched the direction asymmetry in §3.
- **The short-side + first-flip combo is the best candidate found so far**: 13/15 cells
  OOS-positive, ~+3 ticks/trade mean, and OOS mean actually *exceeded* IS mean. Frequency is
  ~7 trades/day across all RTHC/VPOC/etc. levels pooled.
- **Important honesty note:** these filters were picked after seeing the pooled data, so the
  OOS numbers above are a partial validation, not a clean one. The next calendar month of tick
  data is the true out-of-sample test. Keep the filter list small (the two above) and resist
  adding more until they earn it.

## 8. Caveats (read before live-trading any of this)

- **2 months, one sample.** 44 RTH days of signals + walk-forward; regimes change. Re-run monthly as
  new tick data lands (rename with new `date=YYYY-MM-DD/ticks.parquet` partitions).
- **No costs modeled.** Slippage/commission are set to 0 in config. At 4-tick targets, ~2 ticks
  round-trip of costs eats most of the flip edge — you need the bigger-target variant or better
  fills to net much.
- **Control semantic**: control entries measure MFE from the random entry price; signal entries
  measure from the level. This slightly flatters signals; the flip edge kept ~+0.7 ticks anyway.
- **Small-n cells** (e.g., HI 34/16 with n=81 OOS) are shown but not recommendations.
- Naked levels (never touched before) and prior-session-only states remain underpowered here.
- The signal-vs-level MFE convention and the 8-tick de-clustering count ~690 holds/day —
  stricter than raw chop (previous study's 7,600/day) but not a guarantee of independence.

## 9. How this was produced

- `results_v2/events.parquet` — full re-replay of roles with the fixed state machine (the original
  pipeline missed the Flipped branch, so reclaims were double-counted as flips).
- `results_v2/signals.parquet`, `results_v2/episodes.parquet` — de-clustered tradeable signals.
- `results_v2/tables/*.csv` — all rate and grid tables, including `walk_forward_CONTROL.csv`.
- Code: `level_role/analysis.py` (this run), `level_role/study.py` (streaming replay).
- Run it yourself: see `README_STUDY.md` (double-click `RUN_STUDY.bat` on Windows).
