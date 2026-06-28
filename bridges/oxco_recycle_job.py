#!/usr/bin/env python3
"""Send files to Windows recycle bin via Oxco oxco_workers."""
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


def _oxco_dir() -> Path:
    """Vendored Oxco-Quellen (im Paket enthalten) bevorzugen, sonst externes Repo."""
    vendored = ROOT / "vendor" / "oxco"
    if (vendored / "oxco_workers.py").is_file():
        return vendored
    return _projects_root() / "Oxco"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--config-json", required=True)
    args = parser.parse_args()
    data = json.loads(Path(args.config_json).read_text(encoding="utf-8"))
    paths = [Path(p) for p in (data.get("paths") or []) if str(p).strip()]
    if not paths:
        emit("Keine Pfade angegeben.")
        return 1

    oxco_dir = _oxco_dir()
    sys.path.insert(0, str(oxco_dir))
    import oxco_workers as ow  # noqa: E402

    deleted, err = ow.send_paths_to_recycle_bin(paths)
    if err == "unsupported":
        emit("ERROR: Papierkorb nicht unterstützt.")
        return 1
    for p in deleted:
        emit(f"Gelöscht: {p.name}")
    emit(json.dumps({"deleted": [str(p) for p in deleted]}, ensure_ascii=False))
    return 0 if deleted else 1


if __name__ == "__main__":
    raise SystemExit(main())
