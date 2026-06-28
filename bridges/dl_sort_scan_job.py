#!/usr/bin/env python3
"""One-shot folder scan for DL Sort profiles."""
from __future__ import annotations

import argparse
import logging
import sys
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


def main() -> int:
    parser = argparse.ArgumentParser(description="DL Sort scan bridge")
    parser.add_argument("--config-dir", required=True)
    args = parser.parse_args()
    config_dir = Path(args.config_dir)

    root = logging.getLogger()
    root.handlers.clear()
    handler = _BridgeLogHandler()
    handler.setFormatter(logging.Formatter("%(message)s"))
    root.addHandler(handler)
    root.setLevel(logging.INFO)

    cfg = load_config(config_dir)
    total = 0
    scanned = 0
    for profile in cfg.profiles:
        if not profile.run_enabled:
            continue
        folder = profile.watch_folder.strip()
        if not folder or not Path(folder).is_dir():
            emit(f"Uebersprungen (Ordner fehlt): {profile.name}")
            continue
        ctrl = WatchController()
        snap = runtime_config_for_profile(cfg, profile)
        ctrl.start(folder, snap)
        count = ctrl.scan_folder_now(folder)
        ctrl.stop()
        total += count
        scanned += 1
        emit(f"Scan {profile.name}: {count} Datei(en) eingereiht")

    emit(f"OUTPUT: {total} Datei(en) in {scanned} Profil(en) gescannt")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
