"""Vendored copy of Intro Cutter FFmpeg helpers — original repo unverändert."""
from __future__ import annotations

import os
import shutil
import subprocess
from pathlib import Path
from typing import Callable, Optional

LogFn = Optional[Callable[[str], None]]


def _log(fn: LogFn, msg: str) -> None:
    if fn:
        fn(msg)


def _which(name: str) -> Optional[str]:
    return shutil.which(name)


def ffprobe_duration_sec(path: Path, log: LogFn = None) -> float:
    exe = _which("ffprobe")
    if not exe:
        raise RuntimeError("ffprobe nicht im PATH. FFmpeg installieren und PATH setzen.")
    try:
        out = subprocess.check_output(
            [
                exe,
                "-v",
                "error",
                "-show_entries",
                "format=duration",
                "-of",
                "default=noprint_wrappers=1:nokey=1",
                str(path),
            ],
            stderr=subprocess.STDOUT,
            timeout=120,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except (OSError, subprocess.CalledProcessError, subprocess.TimeoutExpired) as exc:
        raise RuntimeError(f"ffprobe fehlgeschlagen: {exc}") from exc
    text = out.decode("utf-8", errors="replace").strip()
    try:
        return float(text.replace(",", "."))
    except ValueError as exc:
        raise RuntimeError(f"Ungültige Dauer von ffprobe: {text!r}") from exc


def _ffprobe_text(
    path: Path,
    *,
    select_streams: Optional[str],
    show_entries: str,
) -> str:
    exe = _which("ffprobe")
    if not exe:
        raise RuntimeError("ffprobe nicht im PATH.")
    cmd = [exe, "-v", "error"]
    if select_streams:
        cmd.extend(["-select_streams", select_streams])
    cmd.extend(
        [
            "-show_entries",
            show_entries,
            "-of",
            "default=noprint_wrappers=1:nokey=1",
            str(path),
        ]
    )
    try:
        out = subprocess.check_output(
            cmd,
            stderr=subprocess.STDOUT,
            timeout=120,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except (OSError, subprocess.CalledProcessError, subprocess.TimeoutExpired) as exc:
        raise RuntimeError(f"ffprobe fehlgeschlagen: {exc}") from exc
    return out.decode("utf-8", errors="replace").strip()


def _parse_bitrate_field(text: str) -> Optional[int]:
    line = (text or "").strip().split("\n", 1)[0].strip()
    if not line or line.upper() == "N/A":
        return None
    try:
        v = int(float(line))
    except ValueError:
        return None
    return v if v > 1000 else None


def ffprobe_video_bitrate_bps(path: Path, log: LogFn = None) -> Optional[int]:
    try:
        raw = _ffprobe_text(path, select_streams="v:0", show_entries="stream=bit_rate")
        b = _parse_bitrate_field(raw)
        if b:
            _log(log, f"Video-Bitrate (Stream): {b} bit/s (~{b / 1_000_000:.2f} Mbit/s)")
            return b
    except RuntimeError:
        pass

    try:
        raw = _ffprobe_text(path, select_streams=None, show_entries="format=bit_rate")
        b = _parse_bitrate_field(raw)
        if b:
            _log(log, f"Video-Bitrate (Format): {b} bit/s (~{b / 1_000_000:.2f} Mbit/s)")
            return b
    except RuntimeError:
        pass

    try:
        dur = ffprobe_duration_sec(path, log)
        raw = _ffprobe_text(path, select_streams=None, show_entries="format=size")
        size = _parse_bitrate_field(raw)
        if size and dur > 0.01:
            est = int((size * 8) / dur)
            if est > 10_000:
                _log(log, f"Video-Bitrate geschätzt: {est} bit/s")
                return est
    except (RuntimeError, ValueError, ZeroDivisionError):
        pass

    return None


def has_audio_stream(path: Path) -> bool:
    exe = _which("ffprobe")
    if not exe:
        return False
    try:
        subprocess.check_call(
            [
                exe,
                "-v",
                "error",
                "-select_streams",
                "a:0",
                "-show_entries",
                "stream=index",
                "-of",
                "csv=p=0",
                str(path),
            ],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            timeout=60,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        return True
    except (OSError, subprocess.CalledProcessError, subprocess.TimeoutExpired):
        return False


def resolve_output_directory(custom_dir: Optional[str], src: Path) -> Path:
    if not (custom_dir or "").strip():
        return src.parent
    out = Path(str(custom_dir).strip()).expanduser().resolve()
    out.mkdir(parents=True, exist_ok=True)
    return out


def output_extension(video_codec: str, src_path: Path) -> str:
    if video_codec.strip().lower() == "copy":
        return src_path.suffix or ".mp4"
    c = video_codec.lower()
    if "vp9" in c or "vpx" in c:
        return ".webm"
    if "av1" in c or "svtav1" in c or "aom" in c:
        return ".mkv"
    return ".mp4"


def run_ffmpeg_cut(
    src: Path,
    intro_sec: float,
    outro_sec: float,
    video_codec: str,
    video_bitrate: str,
    audio_codec: str,
    audio_bitrate: str,
    *,
    video_bitrate_auto: bool = False,
    output_dir: Optional[str] = None,
    target_fps: Optional[float] = None,
    log: LogFn = None,
    progress=None,
) -> Path:
    if intro_sec < 0 or outro_sec < 0:
        raise ValueError("Intro und Outro muessen >= 0 sein.")
    ffmpeg = _which("ffmpeg")
    if not ffmpeg:
        raise RuntimeError("ffmpeg nicht im PATH.")

    dur = ffprobe_duration_sec(src, log)
    new_dur = dur - intro_sec - outro_sec
    if new_dur <= 0:
        raise RuntimeError(
            f"Ergebnis zu kurz: Gesamtdauer {dur:.3f}s, Intro {intro_sec}s, Outro {outro_sec}s."
        )

    ext = output_extension(video_codec, src)
    out_base = resolve_output_directory(output_dir, src)
    out_path = _unique_path(out_base / f"{src.stem}_introcut{ext}")

    _log(log, f"Quelle: {src}")
    _log(log, f"Dauer: {dur:.3f}s → neu {new_dur:.3f}s")
    _log(log, f"Ziel: {out_path}")

    v_codec = video_codec.strip()
    if v_codec.lower() == "copy":
        v_args = ["-c:v", "copy"]
    else:
        if video_bitrate_auto:
            bps = ffprobe_video_bitrate_bps(src, log)
            if not bps:
                raise RuntimeError("Automatische Video-Bitrate fehlgeschlagen.")
            v_args = ["-c:v", v_codec, "-b:v", str(bps)]
        else:
            if not (video_bitrate or "").strip():
                raise RuntimeError("Video-Bitrate fehlt.")
            v_args = ["-c:v", v_codec, "-b:v", video_bitrate.strip()]

    if has_audio_stream(src):
        a_codec = audio_codec.strip()
        if ext == ".webm" and a_codec.lower() not in ("libopus", "libvorbis"):
            # WebM only allows Opus/Vorbis audio — AAC or a copied AAC track would fail.
            _log(log, "WebM-Ausgabe: Audio wird als Opus kodiert")
            a_codec = "libopus"
        if a_codec.lower() == "copy":
            a_args = ["-map", "0:a:0", "-c:a", "copy"]
        else:
            a_args = ["-map", "0:a:0", "-c:a", a_codec, "-b:a", audio_bitrate.strip()]
    else:
        a_args = ["-an"]

    mov = []
    if ext.lower() in (".mp4", ".m4v", ".mov"):
        mov = ["-movflags", "+faststart"]

    try:
        from ffmpeg_fps import fps_output_args
    except ImportError:
        import sys
        from pathlib import Path as _P
        sys.path.insert(0, str(_P(__file__).resolve().parents[2]))
        from ffmpeg_fps import fps_output_args

    # FPS override only when re-encoding (copy can't change rate)
    fps_args = [] if v_codec.lower() == "copy" else fps_output_args(target_fps)

    try:
        from ffmpeg_progress import run_ffmpeg
    except ImportError:
        import sys
        from pathlib import Path as _P
        sys.path.insert(0, str(_P(__file__).resolve().parents[2]))
        from ffmpeg_progress import run_ffmpeg

    if v_codec.lower() == "copy":
        _log(log, "Copy-Modus: Schnitt landet auf dem nächsten Keyframe vor dem Intro-Ende")

    # Seek in front of -i: FFmpeg jumps to the intro end instead of decoding the intro
    # (frame-accurate when re-encoding).
    args = [
        ffmpeg,
        "-hide_banner",
        "-y",
        "-ss",
        f"{intro_sec:.6f}",
        "-t",
        f"{new_dur:.6f}",
        "-i",
        str(src),
        "-map",
        "0:v:0",
    ] + a_args + v_args + fps_args + mov + [str(out_path)]

    run_ffmpeg(args, total_seconds=new_dur, progress=progress, label="Intro/Outro", log=log)
    return out_path


def _unique_path(base: Path) -> Path:
    """Never overwrite an earlier result: name_introcut.mp4 → name_introcut_2.mp4 …"""
    if not base.exists():
        return base
    for n in range(2, 10000):
        candidate = base.with_name(f"{base.stem}_{n}{base.suffix}")
        if not candidate.exists():
            return candidate
    return base
