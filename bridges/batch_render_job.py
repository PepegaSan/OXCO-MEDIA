#!/usr/bin/env python3
"""Hail Mary Bridge: DaVinci Batch Render — sequentieller Resolve-Export."""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

BRIDGE_DIR = Path(__file__).resolve().parent
VENDOR_DIR = BRIDGE_DIR / "vendor" / "davinci_batch_render"
sys.path.insert(0, str(VENDOR_DIR))

from batch_core import run_batch_render  # noqa: E402

from bridge_io import configure_stdio, emit, emit_batch_item  # noqa: E402


def main() -> int:
    configure_stdio()
    parser = argparse.ArgumentParser()
    parser.add_argument("--config-json", required=True)
    parser.add_argument("--output-json", required=True)
    args = parser.parse_args()

    cfg_path = Path(args.config_json)
    out_path = Path(args.output_json)
    try:
        cfg = json.loads(cfg_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        emit(f"ERROR: Konfiguration unlesbar: {exc}")
        return 1

    videos = cfg.get("videos") or []
    if not videos:
        emit("ERROR: Keine Videos in der Konfiguration.")
        return 1

    def log(msg: str) -> None:
        emit(msg)

    def item_status(path: str, status: str) -> None:
        emit_batch_item(path, status)

    try:
        result = run_batch_render(
            videos,
            davinci_output_dir=str(cfg.get("davinci_output_dir") or ""),
            davinci_preset=str(cfg.get("davinci_preset") or ""),
            resolve_exe=str(cfg.get("resolve_exe") or ""),
            resolve_modules=str(cfg.get("resolve_modules") or ""),
            resolve_dll=str(cfg.get("resolve_dll") or ""),
            safe_output=bool(cfg.get("safe_output", True)),
            lang=str(cfg.get("ui_language") or "de"),
            log=log,
            item_status=item_status,
        )
    except Exception as exc:
        emit(f"ERROR: {exc}")
        out_path.write_text(json.dumps({"success": False, "error": str(exc)}), encoding="utf-8")
        return 1

    payload = {"success": True, **result}
    out_path.write_text(json.dumps(payload, indent=2), encoding="utf-8")

    last_done = next(
        (item for item in reversed(result.get("items", [])) if item.get("status") == "done"),
        None,
    )
    if last_done:
        out_dir = result.get("output_dir") or cfg.get("davinci_output_dir") or ""
        name = last_done.get("output_name") or ""
        if out_dir and name:
            emit(f"OUTPUT:{Path(out_dir) / name}")
    return 0 if result.get("fail", 0) == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
