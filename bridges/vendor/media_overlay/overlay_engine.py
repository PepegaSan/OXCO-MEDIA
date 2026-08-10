"""FFmpeg image/video overlay (PiP) with optional audio mix."""
from __future__ import annotations

import json
import shutil
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Optional

LogFn = Callable[[str], None]
ProgressFn = Callable[[float, str], None]

IMAGE_EXTS = {".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff"}


@dataclass
class OverlaySettings:
    main_path: str
    overlay_path: str
    output_path: str
    pos_x: int = 24
    pos_y: int = 24
    width_percent: float = 25.0
    start_seconds: float = 0.0
    end_seconds: float = 0.0  # 0 = until end of main
    main_audio: bool = True
    overlay_audio: bool = False
    ffmpeg_encoder: str = "nvidia_h264"
    target_fps: float = 0.0


def _log(log: Optional[LogFn], msg: str) -> None:
    if log:
        log(msg)


def _progress(progress: Optional[ProgressFn], pct: float, label: str) -> None:
    if progress:
        progress(max(0.0, min(100.0, pct)), label)


def find_ffmpeg() -> Optional[str]:
    return shutil.which("ffmpeg")


def find_ffprobe() -> Optional[str]:
    return shutil.which("ffprobe")


def is_image_path(path: str) -> bool:
    return Path(path).suffix.lower() in IMAGE_EXTS


def default_output_path(main_path: str, output_dir: Optional[str] = None) -> str:
    ip = Path(main_path)
    directory = Path(output_dir) if output_dir else ip.parent
    return str(directory / f"{ip.stem}_overlay{ip.suffix or '.mp4'}")


def encoder_args(encoder: str) -> list[str]:
    table = {
        "cpu": ["-c:v", "libx264", "-preset", "fast", "-crf", "18"],
        "cpu_hevc": ["-c:v", "libx265", "-preset", "fast", "-crf", "22"],
        "nvidia_h264": ["-c:v", "h264_nvenc", "-preset", "p6", "-cq", "18"],
        "nvidia_hevc": ["-c:v", "hevc_nvenc", "-preset", "p6", "-cq", "18"],
    }
    return list(table.get(encoder, table["nvidia_h264"]))


def _run_ffprobe(path: str) -> dict:
    ffprobe = find_ffprobe()
    if not ffprobe:
        raise RuntimeError("ffprobe not found on PATH")
    cmd = [
        ffprobe,
        "-v",
        "quiet",
        "-print_format",
        "json",
        "-show_format",
        "-show_streams",
        path,
    ]
    flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    proc = subprocess.run(
        cmd,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        creationflags=flags,
    )
    if proc.returncode != 0:
        raise RuntimeError(f"ffprobe failed: {(proc.stderr or proc.stdout or '').strip()}")
    return json.loads(proc.stdout or "{}")


def probe_media(path: str) -> tuple[int, int, float, bool, bool]:
    """Return width, height, duration, has_video, has_audio."""
    data = _run_ffprobe(path)
    width = height = 0
    has_video = has_audio = False
    duration = 0.0
    for stream in data.get("streams") or []:
        codec_type = str(stream.get("codec_type") or "")
        if codec_type == "video" and not has_video:
            has_video = True
            try:
                width = int(stream.get("width") or 0)
                height = int(stream.get("height") or 0)
            except (TypeError, ValueError):
                width = height = 0
            try:
                duration = max(duration, float(stream.get("duration") or 0))
            except (TypeError, ValueError):
                pass
        elif codec_type == "audio":
            has_audio = True
    fmt = data.get("format") or {}
    try:
        duration = max(duration, float(fmt.get("duration") or 0))
    except (TypeError, ValueError):
        pass
    if duration <= 0 and is_image_path(path):
        duration = 0.0
    if has_video and (width <= 0 or height <= 0) and is_image_path(path):
        # still images sometimes omit size in odd probes; try again via format
        pass
    return width, height, duration, has_video, has_audio


def _even(n: int) -> int:
    n = max(2, int(n))
    return n - (n % 2)


def _escape_enable(start: float, end: float) -> str:
    # Commas must be escaped for filter_complex / filtergraph
    return f"enable='between(t\\,{start:.3f}\\,{end:.3f})'"


def build_ffmpeg_command(settings: OverlaySettings, *, log: Optional[LogFn] = None) -> list[str]:
    ffmpeg = find_ffmpeg()
    if not ffmpeg:
        raise RuntimeError("ffmpeg not found on PATH")

    main = Path(settings.main_path)
    overlay = Path(settings.overlay_path)
    if not main.is_file():
        raise RuntimeError(f"Main video not found: {main}")
    if not overlay.is_file():
        raise RuntimeError(f"Overlay media not found: {overlay}")

    mw, mh, main_dur, main_has_v, main_has_a = probe_media(str(main))
    if not main_has_v or mw <= 0:
        raise RuntimeError("Main file has no readable video stream.")
    if main_dur <= 0:
        raise RuntimeError("Could not read main video duration.")

    _, _, _, ov_has_v, ov_has_a = probe_media(str(overlay))
    overlay_is_image = is_image_path(str(overlay))
    if not overlay_is_image and not ov_has_v:
        raise RuntimeError("Overlay file has no readable video stream.")

    start = max(0.0, float(settings.start_seconds or 0.0))
    end = float(settings.end_seconds or 0.0)
    if end <= start:
        end = main_dur
    end = min(end, main_dur)
    if end <= start:
        raise RuntimeError("Overlay end must be after start.")

    pct = float(settings.width_percent or 25.0)
    pct = min(100.0, max(1.0, pct))
    scale_w = _even(int(round(mw * (pct / 100.0))))

    pos_x = max(0, int(settings.pos_x))
    pos_y = max(0, int(settings.pos_y))
    enable = _escape_enable(start, end)

    try:
        from ffmpeg_fps import fps_output_args
    except ImportError:
        sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
        from ffmpeg_fps import fps_output_args

    cmd: list[str] = [ffmpeg, "-y", "-hide_banner", "-i", str(main)]
    if overlay_is_image:
        # Loop still image for main duration so overlay timing works
        cmd.extend(["-loop", "1", "-t", f"{main_dur:.3f}", "-i", str(overlay)])
    else:
        cmd.extend(["-i", str(overlay)])

    # Video: scale overlay by width %, keep aspect; pass through when eof
    vf = (
        f"[1:v]scale={scale_w}:-2,format=rgba[ov];"
        f"[0:v][ov]overlay={pos_x}:{pos_y}:{enable}:eof_action=pass[vout]"
    )

    want_main_a = bool(settings.main_audio) and main_has_a
    want_ov_a = (
        bool(settings.overlay_audio)
        and not overlay_is_image
        and ov_has_a
    )

    maps: list[str]
    audio_args: list[str]
    if want_main_a and want_ov_a:
        delay_ms = max(0, int(round(start * 1000.0)))
        af = (
            f"[0:a]aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo,volume=1[a0];"
            f"[1:a]aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo,"
            f"adelay={delay_ms}|{delay_ms},volume=1[a1];"
            f"[a0][a1]amix=inputs=2:duration=first:dropout_transition=2[aout]"
        )
        fc = f"{vf};{af}"
        maps = ["-map", "[vout]", "-map", "[aout]"]
        audio_args = ["-c:a", "aac", "-b:a", "192k"]
        _log(log, f"Audio: main + overlay (mix, delay {delay_ms} ms)")
    elif want_main_a:
        fc = vf
        maps = ["-map", "[vout]", "-map", "0:a:0?"]
        audio_args = ["-c:a", "aac", "-b:a", "192k"]
        _log(log, "Audio: main only")
    elif want_ov_a:
        delay_ms = max(0, int(round(start * 1000.0)))
        af = (
            f"[1:a]aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo,"
            f"adelay={delay_ms}|{delay_ms},apad[aout]"
        )
        fc = f"{vf};{af}"
        maps = ["-map", "[vout]", "-map", "[aout]"]
        audio_args = ["-c:a", "aac", "-b:a", "192k", "-shortest"]
        _log(log, f"Audio: overlay only (delay {delay_ms} ms)")
    else:
        fc = vf
        maps = ["-map", "[vout]"]
        audio_args = ["-an"]
        _log(log, "Audio: muted")

    out = Path(settings.output_path)
    out.parent.mkdir(parents=True, exist_ok=True)

    cmd.extend(["-filter_complex", fc, *maps, *encoder_args(settings.ffmpeg_encoder)])
    cmd.extend(["-pix_fmt", "yuv420p"])
    cmd.extend(fps_output_args(settings.target_fps))
    cmd.extend(audio_args)
    cmd.extend(["-movflags", "+faststart", str(out)])

    _log(
        log,
        f"Overlay {overlay.name} @ {pos_x},{pos_y} scale={pct:g}% "
        f"({scale_w}px) t={start:.2f}-{end:.2f}s -> {out.name}",
    )
    return cmd


def _preview_scale_filter(width_max: int, height_max: int) -> str:
    w = max(160, int(width_max))
    h = max(120, int(height_max))
    return f"scale='min({w}\\,iw)':'min({h}\\,ih)':force_original_aspect_ratio=decrease"


def extract_preview_frame(
    settings: OverlaySettings,
    time_sec: float,
    *,
    width_max: int = 1280,
    height_max: int = 720,
    force_show: bool = True,
) -> tuple[bytes, int, int, float, int, int]:
    """Return PNG bytes + main (w,h,duration) + overlay source (w,h).

    force_show: always burn overlay at this frame (for editing outside start/end).
    """
    ffmpeg = find_ffmpeg()
    if not ffmpeg:
        raise RuntimeError("ffmpeg not found on PATH")

    main = Path(settings.main_path)
    overlay = Path(settings.overlay_path)
    if not main.is_file():
        raise RuntimeError(f"Main video not found: {main}")
    if not overlay.is_file():
        raise RuntimeError(f"Overlay media not found: {overlay}")

    mw, mh, main_dur, main_has_v, _ = probe_media(str(main))
    if not main_has_v or mw <= 0 or mh <= 0:
        raise RuntimeError("Main file has no readable video stream.")
    if main_dur <= 0:
        raise RuntimeError("Could not read main video duration.")

    ow, oh, _, ov_has_v, _ = probe_media(str(overlay))
    overlay_is_image = is_image_path(str(overlay))
    if not overlay_is_image and not ov_has_v:
        raise RuntimeError("Overlay file has no readable video stream.")
    if ow <= 0 or oh <= 0:
        ow, oh = max(2, mw // 4), max(2, mh // 4)

    t = max(0.0, min(float(time_sec), max(0.0, main_dur - 0.04)))
    start = max(0.0, float(settings.start_seconds or 0.0))
    end = float(settings.end_seconds or 0.0)
    if end <= start:
        end = main_dur
    end = min(end, main_dur)

    pct = min(100.0, max(1.0, float(settings.width_percent or 25.0)))
    scale_w = _even(int(round(mw * (pct / 100.0))))
    pos_x = max(0, int(settings.pos_x))
    pos_y = max(0, int(settings.pos_y))

    if force_show:
        overlay_filter = f"[0:v][ov]overlay={pos_x}:{pos_y}:eof_action=pass[comp]"
    else:
        enable = _escape_enable(start, end)
        overlay_filter = f"[0:v][ov]overlay={pos_x}:{pos_y}:{enable}:eof_action=pass[comp]"

    scale_f = _preview_scale_filter(width_max, height_max)
    fc = (
        f"[1:v]scale={scale_w}:-2,format=rgba[ov];"
        f"{overlay_filter};"
        f"[comp]format=yuv420p,{scale_f},format=rgb24[vout]"
    )

    cmd: list[str] = [
        ffmpeg,
        "-hide_banner",
        "-loglevel",
        "error",
        "-ss",
        f"{t:.3f}",
        "-i",
        str(main),
    ]
    if overlay_is_image:
        cmd.extend(["-loop", "1", "-i", str(overlay)])
    else:
        # Align overlay timeline with main: show frame at (t - start)
        ov_t = max(0.0, t - start)
        cmd.extend(["-ss", f"{ov_t:.3f}", "-i", str(overlay)])

    cmd.extend(
        [
            "-filter_complex",
            fc,
            "-map",
            "[vout]",
            "-frames:v",
            "1",
            "-f",
            "image2pipe",
            "-vcodec",
            "png",
            "pipe:1",
        ]
    )

    flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    proc = subprocess.run(
        cmd,
        capture_output=True,
        creationflags=flags,
    )
    if proc.returncode != 0 or not proc.stdout:
        err = (proc.stderr or b"").decode("utf-8", errors="replace").strip()
        raise RuntimeError(err or "preview frame failed")

    return proc.stdout, mw, mh, main_dur, ow, oh


def run_overlay(
    settings: OverlaySettings,
    *,
    log: Optional[LogFn] = None,
    progress: Optional[ProgressFn] = None,
) -> str:
    _progress(progress, 5, "Building command")
    cmd = build_ffmpeg_command(settings, log=log)
    _progress(progress, 15, "Encoding")
    flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    proc = subprocess.run(
        cmd,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        creationflags=flags,
    )
    if proc.returncode != 0:
        err = (proc.stderr or proc.stdout or "").strip()
        enc = settings.ffmpeg_encoder
        if enc.startswith("nvidia") or "nvenc" in " ".join(encoder_args(enc)).lower():
            _log(log, "NVENC failed — retrying with libx264")
            settings_cpu = OverlaySettings(
                main_path=settings.main_path,
                overlay_path=settings.overlay_path,
                output_path=settings.output_path,
                pos_x=settings.pos_x,
                pos_y=settings.pos_y,
                width_percent=settings.width_percent,
                start_seconds=settings.start_seconds,
                end_seconds=settings.end_seconds,
                main_audio=settings.main_audio,
                overlay_audio=settings.overlay_audio,
                ffmpeg_encoder="cpu",
                target_fps=settings.target_fps,
            )
            cmd_cpu = build_ffmpeg_command(settings_cpu, log=log)
            proc = subprocess.run(
                cmd_cpu,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                creationflags=flags,
            )
            if proc.returncode != 0:
                err2 = (proc.stderr or proc.stdout or "").strip()
                raise RuntimeError(err2 or err or "ffmpeg failed")
        else:
            raise RuntimeError(err or "ffmpeg failed")

    _progress(progress, 100, "Done")
    return settings.output_path
