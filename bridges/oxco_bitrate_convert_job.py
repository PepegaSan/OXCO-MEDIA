#!/usr/bin/env python3
"""Oxco Bitrate-Konvertierung — nutzt oxco_workers.convert_video_rows wie das Original-Oxco."""
from __future__ import annotations

import argparse
import json
import sys
import threading
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / "vendor" / "oxco"))

from bridge_io import configure_stdio, emit, emit_progress_fraction  # noqa: E402

configure_stdio()


def _rows_from_json(raw_rows: list) -> list:
    from oxco_workers import VideoRow  # noqa: E402

    rows: list[VideoRow] = []
    for item in raw_rows:
        if not isinstance(item, dict):
            continue
        path = str(item.get("path") or "").strip()
        if not path:
            continue
        rows.append(
            VideoRow(
                path=Path(path),
                width=int(item.get("width") or 0),
                height=int(item.get("height") or 0),
                source_kbps=item.get("source_kbps"),
                target_rule_kbps=item.get("target_rule_kbps"),
                effective_target_kbps=item.get("effective_target_kbps"),
                action=str(item.get("action") or ""),
                reason=str(item.get("reason") or ""),
            )
        )
    return rows


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--config-json", required=True)
    parser.add_argument("--rows-json", required=True)
    args = parser.parse_args()

    config = json.loads(Path(args.config_json).read_text(encoding="utf-8"))
    payload = json.loads(Path(args.rows_json).read_text(encoding="utf-8"))
    raw_rows = payload.get("rows") if isinstance(payload, dict) else payload
    if not isinstance(raw_rows, list):
        emit("FEHLER: rows ungueltig")
        return 1

    input_folder = str(config.get("input_folder") or "").strip()
    output_folder = str(config.get("output_folder") or "").strip()
    if not input_folder or not output_folder:
        emit("FEHLER: input_folder oder output_folder fehlt")
        return 1

    delete_ok = bool(config.get("delete_source_after_ok"))
    if not delete_ok:
        delete_ok = str(config.get("post_success_action") or "").strip().lower() == "delete_original"

    rows = _rows_from_json(raw_rows)
    if not rows:
        emit("FEHLER: keine Scan-Zeilen")
        return 1

    from oxco_workers import convert_video_rows  # noqa: E402

    stop_event = threading.Event()

    def log(msg: str) -> None:
        emit(msg)

    convert_video_rows(
        rows,
        Path(input_folder),
        Path(output_folder),
        suffix=str(config.get("suffix") or "_bitrate").strip() or "_bitrate",
        output_mp4=bool(config.get("output_mp4", False)),
        codec=str(config.get("codec") or "libx264"),
        audio_mode=str(config.get("audio_mode") or "copy"),
        stop_event=stop_event,
        log=log,
        progress=lambda cur, tot: emit_progress_fraction(cur, tot, "Konvertierung"),
        delete_source_after_ok=delete_ok,
        ui_lang=str(config.get("ui_language") or "de"),
    )

    from bridge_io import emit_progress_end  # noqa: E402

    emit_progress_end()

    emit(f"Konvertierung abgeschlossen")
    emit(f"OUTPUT:{output_folder}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
