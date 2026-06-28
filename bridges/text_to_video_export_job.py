#!/usr/bin/env python3
"""Hail Mary bridge: Text-to-Video — FFmpeg export."""
from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
from pathlib import Path

BRIDGE_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(BRIDGE_DIR))

from bridge_io import configure_stdio, emit, emit_progress  # noqa: E402
from text_to_video_sub import ensure_sub_imports, overlays_from_config, resolve_encoder  # noqa: E402


def _parse_time_seconds(line: str):
    import re

    m = re.search(r"time=(\d{2}):(\d{2}):(\d{2}\.\d+)", line)
    if not m:
        return None
    h, mi, s = int(m.group(1)), int(m.group(2)), float(m.group(3))
    return h * 3600 + mi * 60 + s


def main() -> int:
    configure_stdio()
    parser = argparse.ArgumentParser()
    parser.add_argument("--config-json", required=True)
    args = parser.parse_args()

    cfg = json.loads(Path(args.config_json).read_text(encoding="utf-8"))
    src = (cfg.get("video_path") or "").strip()
    dst = (cfg.get("output_path") or "").strip()
    if not src or not Path(src).is_file():
        emit("ERROR: video_path fehlt oder existiert nicht")
        return 1
    if not dst:
        emit("ERROR: output_path fehlt")
        return 1

    try:
        ensure_sub_imports()
        from overlay_core import (  # noqa: E402
            FFmpegNotFoundError,
            build_export_command,
            parse_bitrate,
            probe_video,
        )

        info = probe_video(src)
        segments = cfg.get("overlay_segments") or []
        draft = cfg.get("draft_segment")
        overlays = overlays_from_config(segments, draft if isinstance(draft, dict) else None)
        srt = (cfg.get("srt_path") or "").strip()
        srt_path = srt if srt and Path(srt).is_file() else None

        if not overlays and not srt_path:
            emit("ERROR: Kein Text-Abschnitt und keine SRT-Datei")
            return 1

        export_as_gif = str(cfg.get("export_container") or "mp4").lower() == "gif"
        encoder = resolve_encoder(str(cfg.get("codec") or "libx264"))
        bitrate = parse_bitrate(str(cfg.get("bitrate") or "8M"))
        gfps = max(1, min(int(cfg.get("gif_fps") or 15), 60))
        gmw = max(160, min(int(cfg.get("gif_max_width") or 720), 1920))
        gncol = max(8, min(int(cfg.get("gif_palette_colors") or 128), 256))

        cmd, ff_cwd = build_export_command(
            src=src,
            dst=dst,
            encoder=encoder,
            bitrate=bitrate,
            overlays=overlays,
            srt_path=srt_path,
            audio_copy=bool(cfg.get("audio_copy", True)),
            progress_file=None,
            export_as_gif=export_as_gif,
            gif_fps=gfps,
            gif_max_width=gmw,
            gif_palette_colors=gncol,
            video_width=int(info.width),
            video_height=int(info.height),
        )

        duration = max(0.01, float(info.duration_sec))
        proc = subprocess.Popen(
            cmd,
            stderr=subprocess.PIPE,
            stdout=subprocess.DEVNULL,
            text=True,
            encoding="utf-8",
            errors="replace",
            cwd=ff_cwd,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        assert proc.stderr is not None
        for line in proc.stderr:
            t = _parse_time_seconds(line)
            if t is not None:
                pct = min(100.0, max(0.0, 100.0 * t / duration))
                emit_progress(pct, "Export")
        code = proc.wait()
        if ff_cwd:
            shutil.rmtree(ff_cwd, ignore_errors=True)

        if code == 0 and Path(dst).is_file():
            emit(f"OUTPUT:{dst}")
            emit("Fertig.")
            return 0

        emit(f"ERROR: ffmpeg beendete sich mit Code {code}")
        return 1
    except FFmpegNotFoundError as exc:
        emit(f"ERROR: {exc}")
        return 1
    except Exception as exc:
        emit(f"ERROR: {exc}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
