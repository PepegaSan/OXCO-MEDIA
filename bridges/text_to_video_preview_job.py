#!/usr/bin/env python3
"""Hail Mary bridge: Text-to-Video — preview frame with overlays."""
from __future__ import annotations

import argparse
import base64
import json
import sys
from pathlib import Path

BRIDGE_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(BRIDGE_DIR))

from bridge_io import configure_stdio, emit  # noqa: E402
from text_to_video_sub import ensure_sub_imports, overlays_from_config  # noqa: E402


def main() -> int:
    configure_stdio()
    parser = argparse.ArgumentParser()
    parser.add_argument("--config-json", required=True)
    parser.add_argument("--output-json", required=True)
    args = parser.parse_args()

    cfg = json.loads(Path(args.config_json).read_text(encoding="utf-8"))
    video_path = (cfg.get("video_path") or "").strip()
    if not video_path or not Path(video_path).is_file():
        emit("ERROR: video_path fehlt oder existiert nicht")
        return 1

    time_sec = float(cfg.get("time_sec") or 0)
    srt_path = (cfg.get("srt_path") or "").strip()
    segments = cfg.get("overlay_segments") or []
    draft = cfg.get("draft_segment")

    try:
        ensure_sub_imports()
        from overlay_core import extract_preview_frame_with_overlay, probe_video  # noqa: E402

        info = probe_video(video_path)
        overlays = overlays_from_config(segments, draft if isinstance(draft, dict) else None)
        srt = srt_path if srt_path and Path(srt_path).is_file() else None
        png = extract_preview_frame_with_overlay(
            video_path,
            time_sec,
            overlays=overlays,
            srt_path=srt,
            cached_info=info,
            width_max=int(cfg.get("width_max") or 960),
            height_max=int(cfg.get("height_max") or 720),
        )
        out = {
            "image_base64": base64.b64encode(png).decode("ascii"),
            "width": info.width,
            "height": info.height,
            "duration_sec": info.duration_sec,
        }
        Path(args.output_json).write_text(json.dumps(out), encoding="utf-8")
        return 0
    except Exception as exc:
        emit(f"ERROR: {exc}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
