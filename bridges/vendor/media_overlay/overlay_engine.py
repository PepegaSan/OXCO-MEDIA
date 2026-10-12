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
# Animated formats: read as a stream and repeat (the still-image ``-loop 1`` option does not
# exist for the GIF demuxer and made FFmpeg refuse the input).
ANIMATED_EXTS = {".gif"}
# H.264/AAC are not allowed in these containers — write .mp4 instead.
_REMUX_TO_MP4 = {".webm", ".wmv", ".flv", ".ogv", ".3gp", ".mpg", ".mpeg", ".ts", ".m2ts", ".mts", ".vob", ".avi"}


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
    ffmpeg_encoder: str = "cpu"
    target_fps: float = 0.0
    opacity: float = 100.0  # percent
    fade_seconds: float = 0.0  # fade in at start, out at end
    loop_overlay: bool = False  # repeat a short overlay video until the end time


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
    suffix = ip.suffix or ".mp4"
    if suffix.lower() in _REMUX_TO_MP4:
        suffix = ".mp4"
    return str(directory / f"{ip.stem}_overlay{suffix}")


def _overlay_input_args(overlay: str, duration: float, *, loop_video: bool) -> list[str]:
    """Input options so the overlay covers *duration* seconds of the main timeline."""
    suffix = Path(overlay).suffix.lower()
    if suffix in ANIMATED_EXTS or (loop_video and not is_image_path(overlay)):
        return ["-stream_loop", "-1", "-t", f"{duration:.3f}", "-i", overlay]
    if is_image_path(overlay):
        return ["-loop", "1", "-t", f"{duration:.3f}", "-i", overlay]
    return ["-i", overlay]


def _overlay_look(settings: "OverlaySettings", start: float, end: float, *, with_fade: bool) -> str:
    """Opacity and fade filters for the (already rgba) overlay stream."""
    parts: list[str] = []
    opacity = min(100.0, max(0.0, float(settings.opacity if settings.opacity is not None else 100.0)))
    if opacity < 99.5:
        parts.append(f"colorchannelmixer=aa={opacity / 100.0:.3f}")
    fade = max(0.0, float(settings.fade_seconds or 0.0))
    if with_fade and fade > 0:
        fade = min(fade, (end - start) / 2)
        parts.append(f"fade=t=in:st={start:.3f}:d={fade:.3f}:alpha=1")
        parts.append(f"fade=t=out:st={max(start, end - fade):.3f}:d={fade:.3f}:alpha=1")
    return ("," + ",".join(parts)) if parts else ""


def encoder_args(encoder: str) -> list[str]:
    table = {
        "cpu": ["-c:v", "libx264", "-preset", "fast", "-crf", "18"],
        "cpu_hevc": ["-c:v", "libx265", "-preset", "fast", "-crf", "22"],
        "nvidia_h264": ["-c:v", "h264_nvenc", "-preset", "p6", "-cq", "18"],
        "nvidia_hevc": ["-c:v", "hevc_nvenc", "-preset", "p6", "-cq", "18"],
    }
    return list(table.get(encoder, table["cpu"]))


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


def _stream_rotation_degrees(stream: dict) -> int:
    """Read ffprobe display rotation (EXIF/mobile video orientation)."""
    candidates: list[object] = []
    tags = stream.get("tags") or {}
    candidates.append(tags.get("rotate"))
    for side_data in stream.get("side_data_list") or []:
        candidates.append(side_data.get("rotation"))
    for raw in candidates:
        try:
            # Only quarter turns swap the displayed width/height.
            return int(round(float(raw))) % 360
        except (TypeError, ValueError):
            continue
    return 0


def probe_media(path: str) -> tuple[int, int, float, bool, bool]:
    """Return displayed width/height, duration, has_video, has_audio."""
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
                if _stream_rotation_degrees(stream) in (90, 270):
                    width, height = height, width
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


def _scaled_overlay_size(
    main_width: int,
    width_percent: float,
    overlay_width: int,
    overlay_height: int,
) -> tuple[int, int]:
    """Return the exact even pixel size shared by preview, handles, and export."""
    scale_w = _even(int(round(main_width * (width_percent / 100.0))))
    if overlay_width <= 0 or overlay_height <= 0:
        return scale_w, scale_w
    scale_h = _even(int(round(scale_w * overlay_height / overlay_width)))
    return scale_w, scale_h


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

    ow, oh, _, ov_has_v, ov_has_a = probe_media(str(overlay))
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
    scale_w, scale_h = _scaled_overlay_size(mw, pct, ow, oh)

    pos_x = min(max(0, int(settings.pos_x)), max(0, mw - scale_w))
    pos_y = min(max(0, int(settings.pos_y)), max(0, mh - scale_h))
    enable = _escape_enable(start, end)

    try:
        from ffmpeg_fps import fps_output_args
    except ImportError:
        sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
        from ffmpeg_fps import fps_output_args

    cmd: list[str] = [ffmpeg, "-y", "-hide_banner", "-i", str(main)]
    animated = overlay.suffix.lower() in ANIMATED_EXTS
    looped = animated or (settings.loop_overlay and not overlay_is_image)
    # Looped/still inputs run on the main timeline from 0; a plain video starts at *start*.
    cmd.extend(_overlay_input_args(str(overlay), main_dur if overlay_is_image or looped else 0, loop_video=looped))

    # Shift the overlay timeline to the requested start. Without this, a short
    # overlay video may already be at EOF before enable= starts showing it.
    overlay_pts = (
        "setpts=PTS-STARTPTS"
        if overlay_is_image and not animated
        else f"setpts=PTS-STARTPTS+{start:.6f}/TB"
    )
    # Scale by displayed width %, keep display aspect, and normalize SAR.
    vf = (
        f"[1:v]{overlay_pts},scale={scale_w}:{scale_h},setsar=1,format=rgba"
        f"{_overlay_look(settings, start, end, with_fade=True)}[ov];"
        f"[0:v]setsar=1[main];"
        f"[main][ov]overlay={pos_x}:{pos_y}:{enable}:eof_action=pass[vout]"
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
    if out.suffix.lower() in (".mp4", ".m4v", ".mov"):
        cmd.extend(["-movflags", "+faststart"])
    cmd.append(str(out))

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

    ow, oh, overlay_dur, ov_has_v, _ = probe_media(str(overlay))
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
    scale_w, scale_h = _scaled_overlay_size(mw, pct, ow, oh)
    pos_x = min(max(0, int(settings.pos_x)), max(0, mw - scale_w))
    pos_y = min(max(0, int(settings.pos_y)), max(0, mh - scale_h))

    if force_show:
        overlay_filter = f"[0:v][ov]overlay={pos_x}:{pos_y}:eof_action=pass[comp]"
    else:
        enable = _escape_enable(start, end)
        overlay_filter = f"[0:v][ov]overlay={pos_x}:{pos_y}:{enable}:eof_action=pass[comp]"

    scale_f = _preview_scale_filter(width_max, height_max)
    fc = (
        f"[1:v]setpts=PTS-STARTPTS,scale={scale_w}:{scale_h},setsar=1,format=rgba"
        f"{_overlay_look(settings, start, end, with_fade=False)}[ov];"
        f"[0:v]setsar=1[main];"
        f"{overlay_filter.replace('[0:v]', '[main]')};"
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
    if overlay.suffix.lower() in ANIMATED_EXTS:
        cmd.extend(["-stream_loop", "-1", "-i", str(overlay)])
    elif overlay_is_image:
        cmd.extend(["-loop", "1", "-i", str(overlay)])
    else:
        # Align overlay timeline with main. For force-show editing previews,
        # wrap short videos so a valid frame remains visible at later playheads.
        ov_t = max(0.0, t - start)
        if overlay_dur > 0:
            if force_show:
                ov_t %= overlay_dur
            else:
                ov_t = min(ov_t, max(0.0, overlay_dur - 0.10))
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
    try:
        from ffmpeg_progress import run_ffmpeg
    except ImportError:
        sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
        from ffmpeg_progress import run_ffmpeg

    _progress(progress, 2, "Building command")
    cmd = build_ffmpeg_command(settings, log=log)
    _, _, main_dur, _, _ = probe_media(settings.main_path)
    try:
        run_ffmpeg(cmd, total_seconds=main_dur or 1.0, progress=progress, label="Encoding")
    except RuntimeError as exc:
        enc = settings.ffmpeg_encoder
        if not (enc.startswith("nvidia") or "nvenc" in " ".join(encoder_args(enc)).lower()):
            _remove_partial(settings.output_path)
            raise
        _log(log, f"NVENC failed — retrying with libx264 ({str(exc).splitlines()[0]})")
        from dataclasses import replace

        cmd_cpu = build_ffmpeg_command(replace(settings, ffmpeg_encoder="cpu"), log=log)
        try:
            run_ffmpeg(cmd_cpu, total_seconds=main_dur or 1.0, progress=progress, label="Encoding")
        except RuntimeError:
            _remove_partial(settings.output_path)
            raise

    _progress(progress, 100, "Done")
    return settings.output_path


def _remove_partial(path: str) -> None:
    """A failed encode leaves a broken file behind — don't let it look like a result."""
    try:
        Path(path).unlink(missing_ok=True)
    except OSError:
        pass
