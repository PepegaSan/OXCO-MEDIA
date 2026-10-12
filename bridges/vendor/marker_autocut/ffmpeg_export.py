"""
Stash-Marker → FFmpeg: Schnitte ohne DaVinci, auch bei gemischten Formaten/FPS.

Ablauf: jeder Marker wird einzeln (schneller Seek pro Segment) auf **ein gemeinsames Zielformat**
gerechnet — gleiche Auflösung, Bildrate, Pixelformat, Audio 48 kHz Stereo (Stille, wenn die Quelle
keinen Ton hat). Danach hängt der Concat-Demuxer die Teile **ohne zweites Encoding** aneinander.
So entsteht nur ein Encoding pro Bild, und beliebig viele Marker brauchen nicht beliebig viel RAM.
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
from collections import Counter, defaultdict
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional, Sequence, Tuple

from marker_math import (
    clamp_marker_range_to_duration,
    duration_seconds,
    rows_to_timeline_segments,
    safe_filename_stem,
)

LogFn = Optional[Callable[[str], None]]
ProgressFn = Optional[Callable[[float, str], None]]

# Gängige Bildraten: FFmpeg bekommt die exakte Bruchform (NTSC-Raten nicht als 29.97 runden).
STANDARD_FPS: Tuple[Tuple[float, str], ...] = (
    (23.976, "24000/1001"),
    (24.0, "24"),
    (25.0, "25"),
    (29.97, "30000/1001"),
    (30.0, "30"),
    (50.0, "50"),
    (59.94, "60000/1001"),
    (60.0, "60"),
)
STANDARD_SHORT_SIDES = (360, 480, 540, 720, 1080, 1440, 2160)

RESOLUTION_PRESETS: Dict[str, Tuple[int, int]] = {
    "2160p": (3840, 2160),
    "1440p": (2560, 1440),
    "1080p": (1920, 1080),
    "720p": (1280, 720),
    "1080x1920": (1080, 1920),
    "720x1280": (720, 1280),
}

ENCODERS: Dict[str, List[str]] = {
    "cpu": ["-c:v", "libx264", "-preset", "medium", "-crf", "20"],
    "cpu_hevc": ["-c:v", "libx265", "-preset", "medium", "-crf", "23", "-tag:v", "hvc1"],
    "nvidia_h264": ["-c:v", "h264_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", "21", "-b:v", "0"],
    "nvidia_hevc": ["-c:v", "hevc_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", "23", "-b:v", "0", "-tag:v", "hvc1"],
}


@dataclass
class SourceInfo:
    path: str
    width: int
    height: int
    fps: float
    has_audio: bool
    duration: float


@dataclass
class Segment:
    path: str
    start: float
    duration: float


@dataclass
class Target:
    width: int
    height: int
    fps_text: str

    @property
    def fps(self) -> float:
        num, _, den = self.fps_text.partition("/")
        return float(num) / float(den or 1)

    def describe(self) -> str:
        return f"{self.width}×{self.height} @ {self.fps:.3f}".rstrip("0").rstrip(".") + " fps"


def _log(cb: LogFn, msg: str) -> None:
    if cb:
        cb(msg)


def _no_window() -> int:
    return getattr(subprocess, "CREATE_NO_WINDOW", 0) if sys.platform == "win32" else 0


def find_ffmpeg() -> str:
    exe = shutil.which("ffmpeg")
    if not exe:
        raise RuntimeError("FFmpeg nicht gefunden (PATH).")
    return exe


def find_ffprobe() -> str:
    exe = shutil.which("ffprobe")
    if not exe:
        raise RuntimeError("ffprobe nicht gefunden (PATH).")
    return exe


# ---------------------------------------------------------------------------
# Quellen analysieren
# ---------------------------------------------------------------------------


def _rate(raw: Any) -> float:
    s = str(raw or "")
    num, _, den = s.partition("/")
    try:
        value = float(num) / float(den or 1)
    except (ValueError, ZeroDivisionError):
        return 0.0
    return value


def probe_source(path: str) -> SourceInfo:
    try:
        out = subprocess.check_output(
            [find_ffprobe(), "-v", "quiet", "-print_format", "json", "-show_streams", "-show_format", path],
            stderr=subprocess.DEVNULL,
            timeout=60,
            creationflags=_no_window(),
        )
        meta = json.loads(out.decode("utf-8", errors="replace"))
    except (OSError, subprocess.SubprocessError, json.JSONDecodeError) as exc:
        raise RuntimeError(f"ffprobe konnte {Path(path).name} nicht lesen: {exc}") from exc

    streams = meta.get("streams") or []
    video = next((s for s in streams if s.get("codec_type") == "video"
                  and not (s.get("disposition") or {}).get("attached_pic")), None)
    if video is None:
        raise RuntimeError(f"Keine Videospur: {Path(path).name}")
    width, height = int(video.get("width") or 0), int(video.get("height") or 0)
    for side in video.get("side_data_list") or []:
        try:
            if int(round(float(side.get("rotation", 0)))) % 180 == 90:
                width, height = height, width
        except (TypeError, ValueError):
            pass

    # avg_frame_rate ist bei Handy-VFR verlässlicher; r_frame_rate kann 90000/1 o. Ä. melden.
    fps = _rate(video.get("avg_frame_rate"))
    if not 1.0 <= fps <= 240.0:
        fps = _rate(video.get("r_frame_rate"))
    if not 1.0 <= fps <= 240.0:
        fps = 0.0

    try:
        duration = float((meta.get("format") or {}).get("duration") or video.get("duration") or 0.0)
    except (TypeError, ValueError):
        duration = 0.0

    return SourceInfo(
        path=path,
        width=width,
        height=height,
        fps=fps,
        has_audio=any(s.get("codec_type") == "audio" for s in streams),
        duration=max(0.0, duration),
    )


# ---------------------------------------------------------------------------
# Zielformat bestimmen
# ---------------------------------------------------------------------------


def snap_fps(fps: float) -> str:
    """Nächste Standard-Bildrate (±3 %), sonst auf 2 Nachkommastellen gerundet."""
    if fps <= 0:
        return "30"
    best = min(STANDARD_FPS, key=lambda item: abs(item[0] - fps))
    if abs(best[0] - fps) / fps <= 0.03:
        return best[1]
    return f"{min(fps, 120.0):.2f}".rstrip("0").rstrip(".")


def _even(value: float) -> int:
    return max(2, int(round(value / 2.0)) * 2)


def _snap_short_side(short: int) -> int:
    fits = [s for s in STANDARD_SHORT_SIDES if s <= short * 1.05]
    return fits[-1] if fits else _even(short)


def _weighted_mode(weights: Dict[Any, float]) -> Any:
    # Bei Gleichstand gewinnt der größere Wert (lieber 1080p als 720p).
    return max(weights.items(), key=lambda kv: (kv[1], kv[0]))[0]


def _first_mode(weights: Counter) -> Any:
    """Häufigster Wert; bei Gleichstand der zuerst vorkommende (wie DaVinci: erstes Segment)."""
    best = max(weights.values())
    return next(k for k, v in weights.items() if v == best)


def _auto_short_side(shorts: Counter) -> int:
    """Höchste Auflösung, die mindestens ein Viertel der Gesamtlänge ausmacht.

    So wird gutes Material nicht verkleinert, aber ein einzelner 4K-Clip bläht nicht alles auf.
    """
    if not shorts:
        return 1080
    total = sum(shorts.values())
    for short in sorted(shorts, reverse=True):
        if shorts[short] >= total * 0.25:
            return short
    return _weighted_mode(shorts)


def pick_target(
    segments: Sequence[Segment],
    sources: Dict[str, SourceInfo],
    *,
    resolution: str = "auto",
    fps: str = "auto",
    default_fps: float = 30.0,
) -> Target:
    """Auto: Ausrichtung/FPS nach Mehrheit (nach Länge gewichtet), Auflösung siehe _auto_short_side."""
    landscape_w = portrait_w = 0.0
    shorts: Counter = Counter()
    rates: Counter = Counter()
    for seg in segments:
        src = sources[seg.path]
        weight = max(0.01, seg.duration)
        if src.width >= src.height:
            landscape_w += weight
        else:
            portrait_w += weight
        shorts[_snap_short_side(min(src.width, src.height))] += weight
        rates[snap_fps(src.fps or default_fps)] += weight

    key = (resolution or "auto").strip().lower()
    if key in RESOLUTION_PRESETS:
        width, height = RESOLUTION_PRESETS[key]
    else:
        short = _auto_short_side(shorts)
        long_side = _even(short * 16 / 9)
        width, height = (long_side, short) if landscape_w >= portrait_w else (short, long_side)

    fps_key = (fps or "auto").strip().lower()
    if fps_key in ("", "auto"):
        fps_text = _first_mode(rates) if rates else snap_fps(default_fps)
    else:
        try:
            fps_text = snap_fps(float(fps_key.replace(",", ".")))
        except ValueError:
            fps_text = _first_mode(rates) if rates else "30"
    return Target(width=width, height=height, fps_text=fps_text)


def source_target(src: SourceInfo, default_fps: float) -> Target:
    """Pro-Datei-Export: Format der Quelle beibehalten."""
    return Target(width=_even(src.width), height=_even(src.height), fps_text=snap_fps(src.fps or default_fps))


# ---------------------------------------------------------------------------
# FFmpeg-Befehle
# ---------------------------------------------------------------------------


def video_filter(target: Target, fit: str) -> str:
    """Filterkette [0:v] → [v] auf das Zielformat (Seitenverhältnis bleibt erhalten)."""
    w, h = target.width, target.height
    tail = f"fps={target.fps_text},format=yuv420p,setsar=1"
    if fit == "crop":
        return f"[0:v]scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h},{tail}[v]"
    if fit == "blur":
        bw, bh = _even(w / 8), _even(h / 8)
        return (
            f"[0:v]split[bgsrc][fgsrc];"
            f"[bgsrc]scale={bw}:{bh}:force_original_aspect_ratio=increase,crop={bw}:{bh},"
            f"boxblur=6:2,scale={w}:{h}[bg];"
            f"[fgsrc]scale={w}:{h}:force_original_aspect_ratio=decrease:force_divisible_by=2[fg];"
            f"[bg][fg]overlay=(W-w)/2:(H-h)/2,{tail}[v]"
        )
    return (
        f"[0:v]scale={w}:{h}:force_original_aspect_ratio=decrease:force_divisible_by=2,"
        f"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:color=black,{tail}[v]"
    )


def frame_exact_duration(seconds: float, target: Target) -> float:
    """Länge auf ganze Zielbilder runden — Video und Audio enden dann exakt gleich (keine Naht-Lücken)."""
    return max(1, round(seconds * target.fps)) / target.fps


def segment_command(seg: Segment, src: SourceInfo, target: Target, fit: str, encoder: str, out_path: str) -> List[str]:
    exact = frame_exact_duration(seg.duration, target)
    cmd = [find_ffmpeg(), "-hide_banner", "-y", "-ss", f"{seg.start:.3f}", "-t", f"{seg.duration:.3f}", "-i", seg.path]
    if src.has_audio:
        audio_chain = "[0:a:0]aresample=48000:async=1:first_pts=0,aformat=channel_layouts=stereo,apad[a]"
    else:
        cmd += ["-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo"]
        audio_chain = "[1:a]anull[a]"
    cmd += [
        "-filter_complex", video_filter(target, fit) + ";" + audio_chain,
        "-map", "[v]", "-map", "[a]",
        "-frames:v", str(max(1, round(seg.duration * target.fps))),
        "-t", f"{exact:.6f}",
        *ENCODERS.get(encoder, ENCODERS["cpu"]),
        "-r", target.fps_text,
        "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2",
        "-video_track_timescale", "90000",
        "-avoid_negative_ts", "make_zero",
        "-map_metadata", "-1",
        out_path,
    ]
    return cmd


def _run(cmd: List[str], *, total: float, on_progress: Callable[[float], None]) -> None:
    """FFmpeg mit -progress; stderr in Datei (kein Pipe-Deadlock), Fehler mit stderr-Ende."""
    full = cmd[:-1] + ["-progress", "pipe:1", "-nostats", cmd[-1]]
    err_fd, err_path = tempfile.mkstemp(prefix="hm_marker_", suffix=".log")
    os.close(err_fd)
    try:
        with open(err_path, "wb") as err:
            proc = subprocess.Popen(
                full, stdout=subprocess.PIPE, stderr=err, stdin=subprocess.DEVNULL,
                text=True, encoding="utf-8", errors="replace", creationflags=_no_window(),
            )
            assert proc.stdout is not None
            total_us = max(1.0, total * 1_000_000.0)
            for line in proc.stdout:
                key, _, value = line.strip().partition("=")
                if key == "out_time_us" and value.isdigit():
                    on_progress(min(1.0, int(value) / total_us))
            code = proc.wait()
        if code != 0:
            with open(err_path, "r", encoding="utf-8", errors="replace") as fh:
                tail = fh.read()[-1200:]
            raise RuntimeError(f"ffmpeg fehlgeschlagen (Code {code}):\n{tail}")
    finally:
        try:
            os.unlink(err_path)
        except OSError:
            pass


def _unique_path(directory: str, stem: str, ext: str = ".mp4") -> str:
    candidate = os.path.join(directory, stem + ext)
    n = 2
    while os.path.exists(candidate):
        candidate = os.path.join(directory, f"{stem}_{n}{ext}")
        n += 1
    return candidate


def render_segments(
    segments: Sequence[Segment],
    sources: Dict[str, SourceInfo],
    target: Target,
    *,
    fit: str,
    encoder: str,
    out_path: str,
    progress: Callable[[float, str], None],
    log: LogFn,
) -> str:
    """Segmente normalisieren → Concat ohne Re-Encode → *out_path*."""
    out_dir = os.path.dirname(out_path) or "."
    work = tempfile.mkdtemp(prefix=".hm_marker_", dir=out_dir)
    total = sum(s.duration for s in segments) or 1.0
    try:
        parts: List[str] = []
        done = 0.0
        for i, seg in enumerate(segments, start=1):
            part = os.path.join(work, f"part_{i:04d}.mp4")
            label = f"Clip {i}/{len(segments)}"
            base = done

            def on_part(frac: float, base: float = base, seg: Segment = seg, label: str = label) -> None:
                progress((base + frac * seg.duration) / total * 97.0, label)

            _run(segment_command(seg, sources[seg.path], target, fit, encoder, part), total=seg.duration, on_progress=on_part)
            parts.append(part)
            done += seg.duration
            progress(done / total * 97.0, label)

        list_path = os.path.join(work, "list.txt")
        with open(list_path, "w", encoding="utf-8") as fh:
            for part, seg in zip(parts, segments):
                fh.write("file '" + part.replace("\\", "/").replace("'", "'\\''") + "'\n")
                # AAC-Padding macht jede Teildatei minimal länger als ihr Video → feste Dauer pro Teil.
                fh.write(f"duration {frame_exact_duration(seg.duration, target):.6f}\n")

        progress(98.0, "Zusammenfügen")
        tmp_out = os.path.join(work, "joined.mp4")
        _run(
            [find_ffmpeg(), "-hide_banner", "-y", "-f", "concat", "-safe", "0", "-i", list_path,
             "-map", "0", "-c", "copy", "-movflags", "+faststart", tmp_out],
            total=total,
            on_progress=lambda f: None,
        )
        os.replace(tmp_out, out_path)
        progress(100.0, "Fertig")
        return out_path
    finally:
        shutil.rmtree(work, ignore_errors=True)


# ---------------------------------------------------------------------------
# Einstieg (gleiche Zeilen/Optionen wie der Resolve-Export)
# ---------------------------------------------------------------------------


def build_segments(
    rows: List[Dict[str, str]],
    options: Dict[str, Any],
    path_kwargs: Dict[str, Any],
    log: LogFn,
) -> Tuple[List[Segment], Dict[str, SourceInfo]]:
    min_seg = float(options.get("min_segment_seconds", 1.0))
    default_fps = float(options.get("default_fps", 25.0))
    raw, not_found = rows_to_timeline_segments(
        rows,
        min_segment_seconds=min_seg,
        inclusive_end=bool(options.get("inclusive_end", True)),
        default_fps=default_fps,
        **path_kwargs,
    )
    if not_found:
        uniq = list(dict.fromkeys(not_found))
        _log(log, f"Hinweis: {len(not_found)} Marker mit nicht auffindbarem Pfad, z. B. {uniq[:3]}")
    if not raw:
        raise RuntimeError(
            "Keine Mediendatei gefunden. Pfad-Zuordnung (NAS → Windows) oder Medien-Stammordner prüfen."
        )

    sources: Dict[str, SourceInfo] = {}
    for path, *_ in raw:
        if path not in sources:
            sources[path] = probe_source(path)

    segments: List[Segment] = []
    for path, s0, s1 in raw:
        src = sources[path]
        fps = src.fps or default_fps
        start, end, clamped = clamp_marker_range_to_duration(s0, s1, src.duration)
        dur = duration_seconds(start, end, fps, min_seg)
        if src.duration > 0:
            dur = min(dur, src.duration - start)
        if dur < 1.0 / fps:
            _log(log, f"Übersprungen (liegt hinter dem Dateiende): {Path(path).name} @ {s0:.2f}s")
            continue
        if clamped:
            _log(log, f"Marker auf Dateilänge gekürzt: {Path(path).name} @ {s0:.2f}s")
        segments.append(Segment(path=path, start=start, duration=dur))
    if not segments:
        raise RuntimeError("Keine gültigen Segmente (alle Marker außerhalb der Dateilänge?).")
    return segments, sources


def run_ffmpeg_export(
    mode: str,
    rows: List[Dict[str, str]],
    options: Dict[str, Any],
    path_kwargs: Dict[str, Any],
    log: LogFn,
    progress: ProgressFn = None,
) -> List[str]:
    """mode: 'per_file' | 'compilation'. Gibt die geschriebenen Dateien zurück."""
    report = progress or (lambda pct, label: None)
    out_dir = str(options.get("output_dir") or "").strip() or os.path.expanduser("~")
    os.makedirs(out_dir, exist_ok=True)
    prefix = str(options.get("output_name_prefix") or "StashMarker")
    fit = str(options.get("ffmpeg_fit") or "blur").strip().lower()
    encoder = str(options.get("ffmpeg_encoder") or "cpu").strip()
    resolution = str(options.get("ffmpeg_resolution") or "auto")
    fps = str(options.get("ffmpeg_fps") or "auto")
    default_fps = float(options.get("default_fps", 25.0))

    report(0.0, "Quellen analysieren")
    segments, sources = build_segments(rows, options, path_kwargs, log)
    stamp = time.strftime("%Y%m%d_%H%M%S")

    if mode == "compilation":
        target = pick_target(segments, sources, resolution=resolution, fps=fps, default_fps=default_fps)
        formats = sorted({(s.width, s.height, snap_fps(s.fps or default_fps)) for s in sources.values()})
        _log(log, f"{len(segments)} Clip(s) aus {len(sources)} Datei(en), Quellformate: "
                  + ", ".join(f"{w}×{h}@{r}" for w, h, r in formats))
        _log(log, f"Zielformat: {target.describe()} — Anpassung: {fit}, Encoder: {encoder}")
        out = _unique_path(out_dir, f"{prefix}_compilation_{stamp}")
        render_segments(segments, sources, target, fit=fit, encoder=encoder, out_path=out, progress=report, log=log)
        _log(log, f"Fertig: {out}")
        return [out]

    if mode != "per_file":
        raise ValueError(f"Unbekannter Modus: {mode}")

    by_file: Dict[str, List[Segment]] = defaultdict(list)
    for seg in segments:
        by_file[seg.path].append(seg)
    outputs: List[str] = []
    files = list(by_file.items())
    for idx, (path, segs) in enumerate(files):
        src = sources[path]
        if resolution.strip().lower() == "auto" and fps.strip().lower() == "auto":
            target = source_target(src, default_fps)
        else:
            target = pick_target(segs, sources, resolution=resolution, fps=fps, default_fps=default_fps)
        name = Path(path).name
        _log(log, f"[{idx + 1}/{len(files)}] {name}: {len(segs)} Clip(s) → {target.describe()}")

        def scoped(pct: float, label: str, idx: int = idx) -> None:
            report((idx + pct / 100.0) / len(files) * 100.0, f"Video {idx + 1}/{len(files)} · {label}")

        out = _unique_path(out_dir, f"{prefix}_{safe_filename_stem(path, 'clip')}_{stamp}")
        render_segments(segs, sources, target, fit=fit, encoder=encoder, out_path=out, progress=scoped, log=log)
        _log(log, f"Fertig: {out}")
        outputs.append(out)
    _log(log, f"{len(outputs)} getrennte Datei(en) erstellt in {out_dir}: "
              + ", ".join(Path(o).name for o in outputs))
    return outputs

