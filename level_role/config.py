"""Paths and study parameters."""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path


# Levels the study tracks (includes RTHC per user request)
LEVEL_TYPES = (
    "VPOC", "VVAH", "VVAL",
    "TPOPOC", "TPOVAH", "TPOVAL",
    "HI", "LO", "RTHC",
)


@dataclass
class Settings:
    root: Path

    # Locked study settings
    tick_size: float = 0.25
    tick_value: float = 12.50
    ticks_per_row: int = 1
    touch_tolerance_ticks: int = 0
    accept_seconds: int = 180
    va_pct: float = 0.70          # volume / TPO value area
    tpo_period_minutes: int = 30  # classic ES TPO period

    # Role classification only during RTH (Globex profiles still built so
    # overnight levels exist to be tested in the cash session).
    role_rth_only: bool = True

    # ES session times (America/New_York)
    # Globex opens 18:00 prior calendar day; RTH 09:30–16:00; session day ends 17:00
    rth_start: str = "09:30"
    rth_end: str = "16:00"
    globex_open: str = "18:00"
    session_day_end: str = "17:00"  # futures "trading day" boundary

    min_separation_ticks: int = 8
    confluence_ticks: int = 4
    min_n: int = 30

    stop_ticks: list[int] = field(default_factory=lambda: list(range(2, 41, 2)))
    target_ticks: list[int] = field(default_factory=lambda: [4, 8, 12, 16, 20, 24, 30, 40, 60])
    mfe_thresholds: list[int] = field(default_factory=lambda: [4, 8, 12, 16, 20, 24, 30, 40, 60])

    slippage_ticks: float = 0.0
    commission_per_side: float = 0.0

    def __post_init__(self) -> None:
        self.root = Path(self.root).resolve()

    @property
    def row_size(self) -> float:
        return self.tick_size * max(1, self.ticks_per_row)

    @property
    def data_dir(self) -> Path:
        return self.root / "data"

    @property
    def ticks_dir(self) -> Path:
        return self.data_dir / "ticks"

    @property
    def parquet_dir(self) -> Path:
        return self.data_dir / "parquet"

    @property
    def results_dir(self) -> Path:
        return self.root / "results"

    @property
    def events_path(self) -> Path:
        return self.results_dir / "events.parquet"

    @property
    def levels_path(self) -> Path:
        return self.results_dir / "levels.parquet"

    @property
    def sessions_path(self) -> Path:
        return self.results_dir / "sessions.parquet"

    def ensure_dirs(self) -> None:
        self.ticks_dir.mkdir(parents=True, exist_ok=True)
        self.parquet_dir.mkdir(parents=True, exist_ok=True)
        self.results_dir.mkdir(parents=True, exist_ok=True)
