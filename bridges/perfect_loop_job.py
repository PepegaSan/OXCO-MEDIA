#!/usr/bin/env python3
"""Hail Mary Bridge: Perfect Loop — seam morph + optional smart trim."""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

BRIDGE_DIR = Path(__file__).resolve().parent
VENDOR_DIR = BRIDGE_DIR / "vendor" / "perfect_loop"
sys.path.insert(0, str(BRIDGE_DIR))
sys.path.insert(0, str(VENDOR_DIR))

from loop_engine import LoopSettings, analyze_loop, default_output_path, run_loop  # noqa: E402

from bridge_io import configure_stdio, emit, emit_progress, emit_progress_end  # noqa: E402


def build_jobs(cfg: dict) -> list[LoopSettings]:
    output_dir = str(cfg.get("output_dir") or "").strip() or None
    seam = float(cfg.get("seam_seconds") or 0.25)
    smart = bool(cfg.get("smart_trim") or False)
    min_loop = float(cfg.get("min_loop_seconds") or 1.5)
    encoder = str(cfg.get("ffmpeg_encoder") or "cpu")
    mute = bool(cfg.get("mute_audio", True))
    method = str(cfg.get("seam_method") or "auto")
    loop_count = max(1, int(cfg.get("loop_count") or 1))
    preview_max_width = max(0, int(cfg.get("preview_max_width") or 0))

    files = cfg.get("files") or []
    if not files and cfg.get("input"):
        files = [cfg["input"]]

    jobs: list[LoopSettings] = []
    for raw in files:
        inp = str(raw)
        out = str(cfg.get("output") or "").strip()
        if not out or len(files) > 1:
            out = default_output_path(inp, output_dir)
        elif output_dir and not Path(out).is_absolute():
            out = str(Path(output_dir) / out)
        jobs.append(
            LoopSettings(
                input_path=inp,
                output_path=out,
                seam_seconds=seam,
                smart_trim=smart,
                min_loop_seconds=min_loop,
                ffmpeg_encoder=encoder,
                mute_audio=mute,
                seam_method=method,
                target_fps=float(cfg.get("target_fps") or 0),
                loop_count=loop_count,
                preview_max_width=preview_max_width,
                crf=max(0, min(51, int(cfg.get("crf") or 0))),
                range_start_seconds=float(cfg.get("range_start_seconds") or 0),
                range_end_seconds=float(cfg.get("range_end_seconds") or 0),
            )
        )
    return jobs


def detail_from_analyze(result) -> dict:
    return {
        "quality_score": round(result.quality_score, 4),
        "quality_warning": result.quality_warning,
        "smart_trim_used": result.smart_trim_used,
        "trim_start_frame": result.trim_start_frame,
        "trim_end_frame": result.trim_end_frame,
        "frame_count": result.frame_count,
        "fps": result.fps,
        "duration_seconds": round(result.duration_seconds, 3),
        "recommended_seam_seconds": result.recommended_seam_seconds,
        "recommended_seam_method": result.recommended_seam_method,
        "range_start_seconds": round(result.range_start_seconds, 3),
        "range_end_seconds": round(result.range_end_seconds, 3),
    }


def main() -> int:
    configure_stdio()
    parser = argparse.ArgumentParser()
    parser.add_argument("--config-json", required=True)
    parser.add_argument("--output-json", required=True)
    args = parser.parse_args()

    cfg_path = Path(args.config_json)
    out_path = Path(args.output_json)
    try:
        raw = cfg_path.read_text(encoding="utf-8-sig")
        cfg = json.loads(raw)
    except (OSError, json.JSONDecodeError) as exc:
        emit(f"ERROR: Konfiguration unlesbar: {exc}")
        return 1

    mode = str(cfg.get("mode") or "export").strip().lower()
    jobs = build_jobs(cfg)
    if not jobs:
        emit("ERROR: Keine Eingabedatei")
        out_path.write_text(json.dumps({"success": False, "error": "no input"}), encoding="utf-8")
        return 1

    details: list[dict] = []
    outputs: list[str] = []
    try:
        for i, settings in enumerate(jobs):
            emit(f"[{i + 1}/{len(jobs)}] {Path(settings.input_path).name}")

            def progress(pct: float, label: str, _i=i, _n=len(jobs)) -> None:
                base = (_i / _n) * 100.0
                span = 100.0 / _n
                emit_progress(base + (pct / 100.0) * span, label)

            if mode == "analyze":
                result = analyze_loop(settings, log=emit, progress=progress)
                details.append(detail_from_analyze(result))
            else:
                if mode == "preview":
                    settings.loop_count = 1
                    settings.ffmpeg_encoder = "cpu"
                    if not settings.preview_max_width:
                        settings.preview_max_width = 640
                result = run_loop(settings, log=emit, progress=progress)
                outputs.append(result.output_path)
                details.append(
                    {
                        "output": result.output_path,
                        "quality_score": round(result.quality_score, 4),
                        "quality_warning": result.quality_warning,
                        "smart_trim_used": result.smart_trim_used,
                        "trim_start_frame": result.trim_start_frame,
                        "trim_end_frame": result.trim_end_frame,
                        "frame_count": result.frame_count,
                        "fps": result.fps,
                        "seam_method_used": result.seam_method_used,
                    }
                )
                emit(f"OUTPUT:{result.output_path}")
                if result.quality_warning:
                    emit(f"WARN:quality_score={result.quality_score:.3f}")
    except Exception as exc:
        emit(f"ERROR: {exc}")
        emit_progress_end()
        out_path.write_text(json.dumps({"success": False, "error": str(exc)}), encoding="utf-8")
        return 1

    emit_progress_end()
    payload = {
        "success": True,
        "mode": mode,
        "outputs": outputs,
        "details": details,
    }
    out_path.write_text(json.dumps(payload, indent=2), encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
