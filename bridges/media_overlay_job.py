#!/usr/bin/env python3
"""Hail Mary Bridge: Media Overlay — image/video PiP onto a main video."""
from __future__ import annotations

import argparse
import base64
import json
import sys
from pathlib import Path

BRIDGE_DIR = Path(__file__).resolve().parent
VENDOR_DIR = BRIDGE_DIR / "vendor" / "media_overlay"
sys.path.insert(0, str(BRIDGE_DIR))
sys.path.insert(0, str(VENDOR_DIR))

from overlay_engine import (  # noqa: E402
    OverlaySettings,
    default_output_path,
    extract_preview_frame,
    probe_media,
    run_overlay,
)

from bridge_io import configure_stdio, emit, emit_progress, emit_progress_end  # noqa: E402


def build_settings(cfg: dict) -> OverlaySettings:
    main = str(cfg.get("main") or cfg.get("input") or "").strip()
    overlay = str(cfg.get("overlay") or "").strip()
    if not main:
        raise ValueError("main video path required")
    if not overlay:
        raise ValueError("overlay media path required")

    output_dir = str(cfg.get("output_dir") or "").strip() or None
    out = str(cfg.get("output") or "").strip()
    if not out:
        out = default_output_path(main, output_dir)
    elif output_dir and not Path(out).is_absolute():
        out = str(Path(output_dir) / out)

    def number(key: str, default: float) -> float:
        raw = cfg.get(key)
        if raw is None or raw == "":
            return default
        return float(raw)

    return OverlaySettings(
        main_path=main,
        overlay_path=overlay,
        output_path=out,
        # Zero is a valid edge position; ``or 24`` used to move x/y=0 inward.
        pos_x=int(number("pos_x", 24)),
        pos_y=int(number("pos_y", 24)),
        width_percent=number("width_percent", 25.0),
        start_seconds=number("start_seconds", 0.0),
        end_seconds=number("end_seconds", 0.0),
        main_audio=bool(cfg.get("main_audio", True)),
        overlay_audio=bool(cfg.get("overlay_audio", False)),
        ffmpeg_encoder=str(cfg.get("ffmpeg_encoder") or "nvidia_h264"),
        target_fps=number("target_fps", 0.0),
    )


def run_probe(cfg: dict, out_path: Path) -> int:
    main = str(cfg.get("main") or cfg.get("input") or "").strip()
    if not main or not Path(main).is_file():
        emit("ERROR: main video missing")
        out_path.write_text(json.dumps({"success": False, "error": "no main"}), encoding="utf-8")
        return 1
    try:
        w, h, dur, has_v, has_a = probe_media(main)
        if not has_v or w <= 0:
            raise RuntimeError("no video stream")
        payload = {
            "success": True,
            "width": w,
            "height": h,
            "duration_sec": dur,
            "has_audio": has_a,
        }
        overlay = str(cfg.get("overlay") or "").strip()
        if overlay and Path(overlay).is_file():
            ow, oh, _, ov_v, _ = probe_media(overlay)
            if ov_v and ow > 0 and oh > 0:
                payload["overlay_width"] = ow
                payload["overlay_height"] = oh
        out_path.write_text(json.dumps(payload), encoding="utf-8")
        return 0
    except Exception as exc:
        emit(f"ERROR: {exc}")
        out_path.write_text(json.dumps({"success": False, "error": str(exc)}), encoding="utf-8")
        return 1


def run_preview(cfg: dict, out_path: Path) -> int:
    try:
        settings = build_settings(cfg)
        time_sec = float(cfg.get("time_sec") or 0)
        force_show = bool(cfg.get("force_show", True))
        png, mw, mh, dur, ow, oh = extract_preview_frame(
            settings,
            time_sec,
            width_max=int(cfg.get("width_max") or 1280),
            height_max=int(cfg.get("height_max") or 720),
            force_show=force_show,
        )
        payload = {
            "success": True,
            "image_base64": base64.b64encode(png).decode("ascii"),
            "width": mw,
            "height": mh,
            "duration_sec": dur,
            "overlay_width": ow,
            "overlay_height": oh,
        }
        out_path.write_text(json.dumps(payload), encoding="utf-8")
        return 0
    except Exception as exc:
        emit(f"ERROR: {exc}")
        out_path.write_text(json.dumps({"success": False, "error": str(exc)}), encoding="utf-8")
        return 1


def main() -> int:
    configure_stdio()
    parser = argparse.ArgumentParser()
    parser.add_argument("--config-json", required=True)
    parser.add_argument("--output-json", required=True)
    args = parser.parse_args()

    cfg_path = Path(args.config_json)
    out_path = Path(args.output_json)
    try:
        cfg = json.loads(cfg_path.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as exc:
        emit(f"ERROR: {exc}")
        out_path.write_text(json.dumps({"success": False, "error": str(exc)}), encoding="utf-8")
        return 1

    mode = str(cfg.get("mode") or "export").strip().lower()
    if mode == "probe":
        return run_probe(cfg, out_path)
    if mode == "preview":
        return run_preview(cfg, out_path)

    try:
        settings = build_settings(cfg)
    except (ValueError, TypeError) as exc:
        emit(f"ERROR: {exc}")
        out_path.write_text(json.dumps({"success": False, "error": str(exc)}), encoding="utf-8")
        return 1

    try:
        emit(f"Main: {Path(settings.main_path).name}")
        emit(f"Overlay: {Path(settings.overlay_path).name}")

        def progress(pct: float, label: str) -> None:
            emit_progress(pct, label)

        result = run_overlay(settings, log=emit, progress=progress)
        emit(f"OUTPUT:{result}")
        emit_progress_end()
        out_path.write_text(
            json.dumps({"success": True, "output": result, "outputs": [result]}, indent=2),
            encoding="utf-8",
        )
        return 0
    except Exception as exc:
        emit(f"ERROR: {exc}")
        emit_progress_end()
        out_path.write_text(json.dumps({"success": False, "error": str(exc)}), encoding="utf-8")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
