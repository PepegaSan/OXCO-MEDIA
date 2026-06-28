#!/usr/bin/env python3
"""Long-running autotagger monitor bridge for Hail Mary."""
from __future__ import annotations

import argparse
import json
import signal
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent
VENDOR = ROOT / "vendor" / "autotagger"
sys.path.insert(0, str(VENDOR))
sys.path.insert(0, str(ROOT))

from bridge_io import configure_stdio, emit
from monitor_core import AutotaggerMonitor

configure_stdio()


def main() -> int:
    parser = argparse.ArgumentParser(description="Autotagger monitor bridge")
    parser.add_argument("--config-json", required=True)
    args = parser.parse_args()
    config_path = Path(args.config_json)
    if not config_path.is_file():
        emit(f"Konfiguration nicht gefunden: {config_path}")
        return 1

    data = json.loads(config_path.read_text(encoding="utf-8"))
    monitor = AutotaggerMonitor.from_json(data, emit)
    stop_requested = False

    def on_signal(_signum, _frame) -> None:
        nonlocal stop_requested
        stop_requested = True

    signal.signal(signal.SIGINT, on_signal)
    signal.signal(getattr(signal, "SIGTERM", signal.SIGINT), on_signal)

    try:
        monitor.start()
    except ValueError as err:
        emit(str(err))
        return 1

    emit("Autotagger Monitor laeuft.")
    try:
        while not stop_requested:
            time.sleep(0.5)
    finally:
        monitor.stop()
        emit("Monitor beendet.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
