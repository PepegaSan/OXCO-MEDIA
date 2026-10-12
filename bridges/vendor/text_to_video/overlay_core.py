"""FFmpeg/ffprobe helpers for preview frames and text/subtitle burn-in."""

from __future__ import annotations

import io
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, List, Optional, Sequence, Tuple

from text_render import (  # vendor-Verzeichnis liegt in sys.path
    ANIMATIONS,
    RenderedSprite,
    TextRenderError,
    TimedTextOverlay,
    overlay_has_visible_text,
    render_overlay_sprite,
)


class FFmpegNotFoundError(RuntimeError):
    pass


class ProbeError(RuntimeError):
    pass


def _which_or_raise(names: Tuple[str, ...]) -> str:
    import shutil

    for n in names:
        p = shutil.which(n)
        if p:
            return p
    raise FFmpegNotFoundError(
        "ffmpeg/ffprobe wurde nicht im PATH gefunden. Bitte installieren und PATH setzen."
    )


def find_ffmpeg() -> str:
    return _which_or_raise(("ffmpeg",))


def find_ffprobe() -> str:
    return _which_or_raise(("ffprobe",))


def run_cmd(
    args: List[str],
    *,
    timeout: Optional[float] = None,
    cwd: Optional[str] = None,
) -> Tuple[int, str, str]:
    proc = subprocess.run(
        args,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=timeout,
        cwd=cwd,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
    )
    return proc.returncode, proc.stdout or "", proc.stderr or ""


@dataclass
class VideoInfo:
    path: str
    duration_sec: float
    width: int
    height: int
    fps: float
    #: Gesamt- oder Videostream-Bitrate laut ffprobe (bit/s), oft bei RAW/GIF leer
    source_bitrate_bps: Optional[int] = None


def _parse_positive_int(raw: object) -> Optional[int]:
    try:
        n = int(str(raw).strip())
        return n if n > 0 else None
    except (TypeError, ValueError):
        return None


def _stream_rotation_degrees(stream: Dict[str, Any]) -> int:
    """Read ffprobe display rotation from tags or Display Matrix side data."""
    candidates: List[object] = [(stream.get("tags") or {}).get("rotate")]
    candidates.extend(
        side_data.get("rotation")
        for side_data in (stream.get("side_data_list") or [])
    )
    for raw in candidates:
        try:
            return int(round(float(raw))) % 360
        except (TypeError, ValueError):
            continue
    return 0


def probe_video(video_path: str) -> VideoInfo:
    ffprobe = find_ffprobe()
    code, out, err = run_cmd(
        [
            ffprobe,
            "-v",
            "quiet",
            "-print_format",
            "json",
            "-show_format",
            "-show_streams",
            video_path,
        ],
        timeout=120,
    )
    if code != 0:
        raise ProbeError(err.strip() or out.strip() or "ffprobe fehlgeschlagen.")

    data = json.loads(out)
    dur = float(data.get("format", {}).get("duration") or 0.0)
    w, h = 1920, 1080
    fps = 25.0
    fmt_br = _parse_positive_int(data.get("format", {}).get("bit_rate"))
    stream_br: Optional[int] = None
    for st in data.get("streams") or []:
        if st.get("codec_type") == "video":
            w = int(st.get("width") or w)
            h = int(st.get("height") or h)
            if _stream_rotation_degrees(st) in (90, 270):
                w, h = h, w
            stream_br = _parse_positive_int(st.get("bit_rate"))
            fr = st.get("r_frame_rate") or st.get("avg_frame_rate") or ""
            if "/" in str(fr):
                a, b = str(fr).split("/", 1)
                try:
                    af, bf = float(a), float(b)
                    if bf:
                        fps = af / bf
                except ValueError:
                    pass
            break
    # Videostream-Bitrate bevorzugen (ohne Audio); sonst Format-Gesamtbitrate
    source_br = stream_br if stream_br is not None else fmt_br
    if dur <= 0:
        dur = 1.0
    return VideoInfo(
        path=video_path,
        duration_sec=dur,
        width=w,
        height=h,
        fps=max(fps, 1.0),
        source_bitrate_bps=source_br,
    )


def suggest_ffmpeg_bitrate_from_bps(bps: Optional[int]) -> Optional[str]:
    """Aus ffprobe-Bitrate einen üblichen FFmpeg-Zielwert wie ``8M`` / ``3500k`` bilden."""
    if bps is None or bps <= 0:
        return None
    if bps >= 950_000:
        mb = bps / 1e6
        if mb >= 10:
            return f"{int(round(mb))}M"
        s = f"{mb:.1f}".rstrip("0").rstrip(".")
        return f"{s}M"
    kb = max(1, int(round(bps / 1000)))
    return f"{kb}k"


def escape_filter_path(path: str) -> str:
    """Fallback für seltene Fälle mit vollem Pfad im Filter (möglichst vermeiden).

    Zuverlässiger auf Windows: *_vf_overlay_parts_and_cwd* legt Dateien ins
    Arbeitsverzeichnis und nutzt nur Basisnamen.
    """
    p = Path(path).resolve().as_posix()
    p = p.replace("\\", "/")
    p = p.replace(":", r"\:")
    p = p.replace("'", r"\'")
    p = p.replace(" ", r"\ ")
    return p


__all__ = ["TimedTextOverlay", "RenderedSprite", "render_overlay_sprite"]


def overlay_segment_has_visible_text(overlay: TimedTextOverlay) -> bool:
    return overlay_has_visible_text(overlay)


def _fmt_sec(x: float) -> str:
    s = f"{float(x):.6f}".rstrip("0").rstrip(".")
    return s if s else "0"


def _ordered_window(ov: TimedTextOverlay) -> Tuple[Optional[float], Optional[float]]:
    a, b = ov.text_visible_from_sec, ov.text_visible_to_sec
    if a is not None and b is not None and b < a:
        a, b = b, a
    return a, b


def _overlay_enable_suffix(t_from: Optional[float], t_to: Optional[float]) -> str:
    """FFmpeg *overlay*-Filter ``enable=…`` ; Kommas als ``\\,``."""
    if t_from is None and t_to is None:
        return ""
    if t_from is not None and t_to is not None:
        return f":enable='between(t\\,{_fmt_sec(t_from)}\\,{_fmt_sec(t_to)})'"
    if t_from is not None:
        return f":enable='gte(t\\,{_fmt_sec(t_from)})'"
    assert t_to is not None
    return f":enable='lte(t\\,{_fmt_sec(t_to)})'"


def _sprite_overlay_parts(
    ov: TimedTextOverlay,
    sprite: RenderedSprite,
    video_fps: float,
) -> List[str]:
    """``movie=…`` + ``[in][lbl]overlay=…`` für ein gerendertes Text-Sprite (inkl. Animation)."""
    lbl = f"st{uuid.uuid4().hex[:10]}"
    x = int(ov.pos_x) - sprite.origin_x
    y = int(ov.pos_y) - sprite.origin_y
    t_from, t_to = _ordered_window(ov)
    enable = _overlay_enable_suffix(t_from, t_to)

    anim = ov.animation if ov.animation in ANIMATIONS else "none"
    if t_from is None and t_to is None:
        anim = "none"
    if anim == "none":
        return [
            f"movie={sprite.path.name}[{lbl}]",
            f"[in][{lbl}]overlay={x}:{y}{enable}",
        ]

    d = max(0.05, min(float(ov.anim_duration or 0.4), 5.0))
    if t_from is not None and t_to is not None:
        d = min(d, max(0.05, (t_to - t_from) / 2))
    fps = _fmt_sec(max(1.0, min(float(video_fps or 30.0), 240.0)))
    # Einzelbild endlos wiederholen, damit fade/scale über die Zeit wirken können.
    src = f"movie={sprite.path.name}:loop=0,setpts=N/({fps}*TB),format=rgba"
    if anim == "pop" and t_from is not None:
        p = f"clip((t-{_fmt_sec(t_from)})/{_fmt_sec(d)}\\,0\\,1)"
        k = f"(0.6+0.4*(1-pow(1-{p}\\,3)))"
        src += f",scale=w='max(2\\,iw*{k})':h='max(2\\,ih*{k})':eval=frame"
    if t_from is not None:
        src += f",fade=t=in:st={_fmt_sec(t_from)}:d={_fmt_sec(d)}:alpha=1"
    if t_to is not None:
        src += f",fade=t=out:st={_fmt_sec(max(0.0, t_to - d))}:d={_fmt_sec(d)}:alpha=1"

    x_expr, y_expr = str(x), str(y)
    if anim == "pop":
        # Um die Sprite-Mitte skalieren
        x_expr = f"'{x}+({sprite.width}-w)/2'"
        y_expr = f"'{y}+({sprite.height}-h)/2'"
    elif anim == "slide" and t_from is not None:
        dist = max(8, int(round(int(ov.fontsize) * 0.6)))
        p = f"clip((t-{_fmt_sec(t_from)})/{_fmt_sec(d)}\\,0\\,1)"
        y_expr = f"'{y}+{dist}*pow(1-{p}\\,3)'"
    return [
        f"{src}[{lbl}]",
        f"[in][{lbl}]overlay=x={x_expr}:y={y_expr}:eval=frame:shortest=1{enable}",
    ]


def _append_subtitles_vf_part(parts: List[str], work_dir: Path, srt_path: Optional[str]) -> None:
    if not srt_path or not os.path.isfile(srt_path):
        return
    sf = Path(srt_path).resolve()
    if sf.parent.resolve() == work_dir.resolve():
        parts.append(f"subtitles={sf.name}")
    else:
        sub_copy = work_dir / "_vttool_overlay.srt"
        shutil.copy2(sf, sub_copy)
        parts.append(f"subtitles={sub_copy.name}")


def _vf_parts_use_movie_overlay(parts: Sequence[str]) -> bool:
    """True wenn PNG-Text mit ffmpeg *movie*+*overlay* eingebunden wird (nicht mit einfachem *-vf*)."""
    return any(p.startswith("movie=") for p in parts)


def _compile_vf_parts_to_filter_complex(parts: Sequence[str], trailing: str) -> str:
    """Baut *filter_complex*: ``movie`` erzeugt einen zweiten Zweig — geht nicht mit einfachem *-vf*.

    *trailing* wird direkt an die aktuelle Videokette angehängt und sollte mit einem Filter
    beginnen (z.B. ``scale=960:-2[vout]`` oder ``null[vout]``) und das Ausgabe-Pad ``[vout]``
    enthalten (bei GIF inkl. Palette bis ``[vout]``).
    """
    idx = 0
    cur = "[0:v]"
    chunks: List[str] = []
    linear_buf: List[str] = []
    plist = list(parts)
    i = 0

    def flush_linear() -> None:
        nonlocal cur, linear_buf, idx
        if not linear_buf:
            return
        idx += 1
        lab = f"vt{idx}"
        chunks.append(f"{cur}{','.join(linear_buf)}[{lab}]")
        cur = f"[{lab}]"
        linear_buf.clear()

    while i < len(plist):
        p = plist[i]
        if p.startswith("movie="):
            flush_linear()
            if i + 1 >= len(plist):
                raise ProbeError("Unvollständige Filterkette (movie ohne overlay).")
            ov_line = plist[i + 1].replace("[in]", cur)
            idx += 1
            lab = f"vt{idx}"
            chunks.append(f"{p};{ov_line}[{lab}]")
            cur = f"[{lab}]"
            i += 2
        else:
            linear_buf.append(p)
            i += 1

    flush_linear()

    body = ";".join(chunks)
    joiner = ";" if body else ""
    return f"{body}{joiner}{cur}{trailing}"


def build_timed_overlay_vf_parts(
    work_dir: Path,
    *,
    srt_path: Optional[str],
    overlays: Sequence[TimedTextOverlay],
    play_res_width: int,
    play_res_height: int,
    video_fps: float = 30.0,
) -> List[str]:
    """Filterketten-Segmente (ohne *scale*). Legt Sprite-PNGs in *work_dir* an."""
    parts: List[str] = []
    _append_subtitles_vf_part(parts, work_dir, srt_path)

    for ov in overlays:
        if not overlay_segment_has_visible_text(ov):
            continue
        try:
            sprite = render_overlay_sprite(
                work_dir, ov, max_width=play_res_width, max_height=play_res_height
            )
        except TextRenderError as exc:
            raise ProbeError(str(exc)) from exc
        parts.extend(_sprite_overlay_parts(ov, sprite, video_fps))

    return parts


def extract_frame_png_bytes(
    video_path: str,
    time_sec: float,
    *,
    cached_info: Optional[VideoInfo] = None,
    width_max: int = 1280,
    height_max: int = 720,
) -> bytes:
    """Decode one preview frame to PNG bytes (scaled down, keeps aspect)."""
    ffmpeg = find_ffmpeg()
    info = cached_info if cached_info is not None else probe_video(video_path)
    t = max(0.0, min(time_sec, max(0.0, info.duration_sec - 1e-3)))
    scale_filter = _preview_scale_filter(width_max, height_max)

    args = [
        ffmpeg,
        "-hide_banner",
        "-nostdin",
        "-loglevel",
        "error",
        "-ss",
        f"{t:.3f}",
        "-i",
        video_path,
        "-vf",
        scale_filter,
        "-frames:v",
        "1",
        "-f",
        "image2pipe",
        "-vcodec",
        "png",
        "-",
    ]
    proc = subprocess.run(
        args,
        capture_output=True,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
    )
    if proc.returncode != 0 or not proc.stdout:
        raise ProbeError(
            (proc.stderr or b"").decode("utf-8", errors="replace").strip()
            or "Vorschau-Frame konnte nicht gelesen werden."
        )
    return proc.stdout


def extract_preview_frame(
    video_path: str,
    time_sec: float,
    *,
    srt_path: Optional[str],
    cached_info: Optional[VideoInfo] = None,
    width_max: int = 1280,
    height_max: int = 720,
) -> bytes:
    """Videobild (optional mit eingebrannten SRT-Untertiteln) ohne Text-Overlays.

    Die Text-Overlays legt die App selbst als Sprites darüber — dieselben PNGs wie im Export.
    """
    ffmpeg = find_ffmpeg()
    info = cached_info if cached_info is not None else probe_video(video_path)
    t = max(0.0, min(time_sec, max(0.0, info.duration_sec - 1e-3)))
    sf = srt_path if srt_path and os.path.isfile(srt_path) else None

    work: Optional[Path] = None
    try:
        chain: List[str] = []
        if sf:
            work = Path(tempfile.mkdtemp(prefix="vttool_prev_"))
            # Nach Input-Seek beginnen die Zeitstempel bei 0 — für Untertitel zurückschieben.
            chain.append(f"setpts=PTS+{t:.3f}/TB")
            _append_subtitles_vf_part(chain, work, sf)
        # Wie Export: zuerst yuv420p (Chroma-Subsampling), dann runterskalieren.
        chain.extend(["format=yuv420p", _preview_scale_filter(width_max, height_max), "format=rgb24"])

        args = [
            ffmpeg,
            "-hide_banner",
            "-nostdin",
            "-loglevel",
            "error",
            "-ss",
            f"{t:.3f}",
            "-i",
            video_path,
            "-vf",
            ",".join(chain),
            "-frames:v",
            "1",
            "-f",
            "image2pipe",
            "-vcodec",
            "png",
            "-",
        ]
        proc = subprocess.run(
            args,
            capture_output=True,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            cwd=str(work) if work else None,
        )
        if proc.returncode != 0 or not proc.stdout:
            err = (proc.stderr or b"").decode("utf-8", errors="replace")
            raise ProbeError(err.strip() or "Vorschau-Frame konnte nicht erstellt werden.")
        return proc.stdout
    finally:
        if work is not None:
            shutil.rmtree(work, ignore_errors=True)


def _preview_scale_filter(wmax: int, hmax: int) -> str:
    _ = hmax  # height follows aspect when width is capped
    return f"scale={int(wmax)}:-2"


def map_codec_to_lib(codec_label: str) -> str:
    s = (codec_label or "").strip()
    m = {
        "libx H.264": "libx264",
        "libx H.265": "libx265",
        "VP9 (libvpx)": "libvpx-vp9",
        "AV1 (SVT)": "libsvtav1",
        # legacy labels
        "H.264 (libx264)": "libx264",
        "H.265 / HEVC (libx265)": "libx265",
        "VP9 (libvpx-vp9)": "libvpx-vp9",
        "AV1 (libsvtav1)": "libsvtav1",
        "libx264": "libx264",
        "libx265": "libx265",
        "libvpx-vp9": "libvpx-vp9",
        "libsvtav1": "libsvtav1",
    }
    return m.get(s, "libx264")


def parse_bitrate(s: str) -> str:
    t = (s or "").strip().replace(",", ".")
    if not t:
        return "8M"
    low = t.lower()
    if low.endswith("k") or low.endswith("m") or low.endswith("g"):
        return t
    # bare number → treat as kbps for convenience (like user's bitrate tool readme)
    try:
        n = float(t)
        if n < 100000:
            return f"{int(n)}k"
        return str(int(n))
    except ValueError:
        return "8M"


_TIME_RE = re.compile(r"time=(\d+):(\d+):(\d+\.\d+)")


def parse_time_seconds(stderr_line: str) -> Optional[float]:
    m = _TIME_RE.search(stderr_line)
    if not m:
        return None
    hh, mm, ss = int(m.group(1)), int(m.group(2)), float(m.group(3))
    return hh * 3600 + mm * 60 + ss


def build_gif_palette_vf_suffix(fps: int, max_width: int, palette_colors: int) -> str:
    """Hohe GIF-Qualität: FPS limitieren, skalieren, Palette aus dem gefilterten Video."""
    fp = max(1, min(int(fps), 60))
    mw = max(16, min(int(max_width), 4096))
    ncol = max(8, min(int(palette_colors), 256))
    # Kommas in scale-Ausdrücken für FFmpeg escapen.
    # stats_mode=full: gesamte Frames fließen ins Histogramm — mit diff fehlen oft Farben
    # von statischem Text (z. B. gerasterte PNG-Overlays), dann verschwindet er nach paletteuse.
    return (
        f"fps={fp},scale=min(iw\\,{mw}):-2:flags=lanczos,"
        f"split[s0][s1];[s0]palettegen=max_colors={ncol}:stats_mode=full[p];"
        f"[s1][p]paletteuse=dither=bayer:bayer_scale=5"
    )


def build_export_command(
    *,
    src: str,
    dst: str,
    encoder: str,
    bitrate: str,
    overlays: Sequence[TimedTextOverlay],
    srt_path: Optional[str],
    audio_copy: bool,
    progress_file: Optional[str],
    export_as_gif: bool = False,
    gif_fps: int = 15,
    gif_max_width: int = 720,
    gif_palette_colors: int = 128,
    video_width: int = 1920,
    video_height: int = 1080,
    target_fps: float = 0.0,
    video_fps: float = 30.0,
) -> Tuple[List[str], Optional[str]]:
    """Gibt (ffmpeg-Argumentliste, Arbeitsordner) zurück.

    Der Ordner enthält Schrift-, Untertitel- und Zeilentextdateien und soll nach ffmpeg
    mit ``shutil.rmtree`` entfernt werden.
    """
    ffmpeg = find_ffmpeg()
    sf = srt_path if srt_path and os.path.isfile(srt_path) else None
    need_workspace = bool([o for o in overlays if overlay_segment_has_visible_text(o)]) or bool(
        sf
    )

    vf_arg: Optional[str] = None
    vf_cwd: Optional[str] = None
    ov_parts: List[str] = []
    uses_movie_overlay = False

    if need_workspace:
        work = Path(tempfile.mkdtemp(prefix="vttool_export_"))
        vf_cwd = str(work)
        pw = max(16, min(int(video_width), 8192))
        ph = max(16, min(int(video_height), 8192))
        ov_parts = build_timed_overlay_vf_parts(
            work,
            srt_path=sf,
            overlays=overlays,
            play_res_width=pw,
            play_res_height=ph,
            video_fps=video_fps,
        )
        vf_arg = ",".join(ov_parts) if ov_parts else None
        uses_movie_overlay = bool(ov_parts and _vf_parts_use_movie_overlay(ov_parts))

    args: List[str] = [ffmpeg, "-hide_banner", "-nostdin", "-y", "-i", src]

    if export_as_gif:
        sfx = build_gif_palette_vf_suffix(gif_fps, gif_max_width, gif_palette_colors)
        if vf_arg:
            if uses_movie_overlay:
                tail = f"format=yuv420p,{sfx}[vout]"
                fc = _compile_vf_parts_to_filter_complex(ov_parts, tail)
                args.extend(["-filter_complex", fc, "-map", "[vout]"])
            else:
                vf_merged = f"{vf_arg},format=yuv420p,{sfx}"
                args.extend(["-vf", vf_merged])
        else:
            args.extend(["-vf", sfx])
        args.extend(["-c:v", "gif", "-loop", "0", "-an"])
    else:
        if vf_arg:
            if uses_movie_overlay:
                fc = _compile_vf_parts_to_filter_complex(ov_parts, "null[vout]")
                args.extend(["-filter_complex", fc, "-map", "[vout]", "-map", "0:a?"])
            else:
                args.extend(["-vf", vf_arg])
        args.extend(["-c:v", encoder, "-b:v", bitrate])

        try:
            from ffmpeg_fps import fps_output_args
        except ImportError:
            import sys
            from pathlib import Path as _P
            sys.path.insert(0, str(_P(__file__).resolve().parents[2]))
            from ffmpeg_fps import fps_output_args
        args.extend(fps_output_args(target_fps))

        if encoder == "libx264":
            args.extend(["-profile:v", "high", "-pix_fmt", "yuv420p"])
        elif encoder == "libx265":
            args.extend(["-pix_fmt", "yuv420p"])
        elif encoder == "libvpx-vp9":
            args.extend(["-row-mt", "1", "-pix_fmt", "yuv420p"])
        elif encoder == "libsvtav1":
            args.extend(["-pix_fmt", "yuv420p"])

        if audio_copy:
            args.extend(["-c:a", "copy"])
        else:
            args.extend(["-c:a", "aac", "-b:a", "192k"])

    low = dst.lower()
    if not export_as_gif and low.endswith((".mp4", ".m4v", ".mov")):
        args.extend(["-movflags", "+faststart"])

    if progress_file:
        args.extend(["-progress", progress_file, "-nostats"])

    args.append(dst)
    return args, vf_cwd


def write_temp_textfile(content: str) -> str:
    fd, path = tempfile.mkstemp(prefix="vttext_", suffix=".txt", text=False)
    os.close(fd)
    data = (content or "").replace("\r\n", "\n")
    Path(path).write_text(data, encoding="utf-8")
    return path
