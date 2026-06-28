#!/usr/bin/env python3
"""Konvertierungs-Job fuer Bitratechanger-Hybrid."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

BRIDGE_ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(BRIDGE_ROOT))
sys.path.insert(0, str(BRIDGE_ROOT / "vendor" / "bitrate"))

from bridge_io import emit, emit_progress_end, emit_progress_fraction  # noqa: E402
from mass_bitrate_core import convert_rows  # noqa: E402


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--config-json", required=True)
    parser.add_argument("--rows-json", required=True)
    args = parser.parse_args()

    config = json.loads(Path(args.config_json).read_text(encoding="utf-8"))
    payload = json.loads(Path(args.rows_json).read_text(encoding="utf-8"))
    rows = payload.get("rows") or payload
    if not isinstance(rows, list):
        emit("FEHLER: rows ungueltig")
        return 1

    done = convert_rows(
        config,
        rows,
        emit,
        progress_cb=lambda cur, total: emit_progress_fraction(cur, total, "Konvertierung"),
    )
    emit_progress_end()
    emit(f"Konvertierung abgeschlossen: {done} Dateien")
    out_dir = config.get("output_folder", "")
    if out_dir:
        emit(f"OUTPUT:{out_dir}")
    return 0 if done >= 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
