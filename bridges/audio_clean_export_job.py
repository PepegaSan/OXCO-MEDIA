#!/usr/bin/env python3
"""Audio-Clean Export fuer Hail Mary Hybrid."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

BRIDGE_ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(BRIDGE_ROOT))
sys.path.insert(0, str(BRIDGE_ROOT / "vendor" / "audio_clean"))

from bridge_io import emit  # noqa: E402

from audio_clean_core import (  # noqa: E402
    adjust_export_returncode_if_output_ok,
    boost_db_from_percent,
    build_export_command,
    clean_params_from_simple,
    has_audio_stream,
    has_video_stream,
    nvenc_windows_two_step_active,
    probe_video_bitrate_kbps,
    run_export_nvenc_two_step_windows,
    run_ffmpeg,
    video_codec_needs_bitrate,
    which_ffprobe,
    which_ffmpeg,
)


def _log_line(line: str) -> None:
    emit(line.rstrip("\n\r"))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--config-json", required=True)
    args = parser.parse_args()

    cfg = json.loads(Path(args.config_json).read_text(encoding="utf-8"))
    in_path = cfg.get("input", "").strip()
    out_path = cfg.get("output", "").strip()
    if not in_path or not out_path:
        emit("FEHLER: input/output fehlt")
        return 1

    ffmpeg = which_ffmpeg()
    ffprobe = which_ffprobe()
    if not ffmpeg or not ffprobe:
        emit("FEHLER: ffmpeg/ffprobe nicht gefunden")
        return 1

    preset = cfg.get("preset", "mid")
    focus_echo = bool(cfg.get("focus_echo", False))
    focus_noise = bool(cfg.get("focus_noise", False))
    boost_pct = float(cfg.get("boost_pct", 116.67))
    video_codec = cfg.get("video_codec", "copy")
    video_bitrate_kbps = int(cfg.get("video_bitrate_kbps", 4000))

    boost_db = boost_db_from_percent(boost_pct)
    clean = clean_params_from_simple(
        preset,
        boost_db,
        focus_echo=focus_echo,
        focus_noise=focus_noise,
    )

    is_video = has_video_stream(ffprobe, in_path)
    if is_video and video_codec != "copy" and video_codec_needs_bitrate(video_codec):
        if not cfg.get("video_bitrate_set"):
            probed = probe_video_bitrate_kbps(ffprobe, in_path)
            if probed:
                video_bitrate_kbps = probed

    if not has_audio_stream(ffprobe, in_path):
        emit("FEHLER: keine Audiospur")
        return 1

    cmd = build_export_command(
        ffmpeg,
        ffprobe,
        in_path,
        out_path,
        clean,
        trim_start=0.0,
        trim_end=None,
        video_codec=video_codec if is_video else "copy",
        video_bitrate_kbps=video_bitrate_kbps if is_video else 4000,
    )

    emit(f"Export startet: {Path(in_path).name}")
    Path(out_path).parent.mkdir(parents=True, exist_ok=True)

    if is_video and nvenc_windows_two_step_active(video_codec, use_trim=False):
        rc = run_export_nvenc_two_step_windows(
            ffmpeg,
            ffprobe,
            in_path,
            out_path,
            clean,
            trim_start=0.0,
            trim_end=None,
            video_codec=video_codec,
            video_bitrate_kbps=video_bitrate_kbps,
            log_line=_log_line,
        )
    else:
        rc = run_ffmpeg(cmd, _log_line)

    rc = adjust_export_returncode_if_output_ok(rc, out_path, ffprobe)
    if rc != 0:
        emit(f"FEHLER: Export fehlgeschlagen (Code {rc})")
        return rc

    emit(f"Fertig: {out_path}")
    emit(f"OUTPUT:{out_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
