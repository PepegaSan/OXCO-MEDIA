"""ffprobe FPS/Dauer — Logik wie Oxco compare.py (r_frame_rate vor avg)."""

from __future__ import annotations

import json
import shutil
import subprocess
from collections import Counter
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Optional

LogFn = Optional[Callable[[str], None]]

_STANDARD_CFR = (
    23.976023976023978,
    24.0,
    25.0,
    29.97002997002997,
    30.0,
    48.0,
    50.0,
    59.94005994005994,
    60.0,
)


@dataclass
class MediaInfo:
    width: int = 1920
    height: int = 1080
    fps: float = 30.0
    r_fps: float = 0.0
    avg_fps: float = 0.0
    duration_sec: float = 0.0
    frames: int = 0


def _find_ffprobe() -> Optional[str]:
    local = Path(__file__).resolve().parent / "ffprobe.exe"
    if local.is_file():
        return str(local)
    return shutil.which("ffprobe")


def _eval_fraction_fps(raw: Any) -> Optional[float]:
    if raw is None:
        return None
    s = str(raw).strip()
    if not s or s == "0/0":
        return None
    if "/" in s:
        num, den = s.split("/", 1)
        try:
            n, d = float(num), float(den)
            if d == 0:
                return None
            f = n / d
            return f if f > 0 else None
        except (TypeError, ValueError):
            return None
    try:
        f = float(s)
        return f if f > 0 else None
    except (TypeError, ValueError):
        return None


def _near_standard_cfr(fps: Optional[float], tol: float = 0.06) -> bool:
    if fps is None or fps <= 0:
        return False
    return any(abs(float(fps) - v) < tol for v in _STANDARD_CFR)


def probe_opencv_fps(path: str) -> Optional[float]:
    try:
        import cv2  # optional

        cap = cv2.VideoCapture(path)
        if not cap.isOpened():
            cap.release()
            return None
        fps = float(cap.get(cv2.CAP_PROP_FPS))
        cap.release()
        if fps > 0.01:
            return fps
    except Exception:
        pass
    return None


def probe_stream_fps_rates(path: str) -> tuple[Optional[float], Optional[float]]:
    """Wie Oxco: r_frame_rate (nominal) und avg_frame_rate getrennt."""
    ffprobe = _find_ffprobe()
    if not ffprobe or not path or not Path(path).is_file():
        return None, None
    try:
        completed = subprocess.run(
            [
                ffprobe,
                "-v",
                "error",
                "-select_streams",
                "v:0",
                "-show_entries",
                "stream=avg_frame_rate,r_frame_rate",
                "-of",
                "json",
                path,
            ],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=90,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except (OSError, subprocess.SubprocessError):
        return None, None
    if completed.returncode != 0:
        return None, None
    try:
        streams = (json.loads(completed.stdout or "{}").get("streams") or [])
    except json.JSONDecodeError:
        return None, None
    if not streams:
        return None, None
    st = streams[0]
    return _eval_fraction_fps(st.get("r_frame_rate")), _eval_fraction_fps(st.get("avg_frame_rate"))


def snap_standard_fps(fps: float) -> float:
    for anchor in _STANDARD_CFR:
        if abs(float(fps) - anchor) < 0.04:
            return anchor
    return float(fps)


def pick_playback_fps(
    opencv_fps: Optional[float],
    r_fps: Optional[float],
    avg_fps: Optional[float],
    *,
    log: LogFn = None,
) -> Optional[float]:
    """
    Wie Cutter/Oxco: r_frame_rate bevorzugen, mit NTSC-Sonderfall und
    Schutz vor doppelt getaggten Raten (r=60 bei 30fps-Inhalt).
    """
    try:
        of = float(opencv_fps) if opencv_fps and float(opencv_fps) > 0 else None
    except (TypeError, ValueError):
        of = None
    rf = float(r_fps) if r_fps and float(r_fps) > 0 else None
    af = float(avg_fps) if avg_fps and float(avg_fps) > 0 else None

    if rf is not None and af is not None:
        # Echtes High-FPS: r und avg stimmen überein (z. B. beide ~60)
        if abs(rf - af) < 0.06 and rf >= 47.0 and _near_standard_cfr(rf):
            if log:
                log(f"FPS: r={rf:.6g}, avg={af:.6g} — echtes High-FPS")
            return snap_standard_fps(rf)
        # Cutter: r=29 + avg=29.97 → avg
        if abs(rf - 29.0) < 0.02 and abs(af - 29.97002997002997) < 0.06:
            if log:
                log(f"FPS: r={rf:.6g}, avg={af:.6g} (NTSC) — nutze avg")
            return snap_standard_fps(af)
        # Container taggt oft 2× (60/1 bei 30fps-Inhalt)
        if abs(rf - af * 2.0) < 0.2 and _near_standard_cfr(af):
            if log:
                log(f"FPS: r={rf:.6g} ist ~2× avg={af:.6g} — nutze avg (Cutter-Heuristik)")
            return snap_standard_fps(af)
        if abs(rf - 60.0) < 0.06 and abs(af - 30.0) < 0.06:
            if log:
                log(f"FPS: r=60, avg=30 — nutze 30")
            return 30.0
        if abs(rf - 59.94005994005994) < 0.06 and abs(af - 29.97002997002997) < 0.06:
            if log:
                log(f"FPS: r=59.94, avg=29.97 — nutze 29.97")
            return 29.97002997002997

    if rf is not None:
        if of is not None and abs(rf - of) >= 1.0 and _near_standard_cfr(of):
            if abs(rf - of * 2.0) < 0.2 or abs(rf - of) >= 15.0:
                if log:
                    log(f"FPS: r={rf:.6g} vs OpenCV={of:.6g} — nutze OpenCV")
                return snap_standard_fps(of)
        if af is not None and abs(rf - af) > 0.5 and log:
            log(f"FPS: r_frame_rate={rf:.6g}, avg={af:.6g} — nutze r")
        return snap_standard_fps(rf)

    if af is not None and of is not None and abs(af - of) >= 0.5 and _near_standard_cfr(of):
        if log:
            log(f"FPS: avg={af:.6g} weicht ab, OpenCV={of:.6g} — nutze OpenCV")
        return snap_standard_fps(of)
    if af is not None:
        return snap_standard_fps(af)
    if of is not None:
        return snap_standard_fps(of)
    return None


def probe_format_duration(path: str) -> Optional[float]:
    ffprobe = _find_ffprobe()
    if not ffprobe or not Path(path).is_file():
        return None
    try:
        completed = subprocess.run(
            [
                ffprobe,
                "-v",
                "error",
                "-show_entries",
                "format=duration",
                "-of",
                "default=noprint_wrappers=1:nokey=1",
                path,
            ],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=60,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except (OSError, subprocess.SubprocessError):
        return None
    if completed.returncode != 0:
        return None
    try:
        v = float((completed.stdout or "").strip())
        return v if v > 0 else None
    except (TypeError, ValueError):
        return None


def probe_media(path: str, *, log: LogFn = None) -> MediaInfo:
    info = MediaInfo()
    ffprobe = _find_ffprobe()
    if not ffprobe:
        return info

    r_fps, avg_fps = probe_stream_fps_rates(path)
    info.r_fps = r_fps or 0.0
    info.avg_fps = avg_fps or 0.0
    ocv = probe_opencv_fps(path)
    picked = pick_playback_fps(ocv, r_fps, avg_fps, log=log)
    if picked:
        info.fps = picked

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
            timeout=60,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        meta = json.loads(out.decode("utf-8", errors="replace"))
    except (OSError, subprocess.SubprocessError, json.JSONDecodeError):
        return info

    for stream in meta.get("streams") or []:
        if stream.get("codec_type") == "video":
            info.width = int(stream.get("width") or info.width)
            info.height = int(stream.get("height") or info.height)
            break

    dur = probe_format_duration(path)
    if dur:
        info.duration_sec = dur
    else:
        try:
            info.duration_sec = float((meta.get("format") or {}).get("duration") or 0)
        except (TypeError, ValueError):
            pass

    if info.duration_sec > 0 and info.fps > 0:
        info.frames = max(1, int(round(info.duration_sec * info.fps)))

    return info


def pick_timeline_fps_from_clips(clips: list[str], *, log: LogFn = None) -> float:
    """Timeline-FPS: alle Clips prüfen, Mehrheitsentscheid (Cutter/Oxco + snap)."""
    if not clips:
        return 30.0
    rates: list[float] = []
    for i, path in enumerate(clips):
        info = probe_media(path, log=log if i == 0 else None)
        if info.fps > 0:
            rates.append(snap_standard_fps(info.fps))
            if log and i > 0:
                log(
                    f"  Clip {i + 1}: r={info.r_fps:.3g} avg={info.avg_fps:.3g} "
                    f"→ {snap_standard_fps(info.fps):.6g} fps"
                )
    if not rates:
        return 30.0
    winner, _ = Counter(rates).most_common(1)[0]
    if log:
        if len(set(rates)) > 1:
            log(f"Timeline-FPS Mehrheit: {dict(Counter(rates))} → {winner:.6g}")
        else:
            log(f"Timeline-FPS: {winner:.6g} (alle {len(rates)} Clips)")
    return winner


def summed_source_duration(clips: list[str]) -> float:
    total = 0.0
    for p in clips:
        d = probe_format_duration(p)
        if d and d > 0:
            total += d
        else:
            total += probe_media(p).duration_sec
    return total


def total_duration_sec(paths: list[str]) -> float:
    return summed_source_duration(paths)
