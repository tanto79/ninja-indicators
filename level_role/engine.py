"""
Full pipeline: ticks → sessions → profiles → levels → role events.

Speed focus:
  - vectorized sessions
  - numpy arrays in replay
  - RTH-only role loop
  - proximity filter: only test levels near the current/prev price
"""

from __future__ import annotations

from pathlib import Path

import numpy as np
import pandas as pd
from tqdm import tqdm

from .config import Settings
from . import ticks as ticks_mod
from .sessions import build_sessions, ticks_in_session, Session
from .profiles import build_profile, profile_to_levels
from .roles import (
    LevelRoleState, TrackedLevel, touch_band, level_touched, run_machine,
)


def run_pipeline(settings: Settings) -> dict[str, Path]:
    settings.ensure_dirs()

    parts = list(settings.parquet_dir.rglob("ticks.parquet"))
    if not parts:
        print("[engine] converting tick text → parquet ...")
        n = ticks_mod.convert_folder(settings)
        if n == 0:
            raise SystemExit("No tick data found. Put NT8 tick files in data/ticks/")

    print("[engine] loading ticks ...")
    all_ticks = ticks_mod.load_all_ticks(settings)
    if all_ticks.empty:
        raise SystemExit("Parquet ticks are empty.")
    print(f"  {len(all_ticks):,} ticks")

    if all_ticks["ts_utc"].dt.tz is None:
        all_ticks["ts_utc"] = pd.to_datetime(all_ticks["ts_utc"], utc=True)
    else:
        all_ticks["ts_utc"] = all_ticks["ts_utc"].dt.tz_convert("UTC")

    print("[engine] building sessions ...")
    sessions = build_sessions(all_ticks, settings)
    print(f"  {len(sessions)} sessions")
    sess_df = pd.DataFrame([{
        "session_id": s.session_id,
        "start_utc": s.start_utc,
        "end_utc": s.end_utc,
        "session_type": s.session_type,
        "trading_day": s.trading_day,
    } for s in sessions])
    sess_df.to_parquet(settings.sessions_path, index=False)

    print("[engine] building profiles + levels ...")
    level_rows: list[dict] = []
    for sess in tqdm(sessions, desc="profiles"):
        sticks = ticks_in_session(all_ticks, sess)
        if not sticks.empty and sticks["ts_utc"].dt.tz is not None:
            sticks = sticks.copy()
            sticks["ts_utc"] = sticks["ts_utc"].dt.tz_localize(None)
        prof = build_profile(sess, sticks, settings)
        for row in profile_to_levels(prof):
            row["active_from_utc"] = sess.end_utc
            row["naked_terminated_utc"] = pd.NaT
            level_rows.append(row)

    levels_df = pd.DataFrame(level_rows)
    if not levels_df.empty:
        levels_df.to_parquet(settings.levels_path, index=False)
    print(f"  {len(levels_df)} levels")

    print("[engine] role replay (RTH only, proximity-filtered) ...")
    events = _replay_roles_fast(all_ticks, sessions, levels_df, settings)
    events_df = pd.DataFrame(events)
    events_df.to_parquet(settings.events_path, index=False)
    print(f"  {len(events_df)} events")

    if not events_df.empty and not levels_df.empty and "level_id" in events_df.columns:
        touches = events_df[events_df["event_type"] == "touch"].groupby("level_id")["ts_utc"].min()
        levels_df = levels_df.copy()
        levels_df["naked_terminated_utc"] = levels_df["level_id"].map(touches)
        levels_df.to_parquet(settings.levels_path, index=False)

    return {
        "sessions": settings.sessions_path,
        "levels": settings.levels_path,
        "events": settings.events_path,
    }


def _replay_roles_fast(
    all_ticks: pd.DataFrame,
    sessions: list[Session],
    levels_df: pd.DataFrame,
    settings: Settings,
) -> list[dict]:
    if levels_df.empty or not sessions:
        return []

    band = touch_band(settings.touch_tolerance_ticks, settings.tick_size)
    tick_size = settings.tick_size
    accept = settings.accept_seconds
    eps = tick_size * 1e-6
    # Extra margin so trade-through across a gap still catches the level
    prox = band + tick_size * 2

    tracked: list[TrackedLevel] = []
    for _, r in levels_df.iterrows():
        tracked.append(TrackedLevel(
            level_id=str(r["level_id"]),
            level_type=str(r["level_type"]),
            price=float(r["price"]),
            source_session_id=str(r["source_session_id"]),
            active_from=pd.Timestamp(r["active_from_utc"]),
        ))

    view_sessions = (
        [s for s in sessions if s.session_type == "RTH"]
        if settings.role_rth_only else sessions
    )

    states: dict[tuple[str, str], LevelRoleState] = {}
    naked_done: dict[str, pd.Timestamp] = {}
    events: list[dict] = []
    tick_index = 0

    for view in tqdm(view_sessions, desc="replay RTH"):
        sticks = ticks_in_session(all_ticks, view)
        if sticks.empty:
            continue

        prices = sticks["last"].to_numpy(dtype=np.float64)
        tcol = sticks["ts_utc"]
        if tcol.dt.tz is not None:
            times = list(tcol.dt.tz_convert("UTC"))
        else:
            times = list(pd.to_datetime(tcol, utc=True))

        view_start = view.start_utc
        if getattr(view_start, "tzinfo", None) is None:
            view_start = pd.Timestamp(view_start, tz="UTC")
        else:
            view_start = pd.Timestamp(view_start).tz_convert("UTC")

        # Levels active this session
        active: list[TrackedLevel] = []
        for lvl in tracked:
            af = lvl.active_from
            af_cmp = pd.Timestamp(af, tz="UTC") if af.tzinfo is None else af.tz_convert("UTC")
            if af_cmp > view_start:
                continue
            if lvl.source_session_id == view.session_id:
                continue
            active.append(lvl)
            key = (lvl.level_id, view.session_id)
            if key not in states:
                states[key] = LevelRoleState()

        if not active:
            tick_index += len(prices)
            continue

        # Sort by price for fast proximity window
        active.sort(key=lambda x: x.price)
        act_prices = np.array([l.price for l in active], dtype=np.float64)
        n_act = len(active)

        prev_price: float | None = None
        is_rth_view = view.session_type == "RTH"

        for i in range(len(prices)):
            price = float(prices[i])
            ts = times[i]
            tick_index += 1

            # Candidate levels near [min(prev,price), max(prev,price)] ± prox
            lo_p = price if prev_price is None else min(prev_price, price)
            hi_p = price if prev_price is None else max(prev_price, price)
            left = float(lo_p - prox)
            right = float(hi_p + prox)

            # Binary search window into sorted active levels
            j0 = int(np.searchsorted(act_prices, left, side="left"))
            j1 = int(np.searchsorted(act_prices, right, side="right"))

            for j in range(j0, j1):
                lvl = active[j]
                key = (lvl.level_id, view.session_id)
                st = states[key]
                if st.frozen:
                    continue

                if lvl.level_id not in naked_done:
                    can_terminate = not (lvl.level_type == "RTHC" and not is_rth_view)
                    if can_terminate and level_touched(lvl.price, prev_price, price, band, eps):
                        naked_done[lvl.level_id] = ts

                evs = run_machine(
                    st, lvl.price, price
                    if not pd.isna(prev_price) else None, ts,
                    int(ts.value), band, accept, tick_size,
                )
                if evs:
                    is_naked = lvl.level_id not in naked_done or ts <= naked_done[lvl.level_id]
                    for e in evs:
                        events.append({
                            "level_id": lvl.level_id,
                            "level_type": lvl.level_type,
                            "price": lvl.price,
                            "session_id": view.session_id,
                            "session_type": view.session_type,
                            "event_type": e["event_type"],
                            "hold_subtype": e.get("hold_subtype"),
                            "held_count_after": e.get("held_count_after"),
                            "role": st.role,
                            "ts_utc": ts,
                            "tick_index": tick_index,
                            "price_at_event": price,
                            "is_naked": bool(is_naked),
                        })

            prev_price = price

        for lvl in active:
            states[(lvl.level_id, view.session_id)].frozen = True

    return events
