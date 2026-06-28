#!/usr/bin/env python3
"""Marker Autocut → DaVinci Resolve export bridge for Hail Mary."""
from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))

from bridge_io import configure_stdio, emit

configure_stdio()


def _projects_root() -> Path:
    raw = os.environ.get("HAIL_MARY_PROJECTS_ROOT", "").strip()
    if raw:
        return Path(raw)
    return Path.home() / "Projects"


def main() -> int:
    parser = argparse.ArgumentParser(description="Marker Autocut Resolve export")
    parser.add_argument("--config-json", required=True)
    args = parser.parse_args()

    config_path = Path(args.config_json)
    if not config_path.is_file():
        emit(f"Konfiguration nicht gefunden: {config_path}")
        return 1

    data = json.loads(config_path.read_text(encoding="utf-8"))
    rows = data.get("rows") or []
    if not rows:
        emit("Keine Marker zum Export.")
        return 1

    vendored = ROOT / "vendor" / "marker_autocut"
    tool_dir = vendored if (vendored / "resolve_timeline_export.py").is_file() else _projects_root() / "Marker autocut"
    if not tool_dir.is_dir():
        emit(f"Marker-autocut-Ordner fehlt: {tool_dir}")
        return 1

    sys.path.insert(0, str(tool_dir))
    try:
        from resolve_timeline_export import run_export_in_scripting_thread
    except ImportError as exc:
        emit(f"Import fehlgeschlagen: {exc}")
        return 1

    mode = (data.get("mode") or "per_file").strip()
    options = data.get("options") or {}

    def log(msg: str) -> None:
        emit(str(msg))

    try:
        run_export_in_scripting_thread(mode, rows, options, log)
    except Exception as exc:
        emit(f"ERROR: {exc}")
        return 1

    emit("Resolve-Export abgeschlossen.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
