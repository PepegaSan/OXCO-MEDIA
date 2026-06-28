#!/usr/bin/env python3
"""Hail Mary bridge: Text-to-Video — DaVinci Resolve import & render."""
from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

BRIDGE_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(BRIDGE_DIR))

from bridge_io import configure_stdio, emit  # noqa: E402
from text_to_video_sub import ensure_sub_imports  # noqa: E402


def main() -> int:
    configure_stdio()
    parser = argparse.ArgumentParser()
    parser.add_argument("--config-json", required=True)
    args = parser.parse_args()

    cfg = json.loads(Path(args.config_json).read_text(encoding="utf-8"))
    video_path = (cfg.get("video_path") or "").strip()
    if not video_path or not Path(video_path).is_file():
        emit("ERROR: video_path fehlt oder existiert nicht")
        return 1

    preset = str(cfg.get("preset") or "YouTube - 1080p").strip()
    if not preset:
        emit("ERROR: preset fehlt")
        return 1

    raw_out = str(cfg.get("output_dir") or "").strip()
    output_dir = raw_out if raw_out else str(Path(video_path).parent)

    try:
        ensure_sub_imports()
        from davinci_api import (  # noqa: E402
            ResolveError,
            apply_project_timeline_settings,
            cleanup_timelines,
            connect_resolve,
            register_custom_resolve_paths,
            render_with_preset,
            scripting_thread,
            to_forward,
        )

        mods = str(cfg.get("resolve_modules") or "").strip()
        dll = str(cfg.get("resolve_dll") or "").strip()
        exe = str(cfg.get("resolve_exe") or "").strip()
        if mods or dll or exe:
            register_custom_resolve_paths(
                modules_dir=mods or None,
                fusionscript_dll=dll or None,
                resolve_exe=exe or None,
            )

        def notify(msg: str) -> None:
            emit(msg)

        with scripting_thread():
            _resolve, project, media_pool, _root = connect_resolve(
                status_callback=notify,
                auto_launch=True,
                create_scratch_project_name="SubTool",
            )
            clips = media_pool.ImportMedia([to_forward(video_path)])
            if not clips:
                raise ResolveError("ImportMedia lieferte keine Clips.")
            clip = clips[0]
            time.sleep(0.35)
            Path(output_dir).mkdir(parents=True, exist_ok=True)
            cleanup_timelines(project, media_pool, name_prefix="SubTool_")
            fps_s = clip.GetClipProperty("FPS") or "25"
            res_s = clip.GetClipProperty("Resolution") or "1920x1080"
            apply_project_timeline_settings(project, fps_s, res_s)
            tl_name = f"SubTool_{int(time.time())}"
            timeline = media_pool.CreateEmptyTimeline(tl_name)
            if not timeline:
                raise ResolveError("Timeline konnte nicht erstellt werden.")
            project.SetCurrentTimeline(timeline)
            media_pool.AppendToTimeline([{"mediaPoolItem": clip}])
            out_name = Path(video_path).stem + "_deliver"
            render_with_preset(
                project,
                output_dir=output_dir,
                output_name=out_name,
                preset_name=preset,
                status_callback=notify,
            )

        out_file = Path(output_dir) / out_name
        candidates = sorted(Path(output_dir).glob(f"{out_name}.*"), key=lambda p: p.stat().st_mtime, reverse=True)
        if candidates:
            emit(f"OUTPUT:{candidates[0]}")
        elif out_file.is_file():
            emit(f"OUTPUT:{out_file}")
        emit("DaVinci-Render abgeschlossen.")
        return 0
    except Exception as exc:
        emit(f"ERROR: {exc}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
