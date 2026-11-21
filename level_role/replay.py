"""
Part C — Replay engine.

Faithful port of the indicator role state machine (spec §6):
  RunLevelRoleMachine, RoleBandEntry, LevelInBand, LevelCrossed.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum, auto
from pathlib import Path
from typing import Iterator

import numpy as np
import pandas as pd
from tqdm import tqdm

from .config import Settings


# ---------------------------------------------------------------------------
# State machine (imported from roles — single source of truth)
# ---------------------------------------------------------------------------

from .roles import run_machine as run_level_role_machine

# ---------------------------------------------------------------------------
# Load registry + ticks, run replay
# ---------------------------------------------------------------------------

def load_registry(settings: Settings) -> tuple[list[Level], list[Session]]:
    levels_path = settings.levels_csv
    sessions_path = settings.sessions_csv
    if not levels_path.exists() or not sessions_path.exists():
        raise FileNotFoundError(
            f"Missing registry files.\n"
            f"  Expected: {levels_path}\n"
            f"            {sessions_path}\n"
            f"Put levels.csv and sessions.csv in data/registry/ then re-run."
        )

    ldf = pd.read_csv(levels_path)
    sdf = pd.read_csv(sessions_path)

    # Flexible column names
    def col(df: pd.DataFrame, *names: str) -> str:
        lower = {c.lower(): c for c in df.columns}
        for n in names:
            if n.lower() in lower:
                return lower[n.lower()]
        raise KeyError(f"None of {names} found in columns {list(df.columns)}")

    levels: list[Level] = []
    for _, r in ldf.iterrows():
        nt = r.get(col(ldf, "naked_terminated_utc", "naked_terminated"), None)
        if pd.isna(nt) or nt == "" or nt is None:
            naked = None
        else:
            naked = pd.Timestamp(nt)

        levels.append(Level(
            level_id=str(r[col(ldf, "level_id")]),
            level_type=str(r[col(ldf, "level_type")]),
            price=float(r[col(ldf, "price")]),
            source_session_start=pd.Timestamp(r[col(ldf, "source_session_start_utc", "source_session_start")]),
            source_session_end=pd.Timestamp(r[col(ldf, "source_session_end_utc", "source_session_end")]),
            active_from=pd.Timestamp(r[col(ldf, "active_from_utc", "active_from")]),
            naked_terminated=naked,
        ))

    sessions: list[Session] = []
    for _, r in sdf.iterrows():
        sessions.append(Session(
            session_id=str(r[col(sdf, "session_id")]),
            start=pd.Timestamp(r[col(sdf, "start_utc", "start")]),
            end=pd.Timestamp(r[col(sdf, "end_utc", "end")]),
            session_type=str(r[col(sdf, "session_type")]) if "session_type" in [c.lower() for c in sdf.columns] or "session_type" in sdf.columns else "",
        ))
    sessions.sort(key=lambda s: s.start)
    return levels, sessions


def iter_tick_days(parquet_dir: Path) -> Iterator[tuple[str, pd.DataFrame]]:
    if not parquet_dir.exists():
        return
    parts = sorted(parquet_dir.rglob("ticks.parquet"))
    for p in parts:
        df = pd.read_parquet(p)
        df = df.sort_values(["ts_utc", "ord"] if "ord" in df.columns else ["ts_utc"])
        day = p.parent.name.replace("date=", "")
        yield day, df


def run(settings: Settings) -> Path:
    settings.ensure_dirs()
    levels, sessions = load_registry(settings)

    band = touch_band(settings.touch_tolerance_ticks, settings.tick_size)
    eps = settings.tick_size * 1e-6
    accept = settings.accept_seconds

    all_events: list[dict] = []
    tick_index = 0
    prev_price: float | None = None
    current_session_idx = -1

    # Fresh state per (level, viewing session)
    # Key: (level_id, session_id)
    states: dict[tuple[str, str], LevelRoleState] = {}

    day_iter = list(iter_tick_days(settings.parquet_dir))
    if not day_iter:
        print("  No parquet ticks found. Run convert first, or put tick text in data/ticks/.")
        # Still write an empty events file so analyze doesn't crash
        out = settings.events_path
        pd.DataFrame().to_parquet(out, index=False)
        return out

    for day, ticks in tqdm(day_iter, desc="replay days"):
        for row in ticks.itertuples(index=False):
            ts = row.ts_utc
            price = float(row.last)
            tick_index += 1

            # Session boundary?
            new_idx = current_session_idx
            for i, sess in enumerate(sessions):
                if sess.start <= ts <= sess.end:
                    new_idx = i
                    break
            if new_idx != current_session_idx:
                # Freeze old session states; reset prev for trade-through
                if current_session_idx >= 0:
                    old = sessions[current_session_idx]
                    for (lid, sid), st in states.items():
                        if sid == old.session_id:
                            st.frozen = True
                prev_price = None  # no trade-through across session gap
                current_session_idx = new_idx

            if current_session_idx < 0:
                prev_price = price
                continue

            sess = sessions[current_session_idx]

            # Active levels this session: active_from <= ts, and source ended before this session
            for lvl in levels:
                if ts < lvl.active_from:
                    continue
                key = (lvl.level_id, sess.session_id)
                if key not in states:
                    states[key] = LevelRoleState()
                st = states[key]
                if st.frozen:
                    continue

                evs = run_level_role_machine(
                    st, lvl.price, price, prev_price, ts,
                    band, accept, settings.tick_size,
                )
                for e in evs:
                    is_naked = (
                        lvl.naked_terminated is None
                        or ts < lvl.naked_terminated
                    )
                    all_events.append({
                        "level_id": lvl.level_id,
                        "level_type": lvl.level_type,
                        "price": lvl.price,
                        "session_id": sess.session_id,
                        "event_type": e["event_type"],
                        "hold_subtype": e.get("hold_subtype"),
                        "held_count_after": e.get("held_count_after"),
                        "role": st.role,
                        "ts_utc": ts,
                        "tick_index": tick_index,
                        "price_at_event": price,
                        "is_naked": is_naked,
                    })

            prev_price = price

    out = settings.events_path
    df = pd.DataFrame(all_events)
    df.to_parquet(out, index=False)
    print(f"  {len(df)} events")
    return out
