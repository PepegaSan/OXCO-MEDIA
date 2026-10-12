#!/usr/bin/env python3
"""Hail Mary bridge: Text-to-Video — render text overlays as RGBA sprites.

Uses the exact renderer of the FFmpeg export (vendor/text_to_video/text_render.py), so the
preview shows the same pixels that end up in the video.
"""
from __future__ import annotations

import argparse
import base64
import json
import shutil
import sys
import tempfile
from pathlib import Path

BRIDGE_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(BRIDGE_DIR))

from bridge_io import configure_stdio, emit  # noqa: E402
from text_to_video_sub import ensure_sub_imports, segment_to_overlay  # noqa: E402


def main() -> int:
    configure_stdio()
    parser = argparse.ArgumentParser()
    parser.add_argument("--config-json", required=True)
    parser.add_argument("--output-json", required=True)
    args = parser.parse_args()

    cfg = json.loads(Path(args.config_json).read_text(encoding="utf-8"))
    items = cfg.get("items") or []
    vw = int(cfg.get("video_width") or 0)
    vh = int(cfg.get("video_height") or 0)

    try:
        ensure_sub_imports()
        from text_render import overlay_has_visible_text, render_overlay_sprite  # noqa: E402
    except Exception as exc:
        emit(f"ERROR: {exc}")
        return 1

    work = Path(tempfile.mkdtemp(prefix="vttool_sprites_"))
    sprites = []
    try:
        for item in items:
            if not isinstance(item, dict):
                continue
            key = str(item.get("key") or "")
            seg = item.get("segment")
            if not key or not isinstance(seg, dict):
                continue
            try:
                ov = segment_to_overlay(seg)
                if not overlay_has_visible_text(ov):
                    continue
                sp = render_overlay_sprite(work, ov, max_width=vw, max_height=vh)
                sprites.append(
                    {
                        "key": key,
                        "image_base64": base64.b64encode(sp.path.read_bytes()).decode("ascii"),
                        "width": sp.width,
                        "height": sp.height,
                        "origin_x": sp.origin_x,
                        "origin_y": sp.origin_y,
                        "content_width": sp.content_width,
                        "content_height": sp.content_height,
                        "warning": sp.warning,
                    }
                )
            except Exception as exc:  # one broken segment must not hide the others
                sprites.append({"key": key, "error": str(exc)})

        Path(args.output_json).write_text(json.dumps({"sprites": sprites}), encoding="utf-8")
        return 0
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    raise SystemExit(main())
