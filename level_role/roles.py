"""
Role state machine — faithful port of indicator RunLevelRoleMachine / RoleBandEntry
(AntonsonProfile.cs), including the Flipped-state branch the earlier port missed.

Time is carried as integer nanoseconds (UTC) instead of Timestamps so the
per-tick replay can avoid object churn; event emission converts on demand.

RTHC special case: naked termination only counts touches during RTH sessions
(overnight prints do not terminate RTHC).
"""

from __future__ import annotations

from dataclasses import dataclass
from enum import Enum, auto

import numpy as np
import pandas as pd


class LRState(Enum):
    Untested = auto()
    Testing = auto()
    Held = auto()
    Breaking = auto()
    Flipped = auto()


@dataclass
class LevelRoleState:
    state: LRState = LRState.Untested
    role: int = 0
    in_band: bool = False
    pending_reject: bool = False
    touches: int = 0
    helds: int = 0
    break_dir: int = 0
    break_start_ns: int = 0
    frozen: bool = False


@dataclass
class TrackedLevel:
    level_id: str
    level_type: str
    price: float
    source_session_id: str
    active_from: pd.Timestamp
    naked_terminated: pd.Timestamp | None = None


def touch_band(tolerance_ticks: int, tick_size: float) -> float:
    ticks = max(0, tolerance_ticks)
    return (0.5 if ticks == 0 else float(ticks)) * tick_size


def level_in_band(level: float, price: float, band: float, eps: float) -> bool:
    return abs(price - level) <= band + eps


def level_crossed(level: float, prev: float, price: float, band: float, eps: float) -> bool:
    if prev is None or (isinstance(prev, float) and np.isnan(prev)):
        return False
    dp = prev - level
    d = price - level
    if abs(dp) <= band + eps or abs(d) <= band + eps:
        return False
    return (dp > 0) != (d > 0)


def level_touched(level: float, prev: float | None, price: float, band: float, eps: float) -> bool:
    if level_in_band(level, price, band, eps):
        return True
    if prev is None or (isinstance(prev, float) and np.isnan(prev)):
        return False
    return level_crossed(level, prev, price, band, eps)


def role_band_entry(st: LevelRoleState) -> None:
    st.break_dir = 0
    if st.state == LRState.Breaking:
        st.state = LRState.Testing
    if st.state == LRState.Untested:
        st.state = LRState.Testing
    if not st.in_band:
        st.in_band = True
        st.pending_reject = True
        st.touches += 1


def _hold_event(subtype: str, helds: int) -> dict:
    return {"event_type": "hold", "hold_subtype": subtype, "held_count_after": helds}


def run_machine(
    st: LevelRoleState,
    level_price: float,
    tick_price: float,
    prev_price: float | None,
    tick_ns: int,
    band: float,
    accept_seconds: int,
    tick_size: float,
) -> list[dict]:
    events: list[dict] = []
    if st.frozen:
        return events

    eps = tick_size * 1e-6
    in_band_now = level_in_band(level_price, tick_price, band, eps)
    touched = level_touched(level_price, prev_price, tick_price, band, eps)

    if touched and not st.in_band:
        role_band_entry(st)
        events.append({"event_type": "touch", "hold_subtype": None})

    if in_band_now:
        st.in_band = True
        return events

    was_in_band = st.in_band
    st.in_band = False
    side = 1 if tick_price > level_price else -1

    if st.state == LRState.Flipped:
        if side != st.role:
            st.helds += 1
            st.role = side
            st.break_dir = 0
            st.pending_reject = False
            st.state = LRState.Held
            events.append(_hold_event("reclaim", st.helds))
        elif st.pending_reject:
            st.helds += 1
            st.pending_reject = False
            st.state = LRState.Held
            events.append(_hold_event("flipped_retest", st.helds))
        return events

    if st.role == 0:
        st.role = side
        st.state = LRState.Held
        st.pending_reject = False
        return events

    if side == st.role:
        if st.state == LRState.Breaking:
            st.helds += 1
            st.break_dir = 0
            st.pending_reject = False
            st.state = LRState.Held
            events.append(_hold_event("failed_break", st.helds))
            events.append({"event_type": "break_end", "hold_subtype": None})
        elif st.pending_reject and was_in_band:
            st.helds += 1
            st.pending_reject = False
            st.state = LRState.Held
            events.append(_hold_event("clean_reject", st.helds))
        else:
            st.state = LRState.Held
    else:
        if st.break_dir != side:
            st.break_dir = side
            st.break_start_ns = tick_ns
            events.append({"event_type": "break_start", "hold_subtype": None})
        elapsed = (tick_ns - st.break_start_ns) / 1e9
        if elapsed >= accept_seconds:
            st.state = LRState.Flipped
            st.role = side
            st.break_dir = 0
            st.pending_reject = False
            events.append({"event_type": "flip", "hold_subtype": None})
        else:
            st.state = LRState.Breaking

    return events
