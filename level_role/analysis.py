"""
Part D — metrics that answer the study questions:

  1. Does "held xN" matter?                     (xN significance, raw + fresh-visit)
  2. Break -> snap back or flip?                (outcome probabilities, accept sweep)
  3. Flip / break follow-through                (MFE curves, MAE)
  4. Which stop/target for each scenario?       (path-simulated expectancy grid)
  5. Is any of it better than chance?           (random-entry control)

De-clustering (spec 7.5): a hold or break counts as an independent signal only
after price moved at least min_separation_ticks away from the level since the
previous counted signal on that level. Every rate shows n with a Wilson 95%
interval; cells below min_n are suppressed. Walk-forward: each bucket's best
(stop, target) is chosen on the first 70% of trading days and reported on the
remaining 30%.
"""

from __future__ import annotations

import math

import numpy as np
import pandas as pd
from tqdm import tqdm

from .config import Settings
from .study import TickStore, find_partitions

MFE_THRESHOLDS = (4, 8, 12, 16, 20, 24, 30, 40, 60)
ACCEPT_SWEEP = (30, 60, 120, 180, 300)
FOLLOW_THROUGH_CALL_TICKS = 12
GAVE_WAY_TICKS = 4
# post-confirmation horizons in seconds; "spec" = until next touch/break/session end
HORIZONS: tuple[tuple[str, int | None], ...] = (("spec", None), ("h600", 600), ("h1800", 1800))
HORIZON_SECONDS: dict[str, int] = {h: s for h, s in HORIZONS if s is not None}


# ---------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------

def wilson(k: int, n: int, z: float = 1.96) -> tuple[float, float, float]:
    if n == 0:
        return float("nan"), float("nan"), float("nan")
    p = k / n
    denom = 1 + z * z / n
    centre = (p + z * z / (2 * n)) / denom
    half = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / denom
    return p, centre - half, centre + half


def _stat(base: dict, k: int, n: int, label: str) -> dict:
    p, lo, hi = wilson(k, n)
    suppressed = n < 30
    return {
        **base,
        "metric": label,
        "n": n,
        "rate": np.nan if suppressed else p,
        "wilson_lo": np.nan if suppressed else lo,
        "wilson_hi": np.nan if suppressed else hi,
        "suppressed_below_min_n": suppressed,
    }


def _mae_row(base: dict, n: int, mae: np.ndarray) -> dict:
    if n == 0:
        vals = {f"p{q}": np.nan for q in (50, 75, 90, 95)}
    else:
        vals = {f"p{q}": float(np.percentile(mae, q)) for q in (50, 75, 90, 95)}
    return {
        **base,
        **vals,
        "metric": "mae_ticks",
        "n": n,
        "rate": np.nan,
        "wilson_lo": np.nan,
        "wilson_hi": np.nan,
        "suppressed_below_min_n": n < 30,
    }


def _horizon_metrics(
    ts: np.ndarray, last: np.ndarray, p: int, n_ticks: int,
    level: float, entry: float, d: int, tsz: float,
) -> tuple[dict, dict, dict, dict, dict]:
    """Per-horizon: (mfe, mae, reach, time, gaveway) — all in ticks.
    gaveway: price traded through the level (holds/flips) by GAVE_WAY_TICKS."""
    mfe_h, mae_h, reach_h, time_h, gave_h = {}, {}, {}, {}, {}
    for hname, hsec in HORIZONS:
        if hsec is None:
            cut = n_ticks - 1
        else:
            cut = int(np.searchsorted(ts, ts[p] + hsec * 1_000_000_000, side="right")) - 1
        cut = max(min(cut, n_ticks - 1), p + 1)
        s_ = last[p + 1: cut + 1]
        if len(s_) == 0:
            s_ = last[p: p + 1]
        if d == 1:
            curve = np.maximum.accumulate(s_)
            mfe_h[hname] = float(curve.max() - level) / tsz
            mae_h[hname] = (entry - float(s_.min())) / tsz
            pcurve = curve
            rel_min = (float(s_.min()) - level) / tsz
            gave_h[hname] = bool(rel_min <= -GAVE_WAY_TICKS)
        else:
            curve = np.minimum.accumulate(s_)
            mfe_h[hname] = float(level - curve.min()) / tsz
            mae_h[hname] = (float(s_.max()) - entry) / tsz
            pcurve = curve
            rel_max = (float(s_.max()) - level) / tsz
            gave_h[hname] = bool(rel_max >= GAVE_WAY_TICKS)
        reach_h[hname] = {}
        time_h[hname] = {}
        for th in MFE_THRESHOLDS:
            hit = bool(mfe_h[hname] >= th)
            reach_h[hname][th] = hit
            if hit:
                tgt = level + th * tsz if d == 1 else level - th * tsz
                j = int(np.searchsorted(pcurve, tgt, side="left"))
                j = min(j, len(pcurve) - 1)
                tick_i = min(p + 1 + j, n_ticks - 1)
                time_h[hname][th] = float(ts[tick_i] - ts[p]) / 1e9
            else:
                time_h[hname][th] = np.nan
    return mfe_h, mae_h, reach_h, time_h, gave_h


# ---------------------------------------------------------------------------
# windows + signals on tick data
# ---------------------------------------------------------------------------

def build_signal_frames(
    events: pd.DataFrame,
    sessions: pd.DataFrame,
    store: TickStore,
    settings: Settings,
) -> tuple[pd.DataFrame, pd.DataFrame]:
    """Returns (signals, episodes): one row per tradeable signal with tick-path
    metrics, one row per break episode with outcome and duration."""
    sess_bounds = {
        r.session_id: (int(r.start_utc.value), int(r.end_utc.value))
        for r in sessions.itertuples(index=False)
    }
    sep = settings.min_separation_ticks * settings.tick_size
    tsz = settings.tick_size

    signals: list[dict] = []
    episodes: list[dict] = []

    groups = events.groupby(["session_id", "level_id"], sort=True)
    for (sid, lid), ev in tqdm(groups, total=groups.ngroups, desc="windows"):
        ev = ev.sort_values("sess_tick_index")
        s_start, s_end_ns = sess_bounds[sid]
        ts, last, _ = store.slice_range(s_start, s_end_ns)
        n_ticks = len(last)
        if n_ticks == 0:
            continue

        level = float(ev["price"].iloc[0])
        ltype = ev["level_type"].iloc[0]
        is_naked = bool(ev["is_naked"].iloc[0])
        is_prior = bool(ev["is_prior"].iloc[0])

        ev_types = ev["event_type"].to_numpy()
        ev_pos = ev["sess_tick_index"].to_numpy().astype(np.int64)

        touch_pos = np.sort(ev_pos[ev_types == "touch"])
        break_pos = np.sort(ev_pos[ev_types == "break_start"])
        flip_pos = np.sort(ev_pos[ev_types == "flip"])

        def _next(arr: np.ndarray, p: int) -> int:
            i = int(np.searchsorted(arr, p, side="right"))
            return int(arr[i]) if i < len(arr) else n_ticks - 1

        def emit(r, scenario: str, subtype: str, d: int, p: int,
                 fresh: int, held_after) -> None:
            entry = float(r.price_at_event)
            mfe_h, mae_h, reach_h, time_h, gave_h = _horizon_metrics(
                ts, last, p, n_ticks, level, entry, d, tsz)
            row = {
                "session_id": sid,
                "trading_day": sid.split("|")[0],
                "level_id": lid,
                "level_type": ltype,
                "scenario": scenario,
                "subtype": subtype,
                "role": d,
                "held_count_after": held_after,
                "fresh_visit": fresh,
                "is_naked": is_naked,
                "is_prior": is_prior,
                "ts": pd.Timestamp(r.ts_utc),
                "pos": p,
                "end": min(_next(touch_pos, p), _next(break_pos, p),
                           _next(flip_pos, p), n_ticks - 1),
                "entry": entry,
                "mfe_ticks": mfe_h["spec"],
                "mae_ticks": mae_h["spec"],
                "break_before_12": np.nan,
                **{f"reached_{th}": reach_h["spec"][th] for th in MFE_THRESHOLDS},
                **{f"time_to_{th}_s": time_h["spec"][th] for th in MFE_THRESHOLDS},
                "gave_way": gave_h["spec"],
            }
            for hname, _ in HORIZONS:
                if hname == "spec":
                    continue
                row[f"end_{hname}"] = int(np.searchsorted(
                    ts, ts[p] + HORIZON_SECONDS[hname] * 1_000_000_000,
                    side="right")) - 1
                row[f"end_{hname}"] = max(1, int(row[f"end_{hname}"]))
                row[f"mfe_{hname}"] = mfe_h[hname]
                row[f"mae_{hname}"] = mae_h[hname]
                row[f"gave_way_{hname}"] = gave_h[hname]
                for th in MFE_THRESHOLDS:
                    row[f"reached_{hname}_{th}"] = reach_h[hname][th]
                    row[f"time_to_{hname}_{th}_s"] = time_h[hname][th]
            signals.append(row)

        # ---- holds + flips ----
        kept_hold_pos = -1
        fresh_visit = 0
        next_break_pos_arr = {}
        for r in ev.itertuples(index=False):
            scenario = "flip" if r.event_type == "flip" else \
                       "hold" if r.event_type == "hold" else None
            if scenario is None:
                continue
            p = int(r.sess_tick_index)
            if scenario == "flip":
                # a flip is meaningful only if it follows an independent break
                emit(r, "flip", "flip", int(r.role), p, np.nan, np.nan)
                continue
            seg = last[kept_hold_pos: p + 1] if kept_hold_pos >= 0 else last[:p + 1]
            excursion = float(np.abs(seg - level).max()) if len(seg) else 0.0
            if kept_hold_pos >= 0 and excursion < sep:
                continue
            fresh_visit += 1
            kept_hold_pos = p
            emit(r, "hold", r.hold_subtype, int(r.role), p, fresh_visit,
                 int(r.held_count_after) if not (r.held_count_after is None or
                 (isinstance(r.held_count_after, float) and math.isnan(r.held_count_after)))
                 else np.nan)

        # failure metric per hold: did an independent break start before 12-tick follow-through?
        hold_rows = [i for i, s in enumerate(signals)
                     if s["session_id"] == sid and s["level_id"] == lid
                     and s["scenario"] == "hold"]
        indep_break_pos = [int(r.sess_tick_index) for r in ev.itertuples(index=False)
                           if r.event_type == "break_start"]
        indep_break_pos = np.array(sorted(set(indep_break_pos)), dtype=np.int64)

        # ---- break episodes (break scenario signals come from independent ones) ----
        kept_ep_pos = -1
        indep_break_positions: list[int] = []
        for r in ev.itertuples(index=False):
            if r.event_type != "break_start":
                continue
            p = int(r.sess_tick_index)
            end_touch = _next(touch_pos, p)
            fl_after = flip_pos[flip_pos > p]
            flipped = bool(len(fl_after) and fl_after.min() <= end_touch)
            end = max(end_touch, p + 1)
            sl = last[p: end + 1]
            side = int(np.sign(float(r.price_at_event) - level))
            if side == 0 or len(sl) == 0:
                continue
            excursion_ok = True
            if kept_ep_pos >= 0:
                between = last[kept_ep_pos: p + 1]
                excursion_ok = bool(len(between) and np.abs(between - level).max() >= sep)
            if excursion_ok:
                kept_ep_pos = p
                indep_break_positions.append(p)
                emit(r, "break", "independent_break", side, p, np.nan, np.nan)
            depth = (float(sl.max()) - level if side == 1 else level - float(sl.min())) / tsz
            end_ts = pd.Timestamp(ts[end], tz="UTC")
            episodes.append({
                "session_id": sid,
                "trading_day": sid.split("|")[0],
                "level_id": lid,
                "level_type": ltype,
                "ts": pd.Timestamp(r.ts_utc),
                "side": side,
                "duration_s": (end_ts - pd.Timestamp(r.ts_utc)).total_seconds(),
                "depth_ticks": depth,
                "flipped": flipped,
                "endpoint": ("flip" if flipped else
                             "touch" if end < n_ticks - 1 else "session_end"),
                "independent": excursion_ok,
            })

        # break_before_12 per hold signal: an independent break before 12-tick follow-through
        for idx in hold_rows:
            s = signals[idx]
            p = int(s["pos"])
            nb = indep_break_pos[indep_break_pos > p]
            if not len(nb):
                s["break_before_12"] = False
                continue
            seg = last[p + 1: int(nb[0]) + 1]
            d = int(s["role"])
            if d == 1:
                got = len(seg) and np.maximum.accumulate(seg).max() >= level + FOLLOW_THROUGH_CALL_TICKS * tsz
            else:
                got = len(seg) and np.minimum.accumulate(seg).min() <= level - FOLLOW_THROUGH_CALL_TICKS * tsz
            s["break_before_12"] = bool(not got)

    return pd.DataFrame(signals), pd.DataFrame(episodes)


# ---------------------------------------------------------------------------
# random-entry control (is any of this better than chance?)
# ---------------------------------------------------------------------------

def random_control(
    sessions: pd.DataFrame, store: TickStore, settings: Settings,
    per_session: int = 150, seed: int = 7,
) -> pd.DataFrame:
    rng = np.random.default_rng(seed)
    tsz = settings.tick_size
    rows = []
    views = sessions[sessions["session_type"] == "RTH"]
    for sess in views.itertuples(index=False):
        ts, last, _ = store.slice_range(int(sess.start_utc.value), int(sess.end_utc.value))
        n = len(last)
        if n < 100:
            continue
        for _ in range(per_session):
            p = int(rng.integers(0, n - 2))
            d = int(rng.choice([-1, 1]))
            entry = float(last[p])
            mfe_h, mae_h, reach_h, time_h, gave_h = _horizon_metrics(
                ts, last, p, n, entry, entry, d, tsz)
            row = {
                "session_id": sess.session_id,
                "trading_day": sess.trading_day,
                "level_type": "CONTROL",
                "scenario": "control",
                "subtype": "random",
                "role": d,
                "held_count_after": np.nan,
                "fresh_visit": np.nan,
                "is_naked": False,
                "is_prior": False,
                "ts": pd.Timestamp(int(ts[p]), tz="UTC"),
                "pos": p,
                "end": n - 1,
                "entry": entry,
                "mfe_ticks": mfe_h["spec"],
                "mae_ticks": mae_h["spec"],
                "break_before_12": np.nan,
                **{f"reached_{th}": reach_h["spec"][th] for th in MFE_THRESHOLDS},
                **{f"time_to_{th}_s": time_h["spec"][th] for th in MFE_THRESHOLDS},
                "gave_way": gave_h["spec"],
            }
            for hname, hsec in HORIZONS:
                if hname == "spec":
                    continue
                cut = int(np.searchsorted(ts, ts[p] + hsec * 1_000_000_000, side="right")) - 1
                row[f"end_{hname}"] = max(1, int(min(cut, n - 1)))
                row[f"mfe_{hname}"] = mfe_h[hname]
                row[f"mae_{hname}"] = mae_h[hname]
                row[f"gave_way_{hname}"] = gave_h[hname]
                for th in MFE_THRESHOLDS:
                    row[f"reached_{hname}_{th}"] = reach_h[hname][th]
                    row[f"time_to_{hname}_{th}_s"] = time_h[hname][th]
            rows.append(row)
    return pd.DataFrame(rows)


# ---------------------------------------------------------------------------
# stop/target simulation (path-based, spec 7.3)
# ---------------------------------------------------------------------------

def _first_hits(sl: np.ndarray, entry: float, d: int, tsz: float,
                stops: list[int], targets: list[int]) -> dict:
    cmax = np.maximum.accumulate(sl)
    cmin = np.minimum.accumulate(sl)
    neg_cmin = -cmin
    n = len(sl)
    out = {}
    for S in stops:
        if d == 1:
            i = int(np.searchsorted(neg_cmin, S * tsz - entry, side="left"))
        else:
            i = int(np.searchsorted(cmax, entry + S * tsz, side="left"))
        out[("s", S)] = i if i < n else n
    for T in targets:
        if d == 1:
            i = int(np.searchsorted(cmax, entry + T * tsz, side="left"))
        else:
            i = int(np.searchsorted(neg_cmin, -(entry - T * tsz), side="left"))
        out[("t", T)] = i if i < n else n
    return out


def simulate_grid(
    signals: pd.DataFrame,
    sessions: pd.DataFrame,
    store: TickStore,
    settings: Settings,
) -> pd.DataFrame:
    """One row per (scenario, level_type, horizon, stop, target). Win/loss/timeout
    tally. A tied hit (one tick spans both stop and target) is scored as a loss —
    the conservative read of 7.3 when tick order can't resolve it."""
    tsz = settings.tick_size
    stops, targets = settings.stop_ticks, settings.target_ticks
    bounds = {
        r.session_id: (int(r.start_utc.value), int(r.end_utc.value))
        for r in sessions.itertuples(index=False)
    }
    cache: dict[str, np.ndarray] = {}

    cells: dict[tuple, dict] = {}
    for r in tqdm(signals.itertuples(index=False), total=len(signals), desc="simulate"):
        s_start, s_end = bounds[r.session_id]
        px = cache.get(r.session_id)
        if px is None:
            _, last, _ = store.slice_range(s_start, s_end)
            px = last
            cache = {r.session_id: px}
            if len(cache) > 3:
                cache.pop(next(iter(cache)))
        p = int(r.pos)
        for hname, _ in HORIZONS:
            if hname == "spec":
                end = int(r.end)
            else:
                end_attr = getattr(r, f"end_{hname}", None)
                if end_attr is None:
                    continue
                end = int(end_attr)
            sl = px[p + 1: end + 1]
            if len(sl) == 0:
                sl = px[p: end + 1]
            if len(sl) == 0:
                continue
            hits = _first_hits(sl, float(r.entry), int(r.role), tsz, stops, targets)
            timeout_pnl = int(r.role) * (float(sl[-1]) - float(r.entry)) / tsz
            key_head = (r.scenario, r.level_type, hname)
            for S in stops:
                stop_i = hits[("s", S)]
                for T in targets:
                    tgt_i = hits[("t", T)]
                    if tgt_i < stop_i:
                        pnl, outcome = (float(T), "win") if tgt_i < len(sl) else (timeout_pnl, "timeout")
                    elif stop_i < tgt_i and stop_i < len(sl):
                        pnl, outcome = float(-S), "loss"
                    elif tgt_i < len(sl) and stop_i < len(sl):
                        pnl, outcome = float(-S), "loss_tie"
                    elif tgt_i < len(sl):
                        pnl, outcome = float(T), "win"
                    elif stop_i < len(sl):
                        pnl, outcome = float(-S), "loss"
                    else:
                        pnl, outcome = timeout_pnl, "timeout"
                    cell = cells.setdefault((*key_head, S, T),
                                            {"n": 0, "wins": 0, "losses": 0, "timeouts": 0,
                                             "sum": 0.0, "gp": 0.0, "gl": 0.0, "mae": 0.0})
                    cell["n"] += 1
                    cell["sum"] += pnl
                    if outcome.startswith("win"):
                        cell["wins"] += 1
                    elif outcome.startswith("loss"):
                        cell["losses"] += 1
                    else:
                        cell["timeouts"] += 1
                    if pnl > 0:
                        cell["gp"] += pnl
                    else:
                        cell["gl"] += -pnl
                    cell["mae"] += float(r.mae_ticks)

    rows = []
    for (scenario, ltype, horizon, S, T), c in cells.items():
        n = c["n"]
        rows.append({
            "scenario": scenario,
            "level_type": ltype,
            "horizon": horizon,
            "stop_ticks": S,
            "target_ticks": T,
            "n": n,
            "win_rate": c["wins"] / n,
            "loss_rate": c["losses"] / n,
            "timeout_rate": c["timeouts"] / n,
            "expectancy_ticks": c["sum"] / n,
            "expectancy_usd": c["sum"] / n * settings.tick_value,
            "profit_factor": (c["gp"] / c["gl"]) if c["gl"] > 0 else np.inf,
            "avg_mae_ticks": c["mae"] / n,
        })
    return pd.DataFrame(rows)


# ---------------------------------------------------------------------------
# aggregation tables
# ---------------------------------------------------------------------------

def _followthrough_table(sig: pd.DataFrame, by: list[str]) -> pd.DataFrame:
    rows = []
    for keys, g in sig.groupby(by, dropna=False):
        keys = keys if isinstance(keys, tuple) else (keys,)
        base = dict(zip(by, keys))
        n = len(g)
        for th in MFE_THRESHOLDS:
            rows.append(_stat(base, int(g[f"reached_{th}"].sum()), n, f"reached_{th}"))
        rows.append(_stat(base, int((g["gave_way"] == True).sum()), n, "gave_way"))
        med = g["break_before_12"].dropna()
        if len(med):
            rows.append(_stat(base, int(med.sum()), len(med), "break_before_12"))
        for label, col in (("median_mfe_ticks", "mfe_ticks"),
                           ("median_window_s", "window_s" if "window_s" in g.columns else "mfe_ticks")):
            rows.append({
                **base,
                "metric": label,
                "n": n,
                "rate": float(g[col].median()) if n else np.nan,
                "wilson_lo": np.nan,
                "wilson_hi": np.nan,
                "suppressed_below_min_n": n < 30,
            })
        rows.append(_mae_row(base, n, g["mae_ticks"].to_numpy()))
    return pd.DataFrame(rows)


def _horizon_reach_table(sig: pd.DataFrame) -> pd.DataFrame:
    rows = []
    for hname, _ in HORIZONS:
        for (scenario, ltype), g in sig.groupby(["scenario", "level_type"]):
            base = {"scenario": scenario, "level_type": ltype, "horizon": hname}
            n = len(g)
            for th in MFE_THRESHOLDS:
                k_col = f"reached_{th}" if hname == "spec" else f"reached_{hname}_{th}"
                if k_col not in g.columns:
                    continue
                rows.append(_stat(base, int(g[k_col].sum()), n, f"reached_{th}_by_{hname}"))
            mfe_col = "mfe_ticks" if hname == "spec" else f"mfe_{hname}"
            mae_col = "mae_ticks" if hname == "spec" else f"mae_{hname}"
            for label, col in (("median_mfe", mfe_col), ("median_mae", mae_col)):
                rows.append({
                    **base,
                    "metric": f"{label}_{hname}",
                    "n": n,
                    "rate": float(g[col].median()) if n else np.nan,
                    "wilson_lo": np.nan,
                    "wilson_hi": np.nan,
                    "suppressed_below_min_n": n < 30,
                })
            gw_col = "gave_way" if hname == "spec" else f"gave_way_{hname}"
            if gw_col in g.columns:
                rows.append(_stat(base, int((g[gw_col] == True).sum()), n,
                                  f"gave_way_by_{hname}"))
    return pd.DataFrame(rows)


def _break_table(ep: pd.DataFrame) -> pd.DataFrame:
    rows = []
    for keys, g in ep.groupby(["level_type", "independent"], dropna=False):
        ltype, indep = keys
        base = {"level_type": ltype, "independent": indep}
        n = len(g)
        rows.append(_stat(base, int(g["flipped"].sum()), n, "flip_rate"))
        rows.append(_stat(base, n - int(g["flipped"].sum()), n, "snap_rate"))
        rows.append({
            **base,
            "metric": "duration_s",
            "n": n,
            "rate": float(g["duration_s"].median()) if n else np.nan,
            "wilson_lo": float(g["duration_s"].quantile(0.9)) if n else np.nan,
            "wilson_hi": np.nan,
            "suppressed_below_min_n": n < 30,
        })
        for q, v in _mae_row(base, n, g["depth_ticks"].to_numpy()).items():
            if q.startswith("p"):
                rows.append({
                    **base,
                    "metric": f"depth_{q}",
                    "n": n,
                    "rate": v,
                    "wilson_lo": np.nan,
                    "wilson_hi": np.nan,
                    "suppressed_below_min_n": n < 30,
                })
    return pd.DataFrame(rows)


def _accept_sweep(ep: pd.DataFrame) -> pd.DataFrame:
    rows = []
    ind = ep[ep["independent"]] if "independent" in ep.columns else ep
    if ind.empty:
        return pd.DataFrame(rows)
    for ltype, g in ind.groupby("level_type"):
        durs = g["duration_s"].to_numpy()
        for a in ACCEPT_SWEEP:
            flips = int((durs >= a).sum())
            rows.append(_stat({"level_type": ltype}, flips, len(durs), f"flip_rate_accept_{a}s"))
    return pd.DataFrame(rows)


def walk_forward(
    sig: pd.DataFrame, sessions: pd.DataFrame, store: TickStore, settings: Settings
) -> pd.DataFrame:
    days = sorted(sig["trading_day"].unique())
    cut = int(len(days) * 0.7)
    is_days, oos_days = set(days[:cut]), set(days[cut:])
    sig_is = sig[sig["trading_day"].isin(is_days)]
    sig_oos = sig[sig["trading_day"].isin(oos_days)]
    grid_is = simulate_grid(sig_is, sessions, store, settings)
    grid_oos = simulate_grid(sig_oos, sessions, store, settings)

    rows = []
    for (scenario, ltype, horizon), g in grid_is.groupby(
            ["scenario", "level_type", "horizon"]):
        ok = g[g["n"] >= settings.min_n]
        if ok.empty:
            continue
        best = ok.loc[ok["expectancy_ticks"].idxmax()]
        S, T = int(best["stop_ticks"]), int(best["target_ticks"])
        oos = grid_oos[(grid_oos["scenario"] == scenario)
                       & (grid_oos["level_type"] == ltype)
                       & (grid_oos["horizon"] == horizon)
                       & (grid_oos["stop_ticks"] == S)
                       & (grid_oos["target_ticks"] == T)]
        rows.append({
            "scenario": scenario,
            "level_type": ltype,
            "horizon": horizon,
            "is_days": len(is_days),
            "oos_days": len(oos_days),
            "chosen_stop": S,
            "chosen_target": T,
            "is_expectancy_ticks": float(best["expectancy_ticks"]),
            "is_profit_factor": float(best["profit_factor"]),
            "is_n": int(best["n"]),
            "oos_expectancy_ticks": float(oos["expectancy_ticks"].iloc[0]) if len(oos) else np.nan,
            "oos_profit_factor": float(oos["profit_factor"].iloc[0]) if len(oos) else np.nan,
            "oos_n": int(oos["n"].iloc[0]) if len(oos) else 0,
        })
    return pd.DataFrame(rows)


# ---------------------------------------------------------------------------
# entry point
# ---------------------------------------------------------------------------

def run_analysis(settings: Settings, skip_simulation: bool = False,
                 skip_control: bool = False) -> dict[str, pd.DataFrame]:
    tables_dir = settings.results_dir / "tables"
    tables_dir.mkdir(parents=True, exist_ok=True)

    events = pd.read_parquet(settings.events_path)
    sessions = pd.read_parquet(settings.sessions_path)
    store = TickStore(find_partitions(settings))

    sig, ep = build_signal_frames(events, sessions, store, settings)
    sig.to_parquet(settings.results_dir / "signals.parquet", index=False)
    ep.to_parquet(settings.results_dir / "episodes.parquet", index=False)

    if not skip_control:
        ctrl = random_control(sessions, store, settings)
        sig_all = pd.concat([sig, ctrl], ignore_index=True)
    else:
        ctrl = pd.DataFrame()
        sig_all = sig

    tables: dict[str, pd.DataFrame] = {}

    holds = sig[sig["scenario"] == "hold"]
    flips = sig[sig["scenario"] == "flip"]
    brks = sig[sig["scenario"] == "break"]

    fv = holds.copy()
    fv["fresh_bucket"] = np.where(fv["fresh_visit"] >= 5, "5+", fv["fresh_visit"].astype(str))
    tables["hold_followthrough_by_type"] = _followthrough_table(holds, ["level_type"])
    tables["hold_followthrough_by_heldcount"] = _followthrough_table(
        holds, ["level_type", "held_count_after"])
    tables["hold_followthrough_by_freshvisit"] = _followthrough_table(fv, ["level_type", "fresh_bucket"])
    tables["hold_by_subtype"] = _followthrough_table(holds, ["subtype"])
    tables["naked_vs_prior"] = _followthrough_table(holds, ["is_naked", "is_prior"])
    tables["break_followthrough_by_type"] = _followthrough_table(brks, ["level_type"])
    tables["flip_followthrough_by_type"] = _followthrough_table(flips, ["level_type"])

    tables["break_outcomes_by_type"] = _break_table(ep)
    tables["accept_sweep"] = _accept_sweep(ep)
    tables["horizon_reach"] = _horizon_reach_table(sig_all)

    if not skip_simulation:
        tables["stop_target_grid"] = simulate_grid(sig_all, sessions, store, settings)
        wf = walk_forward(sig, sessions, store, settings)
        tables["walk_forward"] = wf
        control_wf = walk_forward(ctrl, sessions, store, settings) if len(ctrl) else pd.DataFrame()
        if len(control_wf):
            control_wf.to_csv(tables_dir / "walk_forward_CONTROL.csv", index=False)

    for name, df in tables.items():
        df.to_csv(tables_dir / f"{name}.csv", index=False)
    return tables
