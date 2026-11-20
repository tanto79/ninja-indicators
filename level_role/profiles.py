"""
Build volume + TPO profiles for one session from its ticks (vectorized where possible).
"""

from __future__ import annotations

from dataclasses import dataclass, field
from collections import defaultdict

import numpy as np
import pandas as pd

from .config import Settings
from .sessions import Session


@dataclass
class SessionProfile:
    session: Session
    volume_at_price: dict[int, int] = field(default_factory=dict)
    tpo_at_price: dict[int, set] = field(default_factory=dict)
    session_high: float = float("-inf")
    session_low: float = float("inf")
    open_price: float = 0.0
    close_price: float = 0.0
    rth_close: float = 0.0
    total_volume: int = 0
    vpoc: float = 0.0
    vvah: float = 0.0
    vval: float = 0.0
    tpopoc: float = 0.0
    tpovah: float = 0.0
    tpoval: float = 0.0


def price_to_level(price: float, tick_size: float) -> int:
    return int(round(price / tick_size))


def level_center(level: int, tick_size: float) -> float:
    return level * tick_size


def level_top(level: int, tick_size: float) -> float:
    return (level + 1) * tick_size


def level_bottom(level: int, tick_size: float) -> float:
    return level * tick_size


def _value_area(levels_sorted: list[int], counts: dict[int, int], total: int, pct: float) -> tuple[int, int]:
    if not levels_sorted or total <= 0:
        return 0, 0
    poc = max(levels_sorted, key=lambda lv: counts.get(lv, 0))
    target = total * pct
    cum = counts.get(poc, 0)
    lo = hi = poc
    while cum < target:
        above = hi + 1
        below = lo - 1
        vol_above = counts.get(above, 0)
        vol_below = counts.get(below, 0)
        if vol_above == 0 and vol_below == 0:
            higher = [l for l in levels_sorted if l > hi]
            lower = [l for l in levels_sorted if l < lo]
            if not higher and not lower:
                break
            if not higher:
                lo = max(lower)
                cum += counts[lo]
                continue
            if not lower:
                hi = min(higher)
                cum += counts[hi]
                continue
            next_hi = min(higher)
            next_lo = max(lower)
            if counts[next_hi] >= counts[next_lo]:
                hi = next_hi
                cum += counts[hi]
            else:
                lo = next_lo
                cum += counts[lo]
        elif vol_above >= vol_below:
            hi = above
            cum += vol_above
        else:
            lo = below
            cum += vol_below
    return hi, lo


def build_profile(session: Session, ticks: pd.DataFrame, settings: Settings) -> SessionProfile:
    prof = SessionProfile(session=session)
    if ticks.empty:
        return prof

    ts = settings.tick_size
    prices = ticks["last"].to_numpy(dtype=np.float64)
    volumes = ticks["volume"].to_numpy(dtype=np.int64) if "volume" in ticks.columns else np.ones(len(ticks), dtype=np.int64)
    volumes = np.maximum(volumes, 1)

    levels = np.rint(prices / ts).astype(np.int64)

    # Volume map via bincount-style groupby
    uniq, inv = np.unique(levels, return_inverse=True)
    vol_sums = np.bincount(inv, weights=volumes).astype(np.int64)
    vol_map = {int(lv): int(v) for lv, v in zip(uniq, vol_sums)}
    prof.volume_at_price = vol_map
    prof.total_volume = int(volumes.sum())
    prof.session_high = float(prices.max())
    prof.session_low = float(prices.min())
    prof.open_price = float(prices[0])
    prof.close_price = float(prices[-1])
    if session.session_type == "RTH":
        prof.rth_close = prof.close_price

    # TPO: period index from timestamps
    tcol = ticks["ts_utc"]
    if tcol.dt.tz is not None:
        t_ns = tcol.dt.tz_convert("UTC").astype("int64").to_numpy()
    else:
        t_ns = pd.to_datetime(tcol).astype("int64").to_numpy()
    start = session.start_utc
    if getattr(start, "tzinfo", None) is not None:
        start = pd.Timestamp(start).tz_convert("UTC").tz_localize(None)
    start_ns = pd.Timestamp(start).value
    period_ns = settings.tpo_period_minutes * 60 * 1_000_000_000
    period_idx = np.maximum(0, ((t_ns - start_ns) // period_ns).astype(np.int64))

    tpo_map: dict[int, set] = defaultdict(set)
    # Limit unique periods for lettering
    for lv, pidx in zip(levels.tolist(), period_idx.tolist()):
        tpo_map[int(lv)].add(int(pidx))
    prof.tpo_at_price = {k: set(v) for k, v in tpo_map.items()}

    # Volume POC + VA
    if vol_map:
        lvls = sorted(vol_map.keys())
        poc_lvl = max(lvls, key=lambda lv: vol_map[lv])
        prof.vpoc = level_center(poc_lvl, ts)
        vah_lvl, val_lvl = _value_area(lvls, vol_map, prof.total_volume, settings.va_pct)
        prof.vvah = level_top(vah_lvl, ts)
        prof.vval = level_bottom(val_lvl, ts)

    # TPO POC + VA
    if tpo_map:
        tpo_counts = {lv: len(chars) for lv, chars in tpo_map.items()}
        lvls = sorted(tpo_counts.keys())
        total_tpo = sum(tpo_counts.values())
        poc_lvl = max(lvls, key=lambda lv: tpo_counts[lv])
        prof.tpopoc = level_center(poc_lvl, ts)
        vah_lvl, val_lvl = _value_area(lvls, tpo_counts, total_tpo, settings.va_pct)
        prof.tpovah = level_top(vah_lvl, ts)
        prof.tpoval = level_bottom(val_lvl, ts)

    return prof


def profile_to_levels(prof: SessionProfile) -> list[dict]:
    s = prof.session
    rows = []

    def add(ltype: str, price: float):
        if price and price != float("-inf") and price != float("inf") and price > 0:
            rows.append({
                "level_id": f"{s.session_id}|{ltype}",
                "level_type": ltype,
                "price": float(price),
                "source_session_id": s.session_id,
                "source_session_start_utc": s.start_utc,
                "source_session_end_utc": s.end_utc,
                "source_session_type": s.session_type,
                "trading_day": s.trading_day,
            })

    add("VPOC", prof.vpoc)
    add("VVAH", prof.vvah)
    add("VVAL", prof.vval)
    add("TPOPOC", prof.tpopoc)
    add("TPOVAH", prof.tpovah)
    add("TPOVAL", prof.tpoval)
    add("HI", prof.session_high)
    add("LO", prof.session_low)
    if s.session_type == "RTH" and prof.rth_close > 0:
        add("RTHC", prof.rth_close)
    return rows
