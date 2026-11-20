"""Write CSV tables + FINDINGS.txt into results/."""

from __future__ import annotations

from datetime import datetime, timezone

import pandas as pd

from .config import Settings


def write(tables: dict[str, pd.DataFrame], settings: Settings) -> None:
    settings.ensure_dirs()
    out = settings.results_dir

    if not tables:
        (out / "FINDINGS.txt").write_text(
            "No tables produced. Check tick data and re-run.\n", encoding="utf-8"
        )
        return

    for name, df in tables.items():
        path = out / f"{name}.csv"
        df.to_csv(path, index=False)
        print(f"  wrote {path.name} ({len(df)} rows)")

    lines = [
        "Level Role Study — findings",
        f"Generated UTC: {datetime.now(timezone.utc).strftime('%Y-%m-%d %H:%M:%S')}",
        f"accept_seconds={settings.accept_seconds}  tolerance={settings.touch_tolerance_ticks}  tpr={settings.ticks_per_row}",
        f"role_rth_only={settings.role_rth_only}",
        f"levels: VPOC VVAH VVAL TPOPOC TPOVAH TPOVAL HI LO RTHC",
        "",
    ]
    if "summary" in tables:
        lines.append("Summary:")
        for _, r in tables["summary"].iterrows():
            lines.append(f"  {r['metric']}: {r['value']}")
        lines.append("")
    if "hold_counts" in tables and not tables["hold_counts"].empty:
        lines.append("Hold counts:")
        lines.append(tables["hold_counts"].head(30).to_string(index=False))
        lines.append("")

    (out / "FINDINGS.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print("  wrote FINDINGS.txt")
