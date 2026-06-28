#!/usr/bin/env python3
"""Scan-Job fuer Bitratechanger-Hybrid."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

BRIDGE_ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(BRIDGE_ROOT))
sys.path.insert(0, str(BRIDGE_ROOT / "vendor" / "bitrate"))

from bridge_io import emit, emit_progress_end, emit_progress_fraction  # noqa: E402
from mass_bitrate_core import rows_to_dicts, scan_folder  # noqa: E402


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--config-json", required=True)
    parser.add_argument("--output-json", required=True)
    args = parser.parse_args()

    config = json.loads(Path(args.config_json).read_text(encoding="utf-8"))
    input_folder = config.get("input_folder", "").strip()
    if not input_folder:
        emit("FEHLER: input_folder fehlt")
        return 1

    rows = scan_folder(
        input_folder,
        bool(config.get("recursive", True)),
        bool(config.get("only_lower", True)),
        config.get("rule_values") or {},
        progress_cb=lambda cur, total: emit_progress_fraction(cur, total, "Scan"),
    )
    out = {"rows": rows_to_dicts(rows), "input_folder": input_folder}
    Path(args.output_json).write_text(json.dumps(out, ensure_ascii=False, indent=2), encoding="utf-8")
    convert_count = sum(1 for r in rows if r.action == "convert")
    emit_progress_end()
    emit(f"Scan fertig: {len(rows)} Dateien, {convert_count} zum Konvertieren")
    emit(f"OUTPUT:{args.output_json}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
