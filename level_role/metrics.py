"""Summary metrics from events."""

from __future__ import annotations

import pandas as pd

from .config import Settings


def compute(settings: Settings) -> dict[str, pd.DataFrame]:
    settings.ensure_dirs()
    path = settings.events_path
    if not path.exists():
        print("  No events.parquet — pipeline did not produce events.")
        return {}

    df = pd.read_parquet(path)
    if df.empty:
        print("  events.parquet is empty.")
        return {}

    tables: dict[str, pd.DataFrame] = {}
    holds = df[df["event_type"] == "hold"].copy()
    tables["holds_raw"] = holds

    if not holds.empty:
        counts = (
            holds.groupby(["level_type", "hold_subtype", "is_naked", "session_type"], dropna=False)
            .size()
            .reset_index(name="n")
            .sort_values("n", ascending=False)
        )
        tables["hold_counts"] = counts

        if "held_count_after" in holds.columns:
            holds["held_bucket"] = holds["held_count_after"].apply(
                lambda x: "x4+" if pd.notna(x) and x >= 4 else (f"x{int(x)}" if pd.notna(x) else "?")
            )
            tables["hold_buckets"] = (
                holds.groupby(["level_type", "held_bucket"], dropna=False)
                .size()
                .reset_index(name="n")
            )

    breaks = df[df["event_type"].isin(["break_start", "flip", "break_end"])]
    tables["breaks_raw"] = breaks
    if not breaks.empty:
        tables["break_counts"] = (
            breaks.groupby(["level_type", "event_type"], dropna=False)
            .size()
            .reset_index(name="n")
        )

    tables["summary"] = pd.DataFrame([
        {"metric": "total_events", "value": len(df)},
        {"metric": "hold_events", "value": len(holds)},
        {"metric": "break_starts", "value": int((df["event_type"] == "break_start").sum())},
        {"metric": "flips", "value": int((df["event_type"] == "flip").sum())},
        {"metric": "levels_seen", "value": int(df["level_id"].nunique())},
        {"metric": "accept_seconds", "value": settings.accept_seconds},
        {"metric": "touch_tolerance_ticks", "value": settings.touch_tolerance_ticks},
        {"metric": "ticks_per_row", "value": settings.ticks_per_row},
    ])
    return tables
