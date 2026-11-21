"""
Streaming end-to-end study runner: sessions -> profiles -> fixed role replay.

Memory-safe: tick partitions are loaded on demand and sliced per session, so
the run never holds the whole history as a pandas DataFrame. Events carry
sess_tick_index (position within the viewing session) so the analysis can
re-locate windows in the tick arrays without guessing.
"""

from __future__ import annotations

from collections import OrderedDict
from pathlib import Path

import numpy as np
import pandas as pd
from tqdm import tqdm

from .config import Settings
from .profiles import build_profile, profile_to_levels
from .roles import (
    LevelRoleState, TrackedLevel, level_touched, touch_band, run_machine,
)
from .sessions import session_bounds_utc, trading_days_vectorized

NS_DAY = 86_400_000_000_000


def find_partitions(settings: Settings) -> list[Path]:
    root = settings.root
    for pat in (
        (root / "data" / "parquet", "rglob"),
        (root / "data", "glob"),
        (root, "glob"),
    ):
        base, meth = pat
        if meth == "rglob":
            files = sorted(base.rglob("ticks.parquet"))
        else:
            files = sorted(base.glob("date=*/ticks.parquet"))
        if files:
            return files
    return []


def _load_arrays(path: Path) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    df = pd.read_parquet(path, columns=["ts_utc", "last", "volume", "ord"])
    ts = pd.to_datetime(df["ts_utc"]).astype("int64").to_numpy()
    last = df["last"].to_numpy(dtype=np.float64)
    vol = df["volume"].to_numpy(dtype=np.int64)
    ordn = df["ord"].to_numpy()
    order = np.lexsort((ordn, ts))
    return ts[order], last[order], vol[order]


class TickStore:
    """Loads UTC-day partitions on demand with a small LRU cache."""

    def __init__(self, files: list[Path], cache_size: int = 4):
        self.files = files
        self._cache: OrderedDict[Path, tuple] = OrderedDict()
        self._cache_size = cache_size

    def _partition(self, path: Path):
        arr = self._cache.get(path)
        if arr is None:
            arr = _load_arrays(path)
            self._cache[path] = arr
            while len(self._cache) > self._cache_size:
                self._cache.popitem(last=False)
        else:
            self._cache.move_to_end(path)
        return arr

    def _bounds(self, path: Path) -> tuple[int, int]:
        day = path.parent.name.replace("date=", "")
        t0 = int(pd.Timestamp(day).value)
        return t0, t0 + NS_DAY

    def slice_range(self, start_ns: int, end_ns: int):
        parts = []
        for f in self.files:
            p0, p1 = self._bounds(f)
            if p1 < start_ns or p0 > end_ns:
                continue
            ts, last, vol = self._partition(f)
            i0 = int(np.searchsorted(ts, start_ns, side="left"))
            i1 = int(np.searchsorted(ts, end_ns, side="right"))
            if i1 > i0:
                parts.append((ts[i0:i1], last[i0:i1], vol[i0:i1]))
        if not parts:
            return np.empty(0, np.int64), np.empty(0), np.empty(0, np.int64)
        if len(parts) == 1:
            return parts[0]
        ts = np.concatenate([p[0] for p in parts])
        last = np.concatenate([p[1] for p in parts])
        vol = np.concatenate([p[2] for p in parts])
        return ts, last, vol


def build_sessions_streaming(store: TickStore, settings: Settings) -> list[dict]:
    seen: dict[tuple, tuple] = {}
    for f in tqdm(store.files, desc="sessions"):
        ts, _, _ = store._partition(f)
        if len(ts) == 0:
            continue
        day_keys = trading_days_vectorized(pd.Series(pd.to_datetime(ts, utc=True)), settings)
        days_arr = day_keys.to_numpy()
        for day in np.unique(days_arr):
            g0, r0, r1 = session_bounds_utc(str(day), settings)
            g0_ns = int(pd.Timestamp(g0, tz="UTC").value)
            r0_ns = int(pd.Timestamp(r0, tz="UTC").value)
            r1_ns = int(pd.Timestamp(r1, tz="UTC").value)
            mask_g = (ts >= g0_ns) & (ts < r0_ns)
            mask_r = (ts >= r0_ns) & (ts <= r1_ns)
            if mask_g.any():
                seen[(str(day), "Globex")] = (g0_ns, r0_ns - 1)
            if mask_r.any():
                seen[(str(day), "RTH")] = (r0_ns, r1_ns)
    out = []
    for (day, stype), (s, e) in sorted(seen.items(), key=lambda kv: kv[1][0]):
        out.append({
            "session_id": f"{day}|{stype}",
            "start_utc": pd.Timestamp(s, tz="UTC"),
            "end_utc": pd.Timestamp(e, tz="UTC"),
            "session_type": stype,
            "trading_day": day,
        })
    return out


def build_levels(store: TickStore, sessions: list[dict], settings: Settings) -> pd.DataFrame:
    rows: list[dict] = []
    for sess in tqdm(sessions, desc="profiles"):
        ts, last, vol = store.slice_range(
            int(sess["start_utc"].value), int(sess["end_utc"].value)
        )
        if len(ts) == 0:
            continue
        df = pd.DataFrame({
            "ts_utc": pd.to_datetime(ts, utc=True),
            "last": last,
            "volume": vol,
        })
        prof = build_profile(_SimpleSession(sess), df, settings)
        for row in profile_to_levels(prof):
            row["active_from_utc"] = sess["end_utc"]
            row["naked_terminated_utc"] = pd.NaT
            rows.append(row)
    return pd.DataFrame(rows)


class _SimpleSession:
    def __init__(self, d: dict):
        self.session_id = d["session_id"]
        self.start_utc = d["start_utc"]
        self.end_utc = d["end_utc"]
        self.session_type = d["session_type"]
        self.trading_day = d["trading_day"]

    @property
    def StartTime(self):
        return self.start_utc

    @property
    def SessionDate(self):
        return self.start_utc


def replay_roles(
    store: TickStore,
    sessions: list[dict],
    levels_df: pd.DataFrame,
    settings: Settings,
) -> pd.DataFrame:
    band = touch_band(settings.touch_tolerance_ticks, settings.tick_size)
    accept = settings.accept_seconds
    if settings.flip_confirm_mode == "seconds":
        confirm = None
        accept = settings.accept_seconds
    else:
        confirm = (settings.flip_confirm_mode, settings.flip_confirm_value)
    prox = band + settings.tick_size * 2

    tracked = [
        TrackedLevel(
            level_id=str(r.level_id),
            level_type=str(r.level_type),
            price=float(r.price),
            source_session_id=str(r.source_session_id),
            active_from=pd.Timestamp(r.active_from_utc),
        )
        for r in levels_df.itertuples(index=False)
    ]

    views = [s for s in sessions if s["session_type"] == "RTH"]
    states: dict[tuple[str, str], LevelRoleState] = {}
    naked_done: dict[str, int] = {}
    events: list[dict] = []
    tick_counter = 0

    for vi, view in enumerate(tqdm(views, desc="replay RTH")):
        v_start_ns = int(view["start_utc"].value)
        v_end_ns = int(view["end_utc"].value)
        ts, last, vol = store.slice_range(v_start_ns, v_end_ns)
        n = len(ts)
        if n == 0:
            continue

        active = [
            lvl for lvl in tracked
            if int(lvl.active_from.value) <= v_start_ns
            and lvl.source_session_id != view["session_id"]
        ]
        if not active:
            continue
        for lvl in active:
            states[(lvl.level_id, view["session_id"])] = LevelRoleState()
        active.sort(key=lambda x: x.price)
        act_prices = np.array([l.price for l in active], dtype=np.float64)

        # immediately preceding session = "prior"; older sources are naked candidates
        prior_sid = sessions[vi - 1]["session_id"] if vi > 0 else ""

        prev_price = np.nan
        prices_list = last.tolist()
        ts_list = ts.tolist()
        vol_list = vol.tolist()

        for i in range(n):
            price = prices_list[i]
            ts_ns = ts_list[i]

            lo_p = price if prev_price != prev_price else (price if price < prev_price else prev_price)
            hi_p = price if prev_price != prev_price else (price if price > prev_price else prev_price)
            j0 = int(np.searchsorted(act_prices, lo_p - prox, side="left"))
            j1 = int(np.searchsorted(act_prices, hi_p + prox, side="right"))

            for j in range(j0, j1):
                lvl = active[j]
                key = (lvl.level_id, view["session_id"])
                st = states[key]
                if st.frozen:
                    continue

                if lvl.level_id not in naked_done and level_touched(
                    lvl.price, None if prev_price != prev_price else prev_price, price, band, settings.tick_size * 1e-6
                ):
                    naked_done[lvl.level_id] = ts_ns

                evs = run_machine(
                    st, lvl.price, price,
                    None if prev_price != prev_price else prev_price,
                    ts_ns, band, accept, settings.tick_size,
                    tick_volume=int(vol_list[i]), confirm=confirm,
                )
                if evs:
                    nd = naked_done.get(lvl.level_id)
                    is_naked = nd is None or ts_ns <= nd
                    for e in evs:
                        events.append({
                            "level_id": lvl.level_id,
                            "level_type": lvl.level_type,
                            "price": lvl.price,
                            "session_id": view["session_id"],
                            "session_type": view["session_type"],
                            "event_type": e["event_type"],
                            "hold_subtype": e.get("hold_subtype"),
                            "held_count_after": e.get("held_count_after"),
                            "role": st.role,
                            "ts_utc": pd.Timestamp(ts_ns, tz="UTC"),
                            "tick_index": tick_counter + i,
                            "sess_tick_index": i,
                            "price_at_event": price,
                            "is_naked": bool(is_naked),
                            "is_prior": lvl.source_session_id == prior_sid,
                        })

            prev_price = price

        for lvl in active:
            states[(lvl.level_id, view["session_id"])].frozen = True
        tick_counter += n

    df = pd.DataFrame(events)
    return df


def run_study(settings: Settings) -> dict[str, Path]:
    settings.ensure_dirs()
    files = find_partitions(settings)
    if not files:
        raise SystemExit("No tick parquet partitions found (date=*/ticks.parquet).")
    store = TickStore(files)
    print(f"[study] {len(files)} tick partitions")

    sessions = build_sessions_streaming(store, settings)
    print(f"[study] {len(sessions)} sessions")
    pd.DataFrame([{
        "session_id": s["session_id"],
        "start_utc": s["start_utc"],
        "end_utc": s["end_utc"],
        "session_type": s["session_type"],
        "trading_day": s["trading_day"],
    } for s in sessions]).to_parquet(settings.sessions_path, index=False)

    levels = build_levels(store, sessions, settings)
    levels.to_parquet(settings.levels_path, index=False)
    print(f"[study] {len(levels)} levels")

    events = replay_roles(store, sessions, levels, settings)
    events.to_parquet(settings.events_path, index=False)
    print(f"[study] {len(events)} events -> {settings.events_path}")

    touches = events[events["event_type"] == "touch"].groupby("level_id")["ts_utc"].min()
    levels = levels.copy()
    levels["naked_terminated_utc"] = levels["level_id"].map(touches)
    levels.to_parquet(settings.levels_path, index=False)

    return {
        "sessions": settings.sessions_path,
        "levels": settings.levels_path,
        "events": settings.events_path,
    }
