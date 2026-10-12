"""Concatenate ordered video clips via FFmpeg or DaVinci Resolve."""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, List, Optional, Sequence

from resolve_pipeline import (
    default_davinci_api_path,
    normalize_media_path,
    resolve_davinci_api_path,
)
from utf8_io import enable_python_utf8_mode

LogFn = Callable[[str], None]

VIDEO_EXTS = {".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".mts", ".m2ts", ".wmv", ".flv"}


@dataclass
class JoinJob:
    files: List[str] = field(default_factory=list)
    output_name: str = "joined"


@dataclass
class JoinSettings:
    output_dir: str = ""
    mode: str = "ffmpeg"  # ffmpeg | davinci | both
    ffmpeg_encoder: str = "cpu"  # cpu | nvidia_h264 | copy | ...
    davinci_preset: str = "YouTube - 1080p"
    davinci_timeout_s: float = 3600.0
    davinci_api_path: str = ""
    target_fps: float = 0.0


def _log(log: Optional[LogFn], msg: str) -> None:
    if log:
        log(msg)


from media_probe import pick_timeline_fps_from_clips, probe_media


def normalize_paths(raw_paths: Sequence[str]) -> List[str]:
    out: List[str] = []
    seen: set[str] = set()
    for raw in raw_paths:
        try:
            p = normalize_media_path(raw)
        except Exception:
            continue
        if not p or p in seen:
            continue
        if Path(p).suffix.lower() in VIDEO_EXTS:
            out.append(p)
            seen.add(p)
    return out


def find_ffmpeg() -> Optional[str]:
    local = Path(__file__).resolve().parent / "ffmpeg.exe"
    if local.is_file():
        return str(local)
    return shutil.which("ffmpeg")


def find_ffprobe() -> Optional[str]:
    local = Path(__file__).resolve().parent / "ffprobe.exe"
    if local.is_file():
        return str(local)
    return shutil.which("ffprobe")


def probe_video(path: str) -> dict[str, Any]:
    ffprobe = find_ffprobe()
    if not ffprobe:
        return {}
    try:
        out = subprocess.check_output(
            [
                ffprobe,
                "-v",
                "quiet",
                "-print_format",
                "json",
                "-show_streams",
                "-show_format",
                path,
            ],
            stderr=subprocess.DEVNULL,
            timeout=30,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        return json.loads(out.decode("utf-8", errors="replace"))
    except (OSError, subprocess.SubprocessError, json.JSONDecodeError):
        return {}


def _video_stream(meta: dict[str, Any]) -> Optional[dict[str, Any]]:
    for stream in meta.get("streams") or []:
        if stream.get("codec_type") == "video":
            return stream
    return None


def _audio_stream(meta: dict[str, Any]) -> Optional[dict[str, Any]]:
    for stream in meta.get("streams") or []:
        if stream.get("codec_type") == "audio":
            return stream
    return None


def _signature(meta: dict[str, Any]) -> Optional[tuple]:
    v = _video_stream(meta)
    if not v:
        return None
    a = _audio_stream(meta)
    audio = (a.get("codec_name"), a.get("sample_rate"), a.get("channels")) if a else None
    return (
        v.get("width"), v.get("height"), v.get("codec_name"), v.get("pix_fmt"),
        v.get("r_frame_rate"), audio,
    )


def can_stream_copy_concat(clips: Sequence[str]) -> bool:
    """Stream copy only works when every clip has identical video AND audio parameters."""
    if len(clips) < 2:
        return True
    ref = _signature(probe_video(clips[0]))
    if ref is None:
        return False
    return all(_signature(probe_video(clip)) == ref for clip in clips[1:])


def _display_size(stream: dict[str, Any]) -> tuple[int, int]:
    w, h = int(stream.get("width") or 0), int(stream.get("height") or 0)
    rot = 0
    for side in stream.get("side_data_list") or []:
        try:
            rot = int(round(float(side.get("rotation", 0)))) % 360
        except (TypeError, ValueError):
            pass
    if rot in (90, 270):
        w, h = h, w
    return w, h


def _fps_of(stream: dict[str, Any]) -> float:
    raw = str(stream.get("r_frame_rate") or stream.get("avg_frame_rate") or "30/1")
    try:
        num, den = raw.split("/", 1) if "/" in raw else (raw, "1")
        fps = float(num) / float(den)
    except (ValueError, ZeroDivisionError):
        fps = 30.0
    return fps if 1.0 <= fps <= 240.0 else 30.0


def _duration_of(meta: dict[str, Any]) -> float:
    try:
        return max(0.0, float((meta.get("format") or {}).get("duration") or 0.0))
    except (TypeError, ValueError):
        return 0.0


def unique_output_path(output_dir: str, stem: str, ext: str = ".mp4") -> str:
    base = os.path.join(output_dir, stem)
    candidate = base + ext
    if not os.path.exists(candidate):
        return candidate
    for i in range(2, 100):
        candidate = f"{base}_{i}{ext}"
        if not os.path.exists(candidate):
            return candidate
    return f"{base}_{int(time.time())}{ext}"


def concat_ffmpeg(
    clips: Sequence[str],
    output_path: str,
    *,
    encoder: str = "cpu",
    target_fps: float = 0.0,
    log: Optional[LogFn] = None,
) -> None:
    if not clips:
        raise ValueError("No clips to concatenate.")
    ffmpeg = find_ffmpeg()
    if not ffmpeg:
        raise RuntimeError("ffmpeg not found (PATH or clip-joiner/ffmpeg.exe).")

    os.makedirs(os.path.dirname(os.path.abspath(output_path)) or ".", exist_ok=True)

    if encoder == "copy" and can_stream_copy_concat(clips):
        _log(log, "FFmpeg: concat demuxer (stream copy)")
        with tempfile.NamedTemporaryFile(
            mode="w", suffix=".txt", delete=False, encoding="utf-8"
        ) as list_file:
            for clip in clips:
                safe = clip.replace("'", "'\\''")
                list_file.write(f"file '{safe}'\n")
            list_path = list_file.name
        try:
            cmd = [
                ffmpeg,
                "-y",
                "-f",
                "concat",
                "-safe",
                "0",
                "-i",
                list_path,
                "-c",
                "copy",
                output_path,
            ]
            _run_ffmpeg(cmd, log)
        finally:
            try:
                os.remove(list_path)
            except OSError:
                pass
        return

    _log(log, f"FFmpeg: filter concat (encoder={encoder})")
    encoder_params = {
        "cpu": ["-c:v", "libx264", "-preset", "fast", "-crf", "18"],
        "cpu_hevc": ["-c:v", "libx265", "-preset", "fast", "-crf", "22"],
        "nvidia_h264": ["-c:v", "h264_nvenc", "-preset", "p6", "-cq", "18"],
        "nvidia_hevc": ["-c:v", "hevc_nvenc", "-preset", "p6", "-cq", "18"],
        "copy": ["-c:v", "libx264", "-preset", "fast", "-crf", "18"],
    }
    vparams = encoder_params.get(encoder, encoder_params["cpu"])

    metas = [probe_video(clip) for clip in clips]
    first_video = _video_stream(metas[0]) or {}
    width, height = _display_size(first_video)
    if width <= 0 or height <= 0:
        width, height = 1920, 1080
    width, height = width - width % 2, height - height % 2
    try:
        from ffmpeg_fps import fps_output_args, normalize_target_fps
        from ffmpeg_progress import run_ffmpeg
    except ImportError:
        sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
        from ffmpeg_fps import fps_output_args, normalize_target_fps
        from ffmpeg_progress import run_ffmpeg
    fps = normalize_target_fps(target_fps) or _fps_of(first_video)
    has_audio = [_audio_stream(m) is not None for m in metas]
    any_audio = any(has_audio)
    _log(log, f"Ziel: {width}x{height} @ {fps:g} fps — abweichende Clips werden eingepasst (schwarze Ränder)")

    inputs: List[str] = []
    for clip in clips:
        inputs.extend(["-i", clip])

    # Different sizes, frame rates or a missing audio track would make concat fail — bring
    # every clip to the first clip's canvas, keep its aspect ratio, and fill silent clips.
    n = len(clips)
    parts: List[str] = []
    labels: List[str] = []
    for i in range(n):
        parts.append(
            f"[{i}:v:0]scale={width}:{height}:force_original_aspect_ratio=decrease,"
            f"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,"
            f"fps={fps:g},format=yuv420p,setpts=PTS-STARTPTS[v{i}]"
        )
        if any_audio:
            if has_audio[i]:
                parts.append(
                    f"[{i}:a:0]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,"
                    f"asetpts=PTS-STARTPTS[a{i}]"
                )
            else:
                dur = _duration_of(metas[i]) or 0.1
                parts.append(f"anullsrc=r=48000:cl=stereo,atrim=duration={dur:.3f},asetpts=PTS-STARTPTS[a{i}]")
            labels.append(f"[v{i}][a{i}]")
        else:
            labels.append(f"[v{i}]")
    if any_audio:
        parts.append(f"{''.join(labels)}concat=n={n}:v=1:a=1[outv][outa]")
        maps = ["-map", "[outv]", "-map", "[outa]"]
    else:
        parts.append(f"{''.join(labels)}concat=n={n}:v=1:a=0[outv]")
        maps = ["-map", "[outv]"]

    cmd = [ffmpeg, "-y", "-hide_banner"] + inputs + ["-filter_complex", ";".join(parts)] + maps + vparams
    cmd.extend(fps_output_args(target_fps))
    cmd.extend(["-c:a", "aac", "-b:a", "256k"] if any_audio else ["-an"])
    if output_path.lower().endswith((".mp4", ".m4v", ".mov")):
        cmd.extend(["-movflags", "+faststart"])
    cmd.append(output_path)
    total = sum(_duration_of(m) for m in metas)
    run_ffmpeg(cmd, total_seconds=total or 1.0, progress=_progress_fn, label="Zusammenfügen", log=None)


# Set by the bridge so long joins show progress in the app.
_progress_fn = None


def set_progress_callback(fn) -> None:
    global _progress_fn
    _progress_fn = fn


def _run_ffmpeg(cmd: List[str], log: Optional[LogFn]) -> None:
    _log(log, " ".join(f'"{c}"' if " " in c else c for c in cmd[:8]) + " …")
    proc = subprocess.Popen(
        cmd,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        universal_newlines=True,
        encoding="utf-8",
        errors="replace",
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
    )
    assert proc.stdout is not None
    tail: List[str] = []
    for line in proc.stdout:
        line = line.rstrip()
        if line:
            tail.append(line)
            if len(tail) > 12:
                tail.pop(0)
    code = proc.wait()
    if code != 0:
        detail = "\n".join(tail[-8:])
        raise RuntimeError(f"FFmpeg failed (exit {code}).\n{detail}")


def concat_davinci(
    clips: Sequence[str],
    output_dir: str,
    output_name: str,
    *,
    preset_name: str,
    timeout_s: float = 3600.0,
    davinci_api_path: str = "",
    log: Optional[LogFn] = None,
) -> None:
    """DaVinci via Subprocess (Hauptthread) — wie Oxco compare.py, nicht GUI-Worker-Thread."""
    if not clips:
        raise ValueError("No clips to concatenate.")

    info0 = probe_media(clips[0])
    w, h = info0.width, info0.height
    fps = pick_timeline_fps_from_clips(clips)
    worker = Path(__file__).resolve().parent / "davinci_worker.py"
    if not worker.is_file():
        raise RuntimeError(f"davinci_worker.py fehlt: {worker}")

    job = {
        "clips": [normalize_media_path(p) for p in clips],
        "output_dir": output_dir,
        "output_name": output_name,
        "davinci_api_path": resolve_davinci_api_path(davinci_api_path),
        "preset_name": preset_name,
        "width": w,
        "height": h,
        "analysis_fps": fps,
        "timeout_s": timeout_s,
    }

    with tempfile.NamedTemporaryFile(
        mode="w", suffix=".json", delete=False, encoding="utf-8"
    ) as jf:
        json.dump(job, jf, ensure_ascii=False, indent=2)
        job_path = jf.name

    _log(log, "DaVinci-Worker starten…")
    env = os.environ.copy()
    enable_python_utf8_mode()
    env["PYTHONUTF8"] = "1"
    env["PYTHONIOENCODING"] = "utf-8"
    try:
        proc = subprocess.Popen(
            [sys.executable, str(worker), job_path],
            cwd=str(worker.parent),
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
            bufsize=1,
            env=env,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        assert proc.stdout is not None
        tail: List[str] = []
        for line in proc.stdout:
            line = line.rstrip()
            if line:
                _log(log, line)
                tail.append(line)
                if len(tail) > 20:
                    tail.pop(0)
        rc = proc.wait()
        if rc != 0:
            detail = "\n".join(tail[-10:])
            raise RuntimeError(
                f"DaVinci-Worker exit {rc}.\n{detail}"
            )
    finally:
        try:
            os.remove(job_path)
        except OSError:
            pass


def run_job(job: JoinJob, settings: JoinSettings, log: Optional[LogFn] = None) -> str:
    clips = normalize_paths(job.files)
    if not clips:
        raise ValueError("Job has no valid video files.")

    out_dir = settings.output_dir.strip() or os.path.dirname(clips[0])
    os.makedirs(out_dir, exist_ok=True)
    stem = (job.output_name or "joined").strip() or "joined"
    ffmpeg_out = unique_output_path(out_dir, stem, ".mp4")

    mode = (settings.mode or "ffmpeg").lower()
    _log(log, f"=== Job: {stem} ({len(clips)} clips, mode={mode}) ===")

    if mode in ("ffmpeg", "both"):
        _log(log, f"FFmpeg → {ffmpeg_out}")
        concat_ffmpeg(
            clips,
            ffmpeg_out,
            encoder=settings.ffmpeg_encoder,
            target_fps=settings.target_fps,
            log=log,
        )
        _log(log, "FFmpeg done.")

    if mode in ("davinci", "both"):
        _log(log, f"DaVinci render → {out_dir}\\{stem}")
        concat_davinci(
            clips,
            out_dir,
            stem,
            preset_name=settings.davinci_preset,
            timeout_s=settings.davinci_timeout_s,
            davinci_api_path=settings.davinci_api_path,
            log=log,
        )
        _log(log, "DaVinci done.")

    return ffmpeg_out if mode in ("ffmpeg", "both") else os.path.join(out_dir, stem)


def run_batch(jobs: Sequence[JoinJob], settings: JoinSettings, log: Optional[LogFn] = None) -> List[str]:
    outputs: List[str] = []
    for i, job in enumerate(jobs, 1):
        _log(log, f"--- Batch {i}/{len(jobs)} ---")
        outputs.append(run_job(job, settings, log=log))
    return outputs
