#!/usr/bin/env python3
"""Long-running download sorter watch bridge for Hail Mary."""
from __future__ import annotations

import argparse
import logging
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent
VENDOR = ROOT / "vendor" / "dl_sort"
sys.path.insert(0, str(VENDOR))
sys.path.insert(0, str(ROOT))

from bridge_io import configure_stdio, emit
from config_io import load_config, runtime_config_for_profile
from watch_service import WatchController

configure_stdio()


class _BridgeLogHandler(logging.Handler):
    def emit(self, record: logging.LogRecord) -> None:
        emit(self.format(record))


def _setup_logging() -> None:
    root = logging.getLogger()
    root.handlers.clear()
    handler = _BridgeLogHandler()
    handler.setFormatter(logging.Formatter("%(message)s"))
    root.addHandler(handler)
    root.setLevel(logging.INFO)


def main() -> int:
    parser = argparse.ArgumentParser(description="DL Sort monitor bridge")
    parser.add_argument("--config-dir", required=True, help="Folder containing config.json")
    args = parser.parse_args()
    config_dir = Path(args.config_dir)
    _setup_logging()

    controllers: dict[str, WatchController] = {}

    def sync_once() -> None:
        cfg = load_config(config_dir)
        enabled = [
            p
            for p in cfg.profiles
            if p.run_enabled and p.watch_folder.strip() and Path(p.watch_folder.strip()).is_dir()
        ]
        enabled_ids = {p.profile_id for p in enabled}

        for profile in enabled:
            pid = profile.profile_id
            folder = profile.watch_folder.strip()
            snap = runtime_config_for_profile(cfg, profile)
            existing = controllers.get(pid)
            if existing is None or not existing.is_running:
                if existing is not None:
                    existing.stop()
                ctrl = WatchController()
                ctrl.start(folder, snap)
                controllers[pid] = ctrl
                emit(f"Ueberwachung gestartet: {profile.name} -> {folder}")
            else:
                existing.set_runtime_config(snap)

        for pid in list(controllers.keys()):
            if pid not in enabled_ids:
                controllers[pid].stop()
                del controllers[pid]
                emit(f"Ueberwachung gestoppt fuer Profil {pid}")

    sync_once()
    emit("DL Sort Monitor laeuft (Strg+C zum Beenden).")
    try:
        while True:
            time.sleep(0.9)
            sync_once()
    except KeyboardInterrupt:
        pass
    finally:
        for ctrl in controllers.values():
            if ctrl.is_running:
                ctrl.stop()
        emit("Monitor beendet.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
