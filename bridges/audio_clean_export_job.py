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

import re  # noqa: E402

from bridge_io import emit, emit_progress  # noqa: E402

from audio_clean_core import (  # noqa: E402
    adjust_export_returncode_if_output_ok,
    boost_db_from_percent,
    build_export_command,
    clean_params_from_simple,
    probe_duration,
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


_TIME_RE = re.compile(r"time=(\d+):(\d+):(\d+(?:\.\d+)?)")
_total_seconds = 0.0


def _log_line(line: str) -> None:
    text = line.rstrip("\n\r")
    match = _TIME_RE.search(text)
    if match and "size=" in text:
        # FFmpeg's status line: turn it into progress instead of flooding the log.
        if _total_seconds > 0:
            h, m, sec = int(match.group(1)), int(match.group(2)), float(match.group(3))
            emit_progress(min(99.5, (h * 3600 + m * 60 + sec) / _total_seconds * 100.0), "Audio")
        return
    emit(text)


def render_preview(ffmpeg: str, ffprobe: str, in_path: str, clean, cfg: dict) -> int:
    """Short A/B snippet: the same seconds once untouched and once cleaned (WAV, no video)."""
    from audio_clean_core import build_audio_filter, run_ffmpeg  # noqa: E402

    out_dir = Path(cfg.get("preview_dir") or Path(cfg["output"]).parent)
    out_dir.mkdir(parents=True, exist_ok=True)
    seconds = max(2.0, min(float(cfg.get("preview_seconds") or 10.0), 60.0))
    try:
        duration = probe_duration(ffprobe, in_path)
    except Exception:
        duration = 0.0
    start = max(0.0, float(cfg.get("preview_start") or 0.0))
    if duration > 0:
        start = min(start, max(0.0, duration - seconds))
    # Denoisers/normalizers need a moment to settle — render 2 s earlier and drop them.
    warm = min(2.0, start)
    original = out_dir / "preview_original.wav"
    cleaned = out_dir / "preview_cleaned.wav"
    base = [ffmpeg, "-hide_banner", "-nostdin", "-y", "-ss", f"{start - warm:.3f}", "-t", f"{seconds + warm:.3f}", "-i", in_path]
    trim = f"atrim=start={warm:.3f},asetpts=PTS-STARTPTS"
    jobs = [
        (base + ["-filter_complex", f"[0:a:0]aformat=sample_fmts=fltp,{trim}[a]", "-map", "[a]", "-c:a", "pcm_s16le", str(original)], original),
        (base + ["-filter_complex", f"[0:a:0]{build_audio_filter(clean)},{trim}[a]", "-map", "[a]", "-c:a", "pcm_s16le", str(cleaned)], cleaned),
    ]
    for cmd, target in jobs:
        rc = run_ffmpeg(cmd, _log_line)
        if rc != 0 or not target.is_file():
            emit(f"FEHLER: Vorschau fehlgeschlagen (Code {rc})")
            return rc or 1
    emit(f"PREVIEW_ORIGINAL:{original}")
    emit(f"PREVIEW_CLEANED:{cleaned}")
    emit(f"OUTPUT:{cleaned}")
    return 0


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
    try:
        from ffmpeg_fps import normalize_target_fps
    except ImportError:
        from pathlib import Path as _P
        sys.path.insert(0, str(_P(__file__).resolve().parent))
        from ffmpeg_fps import normalize_target_fps
    target_fps = normalize_target_fps(cfg.get("target_fps"))

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

    if str(cfg.get("mode") or "").lower() == "preview":
        return render_preview(ffmpeg, ffprobe, in_path, clean, cfg)

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
        target_fps=target_fps if is_video else None,
    )

    emit(f"Export startet: {Path(in_path).name}")
    Path(out_path).parent.mkdir(parents=True, exist_ok=True)
    global _total_seconds
    try:
        _total_seconds = probe_duration(ffprobe, in_path)
    except Exception:
        _total_seconds = 0.0

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

    # Signature: (rc, ffprobe, out_path, expected_duration) → (rc, note). The old call passed
    # three arguments in the wrong order and crashed after every export.
    rc, note = adjust_export_returncode_if_output_ok(rc, ffprobe, out_path, _total_seconds)
    if note:
        emit(note)
    if rc != 0:
        emit(f"FEHLER: Export fehlgeschlagen (Code {rc})")
        return rc

    emit(f"Fertig: {out_path}")
    emit(f"OUTPUT:{out_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
