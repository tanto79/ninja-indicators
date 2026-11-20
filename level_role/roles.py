"""
Role state machine — port of indicator RunLevelRoleMachine / RoleBandEntry.

RTHC special case: naked termination only counts touches during RTH sessions
(overnight prints do not terminate RTHC).
"""

from __future__ import annotations

from dataclasses import dataclass, field
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
    break_start: pd.Timestamp | None = None
    frozen: bool = False


@dataclass
class TrackedLevel:
    level_id: str
    level_type: str
    price: float
    source_session_id: str
    active_from: pd.Timestamp
    naked_terminated: pd.Timestamp | None = None
    # live state is stored outside, keyed by (level_id, viewing_session_id)


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
    if not st.in_band:
        st.in_band = True
        st.pending_reject = True
        st.touches += 1


def run_machine(
    st: LevelRoleState,
    level_price: float,
    tick_price: float,
    prev_price: float | None,
    tick_time: pd.Timestamp,
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

    if abs(tick_price - level_price) <= band + eps:
        side = 0
    else:
        side = 1 if tick_price > level_price else -1

    if touched and not st.in_band:
        role_band_entry(st)
        events.append({"event_type": "touch", "hold_subtype": None})

    if in_band_now:
        st.in_band = True
        return events

    was_in_band = st.in_band
    st.in_band = False

    if st.role == 0:
        if side != 0:
            st.role = side
            st.state = LRState.Untested
            st.pending_reject = False
        return events

    if side == 0:
        return events

    if side == st.role:
        if st.state == LRState.Breaking:
            st.helds += 1
            st.break_dir = 0
            st.pending_reject = False
            st.state = LRState.Held
            events.append({
                "event_type": "hold",
                "hold_subtype": "failed_break",
                "held_count_after": st.helds,
            })
            events.append({"event_type": "break_end", "hold_subtype": None})
        elif st.pending_reject and was_in_band:
            st.helds += 1
            st.pending_reject = False
            st.state = LRState.Held
            events.append({
                "event_type": "hold",
                "hold_subtype": "clean_reject",
                "held_count_after": st.helds,
            })
        else:
            st.state = LRState.Held
    else:
        if st.break_dir != side:
            st.break_dir = side
            st.break_start = tick_time
            events.append({"event_type": "break_start", "hold_subtype": None})
        if st.break_start is not None:
            elapsed = (tick_time - st.break_start).total_seconds()
            if elapsed >= accept_seconds:
                st.state = LRState.Flipped
                st.role = side
                st.break_dir = 0
                st.pending_reject = False
                events.append({"event_type": "flip", "hold_subtype": None})
            else:
                st.state = LRState.Breaking

    return events
