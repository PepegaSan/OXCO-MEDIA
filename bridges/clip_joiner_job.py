#!/usr/bin/env python3
"""Hail Mary Bridge: Clip Joiner — FFmpeg/DaVinci concat wie clip-joiner."""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

BRIDGE_DIR = Path(__file__).resolve().parent
VENDOR_DIR = BRIDGE_DIR / "vendor" / "clip_joiner"
sys.path.insert(0, str(VENDOR_DIR))

from concat_engine import JoinJob, JoinSettings, run_batch, run_job  # noqa: E402

from bridge_io import configure_stdio, emit  # noqa: E402


def build_settings(cfg: dict) -> JoinSettings:
    return JoinSettings(
        output_dir=str(cfg.get("output_dir") or ""),
        mode=str(cfg.get("mode") or "ffmpeg"),
        ffmpeg_encoder=str(cfg.get("ffmpeg_encoder") or "nvidia_h264"),
        davinci_preset=str(cfg.get("davinci_preset") or "YouTube - 1080p"),
        davinci_timeout_s=float(cfg.get("davinci_timeout_s") or 3600),
        davinci_api_path=str(cfg.get("davinci_api_path") or ""),
    )


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

    settings = build_settings(cfg)
    batch_jobs = cfg.get("batch_jobs") or []

    def log(msg: str) -> None:
        emit(msg)

    outputs: list[str] = []
    try:
        if batch_jobs:
            jobs: list[JoinJob] = []
            for entry in batch_jobs:
                files = entry.get("files") or []
                name = str(entry.get("output_name") or "joined")
                jobs.append(JoinJob(files=list(files), output_name=name))
            outputs = run_batch(jobs, settings, log=log)
        else:
            files = cfg.get("files") or []
            name = str(cfg.get("output_name") or "joined")
            outputs.append(run_job(JoinJob(files=list(files), output_name=name), settings, log=log))
    except Exception as exc:
        emit(f"ERROR: {exc}")
        out_path.write_text(json.dumps({"success": False, "error": str(exc)}), encoding="utf-8")
        return 1

    result = {"success": True, "outputs": outputs}
    out_path.write_text(json.dumps(result, indent=2), encoding="utf-8")
    if outputs:
        emit(f"OUTPUT:{outputs[-1]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
