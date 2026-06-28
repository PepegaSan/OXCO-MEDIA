#!/usr/bin/env python3
"""Hail Mary bridge: Text-to-Video — video probe."""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

BRIDGE_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(BRIDGE_DIR))

from bridge_io import configure_stdio, emit  # noqa: E402
from text_to_video_sub import ensure_sub_imports  # noqa: E402


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

    try:
        ensure_sub_imports()
        from overlay_core import probe_video, suggest_ffmpeg_bitrate_from_bps  # noqa: E402

        info = probe_video(video_path)
        sug = suggest_ffmpeg_bitrate_from_bps(info.source_bitrate_bps)
        out = {
            "path": video_path,
            "duration_sec": info.duration_sec,
            "width": info.width,
            "height": info.height,
            "fps": info.fps,
            "suggested_bitrate": sug or "",
        }
        Path(args.output_json).write_text(json.dumps(out, ensure_ascii=False), encoding="utf-8")
        return 0
    except Exception as exc:
        emit(f"ERROR: {exc}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
