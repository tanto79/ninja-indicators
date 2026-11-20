"""
ES session boundaries from tick timestamps (UTC) — vectorized.

Trading day (CME equity index futures):
  Opens 18:00 America/New_York prior calendar evening
  RTH     09:30–16:00 ET
  Ends    17:00 ET
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime

import pandas as pd
import pytz

from .config import Settings

UTC = pytz.UTC


def chart_zone(settings) -> pytz.BaseTzInfo:
    return pytz.timezone(getattr(settings, "timezone", "America/Chicago"))


@dataclass
class Session:
    session_id: str
    start_utc: pd.Timestamp
    end_utc: pd.Timestamp
    session_type: str
    trading_day: str


def _parse_hhmm(s: str) -> tuple[int, int]:
    h, m = s.split(":")
    return int(h), int(m)


def _ensure_utc_series(ts: pd.Series) -> pd.Series:
    if ts.dt.tz is None:
        return pd.to_datetime(ts, utc=True)
    return ts.dt.tz_convert("UTC")


def trading_days_vectorized(ts_utc: pd.Series, settings: Settings) -> pd.Series:
    """Map each UTC timestamp to trading-day date string (YYYY-MM-DD). Vectorized."""
    ts = _ensure_utc_series(ts_utc)
    et = ts.dt.tz_convert(chart_zone(settings))
    gh, gm = _parse_hhmm(settings.globex_open)
    end_h, end_m = _parse_hhmm(settings.session_day_end)

    minutes = et.dt.hour * 60 + et.dt.minute
    globex_m = gh * 60 + gm
    end_m_total = end_h * 60 + end_m

    cal_date = et.dt.normalize()
    after_globex = minutes >= globex_m

    trading = cal_date.where(~after_globex, cal_date + pd.Timedelta(days=1))
    return trading.dt.strftime("%Y-%m-%d")


def session_bounds_utc(trading_day_str: str, settings: Settings):
    """Return naive-UTC (globex_start, rth_start, rth_end) for a trading day YYYY-MM-DD."""
    d = datetime.strptime(trading_day_str, "%Y-%m-%d")
    prev = d - pd.Timedelta(days=1)
    prev = datetime(prev.year, prev.month, prev.day)

    gh, gm = _parse_hhmm(settings.globex_open)
    rh, rm = _parse_hhmm(settings.rth_start)
    reh, rem = _parse_hhmm(settings.rth_end)

    zone = chart_zone(settings)
    g_et = zone.localize(datetime(prev.year, prev.month, prev.day, gh, gm, 0))
    r_et = zone.localize(datetime(d.year, d.month, d.day, rh, rm, 0))
    re_et = zone.localize(datetime(d.year, d.month, d.day, reh, rem, 0))

    def to_naive_utc(dt):
        return dt.astimezone(UTC).replace(tzinfo=None)

    return to_naive_utc(g_et), to_naive_utc(r_et), to_naive_utc(re_et)


def build_sessions(ticks: pd.DataFrame, settings: Settings) -> list[Session]:
    if ticks.empty:
        return []

    ts = _ensure_utc_series(ticks["ts_utc"])
    days = trading_days_vectorized(ts, settings)
    unique_days = sorted(days.dropna().unique())

    sessions: list[Session] = []

    for day_str in unique_days:
        g_start_u, r_start_u, r_end_u = session_bounds_utc(day_str, settings)
        g_start_ts = pd.Timestamp(g_start_u, tz="UTC")
        r_start_ts = pd.Timestamp(r_start_u, tz="UTC")
        r_end_ts = pd.Timestamp(r_end_u, tz="UTC")

        g_mask = (ts >= g_start_ts) & (ts < r_start_ts)
        if bool(g_mask.to_numpy().any()):
            sessions.append(Session(
                session_id=f"{day_str}|Globex",
                start_utc=pd.Timestamp(g_start_u),
                end_utc=pd.Timestamp(r_start_u) - pd.Timedelta(microseconds=1),
                session_type="Globex",
                trading_day=day_str,
            ))

        r_mask = (ts >= r_start_ts) & (ts <= r_end_ts)
        if bool(r_mask.to_numpy().any()):
            sessions.append(Session(
                session_id=f"{day_str}|RTH",
                start_utc=pd.Timestamp(r_start_u),
                end_utc=pd.Timestamp(r_end_u),
                session_type="RTH",
                trading_day=day_str,
            ))

    sessions.sort(key=lambda s: s.start_utc)
    return sessions


def ticks_in_session(ticks: pd.DataFrame, session: Session) -> pd.DataFrame:
    ts = ticks["ts_utc"]
    start = session.start_utc
    end = session.end_utc

    if ts.dt.tz is not None:
        if getattr(start, "tzinfo", None) is None:
            start = pd.Timestamp(start, tz="UTC")
        else:
            start = pd.Timestamp(start).tz_convert("UTC")
        if getattr(end, "tzinfo", None) is None:
            end = pd.Timestamp(end, tz="UTC")
        else:
            end = pd.Timestamp(end).tz_convert("UTC")
    else:
        if getattr(start, "tzinfo", None) is not None:
            start = pd.Timestamp(start).tz_convert("UTC").tz_localize(None)
        if getattr(end, "tzinfo", None) is not None:
            end = pd.Timestamp(end).tz_convert("UTC").tz_localize(None)

    return ticks[(ts >= start) & (ts <= end)]
