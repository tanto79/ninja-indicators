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

### 7.2 Source-session check: which prior levels (user's corrected framing)

Important clarification recorded for fidelity: **the replay never tested a current session's own
developing levels** — levels activate only after their source session closes (`study.py` filter
`source_session_id != view session`). Levels from **all** completed sessions were tested (up to ~60
back); the user's chart (`MaxSessionsToShow = 60`) shows exactly this universe for the 2-month
dataset, so the full-sample results are chart-faithful. The gap ≤ 20 re-slice below is kept as a
robustness check only (it barely changes anything). Splitting by source:

| subset (first flips) | n | OOS-positive cells | mean IS | mean OOS |
|---|---|---|---|---|
| **1st flip on a naked level (gap ≤ 20)** | 460 | **13/15** | +4.30 | **+6.05** |
| 1st flip on any chart-visible level (gap ≤ 20) | 1,194 | 22/27 | +2.17 | +3.15 |
| ... short side | 555 | 11/15 | +3.23 | +5.11 |
| ... long side | 639 | 14/24 | +2.60 | +0.08 |
| prev-Globex (overnight) only | 151 | too few for walk-forward cells | pooled r20 0.554 | − |
| prev-RTH (yesterday) only | 125 | too few for walk-forward cells | pooled r20 0.493 | − |

Pooled 10-min reach (control 0.744 / 0.442):

- first flip on **naked** levels: r8 0.903, r20 **0.616** (+17 pts over control) — the strongest
  level-attribute found in the study
- first flip on overnight-Globex levels: r20 0.554
- first flip on yesterday-RTH levels: r20 0.493

So the user's framing sharpens the top candidate: **the first flip of a naked level is the A+ trade
of this study** (~3–4/day at n=460 over 2 months, OOS-positive in 13/15 cell families, and OOS mean
*above* IS mean). Consistent with market microstructure: a level that has never been traded is the
one where resting interest is real.

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

## 10. Plain-English trade rules (from the OOS-validated cells)

Cost model: 1-tick slippage on market entry, 1-tick on the stop exit, target exits on a limit
(no slip), ~$2.50/side commission = **2.4 ticks round trip**. Net numbers below include that.

### Rule A — the A+ setup: FIRST flip of a NAKED level (~3.5/day, both directions)

- A *naked* level = one the market has never traded since its session completed (the green
  "naked" labels / prior H-L / RTHC that have sat untouched).
- **LONG:** a level acting as **RESISTANCE** above the market (the level is above the opening
  price; price has kept rejecting below it) breaks **up** through it and holds above for 180 s →
  it flips to **support** → **buy the breakout**. Stop 32 ticks (8 pts) below entry. Target
  60 ticks (15 pts). Exit at market if neither hits within 10 minutes.
- **SHORT:** a level acting as **SUPPORT** below the market breaks **down** through it and holds
  below for 180 s → it flips to **resistance** → **sell the breakdown**. Stop 32 ticks (8 pts)
  above entry. Target 60 ticks (15 pts). Same 10-min time stop.

  (In every case the direction of the trade is the direction price pushed through and held — the
  role after the flip is what the level becomes, support or resistance as on the chart label.)
- Expected value: +5.4 ticks/trade gross, **≈ +3.0 ticks net ≈ $38/contract** across 464 signals
  (244 short / 220 long). Win rate 15% — psychologically brutal: occasional 60-tick wins carry 32-tick
  losers. Profit factor 1.62.
- Strongest variants (small n): naked VPOC first flips (h1800: 30/40 stop/target).

### Rule B — broader fallback: first flip of ANY chart level (~9/day)

Same trigger (first flip today of any tracked level — resistance up-flips to support = long,
support down-flips to resistance = short, tolerance 0, accept 180 s), same 32/60 shape.
Lower quality: expect roughly half the naked-level edge; the short side carried the OOS score
(11/15 positive), the long side did not (14/24, ~flat).

### Rule C — do NOT trade

- 2nd+ flips of the same level on the same day (except naked variants in Rule A) — chop, ~0 edge.
- Flip entries in the last ~1 hour of RTH (follow-through collapses after 5.5h into the session).
- Fading holds/clean rejects standalone. Deep-break or long-break conditioning (see 7: collapsed OOS).

## 11. The trader-style study (confirmation modes, Held xN, volume nodes, and why the live week made money)

Everything above was procued by a "robot" frame: chase every signal, fixed bracket, hold 10/30 minutes.
The user trades differently: limit fills AT the level, 8-tick stops, 16-tick scalps (46 seconds to a
couple of minutes), reads the volume-by-price profile, and picks 3-10 trades a day instead of hundreds.
This section re-runs the same state machine in THAT frame. The signals here are limit fills at the
level price (no chase), exits measured as the maximum favorable excursion (MFE) available within
5 minutes of the fill, MAE within 60 seconds, split by the label state at fill time.

### 11.1 Does the confirmation method change flip quality? (time vs ticks vs volume)

| Confirm | Flips/day | Reach 8 / 20 ticks in 10 min | Best net cell (all flips) |
|---|---|---|---|
| 30s | 396 | 82% / 50% | -0.6 tk |
| 60s | 279 | 82% / 50% | -0.6 tk |
| 90s | 226 | 82% / 49% | +0.2 tk |
| 120s | 193 | 82% / 49% | -0.2 tk |
| 180s | 154 | 82% / 49% | -0.9 tk |
| 6 ticks beyond | 8.7 | 91% / 64% | +2.8 tk (n=41, thin) |
| 12 ticks beyond | 1.4 | too rare | n/a |
| 3000 contracts | 0.5 | only 24 flips in 2 months | n/a |
| 8000 contracts | 0.0 | zero flips | n/a |

Read: shortening the timer adds many more signals with zero quality gain (the follow-through rates are
identical); 6-ticks-beyond confirmation is rarer but the flips that pass are genuinely stronger
(91%/64% vs 82%/49%); volume confirmation at 3k/8k contracts is dead on arrival at TicksPerRow=1 -
a single-tick volume print is a handful of contracts, so 3,000 never accumulates before price is 20+
ticks away or the level has already been reclaimed. Volume confirmation would need thresholds in the
tens, not thousands. Net: the confirmation switch is not where the edge lives; 180s is fine, 6-tick
confirmation is the only variant worth a second look.

### 11.2 Does "Held xN" predict how far price will travel? (No.)

Median favorable excursion within 5 minutes of a limit fill at the level, by the held count shown on
the chart at that moment (all 44 RTH sessions, 385k bounce fills):

| Held xN at fill | 0/1 | 2 | 3 | 4 | 5+ |
|---|---|---|---|---|---|
| median MFE (ticks) | 16 | 16 | 16 | 16 | 15 |
| reached +32 within 5 min | 21% | 23% | 21% | 23% | 21% |

The X in "Held xN" is session-age and touch-count, not predictive of travel size. A level that has
held 8 times travels like one that has held twice. The number is confirmation that the level has been
respected - it is not a power meter. This overturns the original premise of the study (and the
Grok spec's hold_buckets.csv framing).

### 11.3 Volume-by-price nodes at the level DO matter - and tops are cleaner than bottoms

The user reads levels against the CURRENT session's developing volume-by-price profile (how today's
tape is distributing around the old level), so the node tag is computed from the developing VbP up to
the last completed 30-min TPO boundary before the fill - no lookahead. Node: LVN = level's price bar
in the bottom quartile of the local profile, HVN = top quartile. Bounce fills (limit at level),
split side, live-trading week (Nov 16-19, 28k fills):

| Node / side | n | median MFE | p75 MFE | reach +16 | reach +32 | stop-hit (8t) in 60s |
|---|---|---|---|---|---|---|
| HVN short | 870 | **48** | 106 | 79% | **63%** | 75% |
| LVN short | 479 | 39 | **122** | 71% | 56% | 53% |
| MID short | 4,193 | 37 | 71 | 90% | 66% | 64% |
| LVN long | 2,502 | 19 | 39 | 55% | 33% | 69% |
| MID long | 6,192 | 26 | 46 | 67% | 42% | 71% |
| HVN long | 3,206 | 28 | 47 | 78% | 45% | 73% |

Retest-after-flip fills, same cut (live week):

| Node / side | n | median MFE | reach +16 | stop-hit (8t) in 60s |
|---|---|---|---|---|
| LVN short | 1,563 | 40 | 72% | 57% |
| MID short | 2,745 | 32 | 81% | 65% |
| HVN short | 460 | 30 | 80% | **36%** |
| MID long | 2,020 | 17 | 59% | 60% |
| LVN long | 615 | 22 | 67% | 57% |
| HVN long | 223 | **4** | 27% | **77%** |

Cross-check with the SOURCE-session profile instead (where the level was born; 385k fills, all 44
days): HVN short reach +32 = 25% vs HVN long 17%, LVN short 24% vs LVN long 19% - the side
asymmetry holds in both framings and in every sample.

Reads:
- The user's rule "short resistance in a low-volume node or at the high of the volume profile" is in
  the tape: HVN/LVN shorts carry the biggest right tails (LVN-short p75 = 122 ticks on the live week).
- "Cautious on support longs" is confirmed in every node and both entry types: longs reach +32 at
  roughly half the rate of shorts.
- The single worst cell in the dataset: retest longs at a level sitting in the current session's HVN
  (median travel 4 ticks, stop hit 77% of the time within 60s) - long retests into a high-volume
  shelf are the highest-percentage loss reported anywhere in this study.
- Best stop-survival combo: retest shorts at HVN (80% reach +16, only 36% hit the 8t stop within 60s).

### 11.4 Why the live week made 100+ ticks/day while the study said "nothing here"

Putting all cuts together on the user's live week (Nov 16-19):

- Median available move from a limit fill at a level: 25-40 ticks (vs ~14-16 on an average day).
- 75-87% of level touches reached +16 within 5 minutes; HVN shorts 94%.
- BUT 60-74% of the same fills saw 8 ticks against within 60 seconds - the 8-tick stop is run over
  roughly half the time on indiscriminate entries. The user's edge is the selection: which touches to
  take (nodes, trend, momentum) and which to skip.
- With-trend bounces win 62% vs 34% counter-trend; first flips are always directionally aligned with
  the day's move from the open.
- Take-everything mechanical versions of any of these rules lose after costs; the discretionary
  selections do not. The 2.4-tick round trip is what kills high-frequency mechanical versions.

Practical exit takeaway (the user's actual question): on active days the tape gives 25-40 ticks off a
respected level, most of it in the first two-five minutes. A flat 16-tick take exists in the data as
the "safe" exit - but scale-outs toward 24/32 are statistically supported on the short side at levels
sitting in HVNs, and unsupported for longs.

### 11.5 files

- tables/confirm_mode_sweep.csv - confirmation modes, flip counts, reach rates, best cells
- tables/heldn_mfe_summary.csv - Held xN vs MFE buckets
- tables/vbp_node_summary.csv - volume node / side cuts
- tables/timstyle_*.csv - 16-tick scalps cut by window/stop
- parquet detail (not committed): heldn_mfe.parquet, vbp_fills.parquet, align_*_trades.parquet, chartstyle_trades.parquet

### 11.6 AMT acceptance test - the user's actual method, validated

The user's stated method (Dalton-style auction theory): the current session reacts to ESTABLISHED
levels (previous/naked session levels, never developing ones), and the tell is the current session's
DEVELOPING volume profile - an established level that sits above today's developing value area where
TODAY'S volume is NOT building at the level's price is being REJECTED by the auction; volume
accepting at the price means the auction has migrated there.

Mechanization (no lookahead): at every limit fill at a prior/naked level,
  acceptance = volume TODAY at the level's price bar / volume TODAY at today's developing POC
  (developing profile as of the last completed 30-min TPO boundary; first boundary excluded)
  position   = level bar vs today's developing 70% value area (above / in / below)
Travel measured as MFE within 5 minutes of the fill.

BOUNCE SHORTS, all 44 days (n=177k fills): median MFE / reach +32 by acceptance x position
  low-accept + above-VA :  15.0 tk / 23.7%   <- rejection at an old level above value = the edge
  mid-accept  + above-VA :  13.0 tk / 16.7%
  high-accept  + above-VA:   9.0 tk / 14.1%   <- acceptance = the level is about to go

RETEST SHORTS, all 44 days (n=154k): same ordering - low-accept/above +32 = 19.6% vs
high-accept/above 4.5%.

LIVE WEEK (Nov 16-19), the user's exact scenario:
  bounce short, low acceptance, level above today's developing VA: n=52 (~13/trading day),
  median MFE 103 ticks, 100% reached +16, only 27% hit an 8t stop within 60s.
  bounce short, HIGH acceptance, level above VA: n=73, median MFE 8 ticks, 14% reached +16,
  86% hit the 8t stop. Same levels, same side - acceptance flips the outcome.

CAVEATS:
- The effect is a SHORT-side effect. Longs show no reliable acceptance gradient (the clean-tops /
  sloppy-bottoms asymmetry again).
- Low-acceptance/shorts get part of their edge on high-range days; on the all-days cut it is real
  but modest (15 vs 9-13 median).
- This validates the technique, not a mechanical system: the n=52 live-week cell includes overlap
  (multiple fills at the same level-day). De-clustering before trading it is mandatory.

tables/amt_acceptance_summary.csv - full acceptance x position grid
