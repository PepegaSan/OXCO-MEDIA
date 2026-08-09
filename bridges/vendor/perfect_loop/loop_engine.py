"""Seamless video loop: optional smart trim + optical-flow seam morph + FFmpeg encode."""
from __future__ import annotations

import os
import shutil
import subprocess
import tempfile
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Optional, Sequence

import cv2
import numpy as np

LogFn = Callable[[str], None]
ProgressFn = Callable[[float, str], None]

VIDEO_EXTS = {".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".mts", ".m2ts", ".wmv", ".flv"}


@dataclass
class LoopSettings:
    input_path: str
    output_path: str
    seam_seconds: float = 0.25
    smart_trim: bool = False
    min_loop_seconds: float = 1.5
    ffmpeg_encoder: str = "nvidia_h264"
    mute_audio: bool = True
    # cut = export segment as-is (best when already similar)
    # crossfade = short alpha blend, no warp
    # morph = optical-flow morph (only for harder mismatches)
    # auto = pick from quality score
    seam_method: str = "auto"
    target_fps: float = 0.0


@dataclass
class LoopResult:
    output_path: str
    quality_score: float
    quality_warning: bool
    smart_trim_used: bool
    trim_start_frame: int
    trim_end_frame: int
    frame_count: int
    fps: float
    seam_method_used: str = "cut"


@dataclass
class AnalyzeResult:
    """Start↔end similarity before seam (seam does not change this score)."""

    quality_score: float
    quality_warning: bool
    smart_trim_used: bool
    trim_start_frame: int
    trim_end_frame: int
    frame_count: int
    fps: float
    duration_seconds: float
    recommended_seam_seconds: float
    recommended_seam_method: str


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


def frame_similarity(a: np.ndarray, b: np.ndarray) -> float:
    """Higher is better (1.0 = identical). Uses grayscale MSE mapped to [0, 1]."""
    if a.shape != b.shape:
        b = cv2.resize(b, (a.shape[1], a.shape[0]), interpolation=cv2.INTER_AREA)
    ga = cv2.cvtColor(a, cv2.COLOR_BGR2GRAY).astype(np.float32)
    gb = cv2.cvtColor(b, cv2.COLOR_BGR2GRAY).astype(np.float32)
    mse = float(np.mean((ga - gb) ** 2))
    return 1.0 / (1.0 + mse / 100.0)


def read_video_meta(path: str) -> tuple[float, int, int, int]:
    cap = cv2.VideoCapture(path)
    if not cap.isOpened():
        raise RuntimeError(f"Cannot open video: {path}")
    fps = float(cap.get(cv2.CAP_PROP_FPS) or 30.0)
    if fps <= 1e-3:
        fps = 30.0
    count = int(cap.get(cv2.CAP_PROP_FRAME_COUNT) or 0)
    w = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH) or 0)
    h = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT) or 0)
    cap.release()
    if count <= 0 or w <= 0 or h <= 0:
        raise RuntimeError(f"Invalid video metadata: {path}")
    return fps, count, w, h


def _maybe_downscale(frame: np.ndarray, max_width: Optional[int]) -> np.ndarray:
    if not max_width or frame.shape[1] <= max_width:
        return frame
    h, w = frame.shape[:2]
    th = max(1, int(round(h * (max_width / max(1, w)))))
    return cv2.resize(frame, (max_width, th), interpolation=cv2.INTER_AREA)


def read_first_last_frames(path: str) -> tuple[np.ndarray, np.ndarray, float, int]:
    """Fast endpoint read for pre-check (no full decode)."""
    fps, total, _, _ = read_video_meta(path)
    cap = cv2.VideoCapture(path)
    if not cap.isOpened():
        raise RuntimeError(f"Cannot open video: {path}")
    ok, first = cap.read()
    if not ok or first is None:
        cap.release()
        raise RuntimeError(f"Cannot read first frame: {path}")
    last = first
    if total > 1:
        cap.set(cv2.CAP_PROP_POS_FRAMES, float(max(0, total - 1)))
        ok2, frame = cap.read()
        if ok2 and frame is not None:
            last = frame
        else:
            # fallback: sequential to end
            cap.set(cv2.CAP_PROP_POS_FRAMES, 0.0)
            while True:
                ok3, frame = cap.read()
                if not ok3 or frame is None:
                    break
                last = frame
    cap.release()
    return first, last, fps, total


def load_frames(
    path: str,
    *,
    start: int = 0,
    end: Optional[int] = None,
    max_width: Optional[int] = None,
    log: Optional[LogFn] = None,
    progress: Optional[ProgressFn] = None,
) -> tuple[list[np.ndarray], float]:
    fps, total, _, _ = read_video_meta(path)
    end_i = total if end is None else min(end, total)
    start_i = max(0, min(start, end_i - 1))
    expected = max(1, end_i - start_i)

    cap = cv2.VideoCapture(path)
    if not cap.isOpened():
        raise RuntimeError(f"Cannot open video: {path}")
    if start_i > 0:
        cap.set(cv2.CAP_PROP_POS_FRAMES, float(start_i))

    frames: list[np.ndarray] = []
    idx = start_i
    while idx < end_i:
        ok, frame = cap.read()
        if not ok or frame is None:
            break
        frames.append(_maybe_downscale(frame, max_width))
        idx += 1
        if len(frames) % 30 == 0:
            _progress(progress, 5.0 + 35.0 * (len(frames) / expected), "Frames laden…")

    cap.release()
    if len(frames) < 4:
        raise RuntimeError(f"Need at least 4 frames, got {len(frames)} from {path}")
    _log(log, f"Loaded {len(frames)} frames @ {fps:.3f} fps")
    return frames, fps


def recommend_seam_seconds(quality_score: float, duration_seconds: float) -> float:
    """Suggest longer seam when start/end match is weak (crossfade/morph only)."""
    if quality_score >= 0.75:
        base = 0.08
    elif quality_score >= 0.55:
        base = 0.15
    elif quality_score >= 0.35:
        base = 0.30
    else:
        base = 0.45
    return round(min(base, max(0.05, duration_seconds * 0.15)), 2)


def recommend_seam_method(quality_score: float) -> str:
    """Pick gentlest method that usually works for the match quality."""
    if quality_score >= 0.70:
        return "cut"
    if quality_score >= 0.45:
        return "crossfade"
    return "morph"


def resolve_seam_method(requested: str, quality_score: float) -> str:
    method = (requested or "auto").strip().lower()
    if method in ("cut", "crossfade", "morph"):
        return method
    return recommend_seam_method(quality_score)


def smart_trim_range(
    frames: Sequence[np.ndarray],
    fps: float,
    *,
    min_loop_seconds: float = 1.5,
    sample_width: int = 160,
    stride: int = 2,
    log: Optional[LogFn] = None,
    progress: Optional[ProgressFn] = None,
) -> tuple[int, int, float]:
    """Find best (start, end_exclusive) loop window via downscaled similarity search."""
    n = len(frames)
    min_len = max(4, int(round(min_loop_seconds * fps)))
    if n < min_len + 2:
        _log(log, "Smart trim skipped — clip shorter than min loop length")
        return 0, n, frame_similarity(frames[0], frames[-1])

    thumbs: list[np.ndarray] = []
    for i in range(0, n, stride):
        f = frames[i]
        h, w = f.shape[:2]
        tw = sample_width
        th = max(1, int(round(h * (tw / max(1, w)))))
        small = cv2.resize(f, (tw, th), interpolation=cv2.INTER_AREA)
        thumbs.append(cv2.cvtColor(small, cv2.COLOR_BGR2GRAY).astype(np.float32))

    thumb_n = len(thumbs)
    min_thumb_span = max(2, min_len // stride)
    best_score = -1.0
    best_a = 0
    best_b = thumb_n - 1
    pairs = 0
    total_pairs = max(1, (thumb_n - min_thumb_span) * (thumb_n // 2))

    for ai in range(0, thumb_n - min_thumb_span):
        for bi in range(ai + min_thumb_span, thumb_n):
            mse = float(np.mean((thumbs[ai] - thumbs[bi]) ** 2))
            score = 1.0 / (1.0 + mse / 100.0)
            # Prefer mid-length loops slightly when scores are close
            length_bias = 0.02 * (1.0 - abs((bi - ai) / max(1, thumb_n - 1) - 0.55))
            score += length_bias
            if score > best_score:
                best_score = score
                best_a = ai
                best_b = bi
            pairs += 1
            if pairs % 200 == 0:
                _progress(progress, 40.0 + 20.0 * (pairs / total_pairs), "Abschnitt suchen…")

    start = best_a * stride
    end = min(n, best_b * stride + 1)
    if end - start < min_len:
        end = min(n, start + min_len)
    score = frame_similarity(frames[start], frames[end - 1])
    _log(log, f"Smart trim: frames {start}..{end - 1} (score={score:.3f})")
    return start, end, score


def _warp_flow(img: np.ndarray, flow: np.ndarray, factor: float) -> np.ndarray:
    h, w = flow.shape[:2]
    flow_scaled = flow * factor
    grid_x, grid_y = np.meshgrid(np.arange(w, dtype=np.float32), np.arange(h, dtype=np.float32))
    map_x = (grid_x + flow_scaled[:, :, 0]).astype(np.float32)
    map_y = (grid_y + flow_scaled[:, :, 1]).astype(np.float32)
    return cv2.remap(img, map_x, map_y, interpolation=cv2.INTER_LINEAR, borderMode=cv2.BORDER_REPLICATE)


def morph_pair(a: np.ndarray, b: np.ndarray, alpha: float) -> np.ndarray:
    """Optical-flow morph from a→b at blend factor alpha in [0, 1]."""
    ga = cv2.cvtColor(a, cv2.COLOR_BGR2GRAY)
    gb = cv2.cvtColor(b, cv2.COLOR_BGR2GRAY)
    flow_ab = cv2.calcOpticalFlowFarneback(ga, gb, None, 0.5, 3, 15, 3, 5, 1.2, 0)
    flow_ba = cv2.calcOpticalFlowFarneback(gb, ga, None, 0.5, 3, 15, 3, 5, 1.2, 0)
    warped_a = _warp_flow(a, flow_ab, alpha)
    warped_b = _warp_flow(b, flow_ba, 1.0 - alpha)
    blended = cv2.addWeighted(warped_a, 1.0 - alpha, warped_b, alpha, 0)
    return blended


def crossfade_pair(a: np.ndarray, b: np.ndarray, alpha: float) -> np.ndarray:
    """Simple dissolve — no warp (avoids ghost morph when motion is small)."""
    if a.shape != b.shape:
        b = cv2.resize(b, (a.shape[1], a.shape[0]), interpolation=cv2.INTER_LINEAR)
    return cv2.addWeighted(a, 1.0 - alpha, b, alpha, 0)


def build_loop_frames(
    frames: Sequence[np.ndarray],
    seam_frames: int,
    method: str,
    *,
    log: Optional[LogFn] = None,
    progress: Optional[ProgressFn] = None,
) -> list[np.ndarray]:
    """Build export frames. 'cut' keeps the segment intact (best for good loops)."""
    method = (method or "cut").lower()
    n = len(frames)
    if method == "cut" or seam_frames <= 0 or n < 6:
        _log(log, "Seam method: cut (segment as-is, no blend)")
        _progress(progress, 85.0, "Schnitt…")
        return list(frames)

    seam = max(2, min(seam_frames, max(2, n // 4)))
    if seam % 2 == 1:
        seam += 1
    half = seam // 2
    use_morph = method == "morph"
    _log(log, f"Seam method: {method} ({seam} frames)")

    blends: list[np.ndarray] = []
    for i in range(seam):
        alpha = (i + 1) / (seam + 1)
        src_idx = n - seam + i
        dst_idx = i
        if use_morph:
            blends.append(morph_pair(frames[src_idx], frames[dst_idx], alpha))
            label = "Naht morphen…"
        else:
            blends.append(crossfade_pair(frames[src_idx], frames[dst_idx], alpha))
            label = "Crossfade…"
        _progress(progress, 60.0 + 25.0 * ((i + 1) / seam), label)

    out: list[np.ndarray] = []
    out.extend(blends[half:])
    out.extend(frames[seam : n - seam])
    out.extend(blends[:half])
    if len(out) < 4:
        out = blends
    return out


def build_seam_loop(
    frames: Sequence[np.ndarray],
    seam_frames: int,
    *,
    log: Optional[LogFn] = None,
    progress: Optional[ProgressFn] = None,
) -> list[np.ndarray]:
    """Back-compat wrapper → morph."""
    return build_loop_frames(frames, seam_frames, "morph", log=log, progress=progress)


def encoder_args(encoder: str) -> list[str]:
    table = {
        "cpu": ["-c:v", "libx264", "-preset", "fast", "-crf", "18"],
        "cpu_hevc": ["-c:v", "libx265", "-preset", "fast", "-crf", "22"],
        "nvidia_h264": ["-c:v", "h264_nvenc", "-preset", "p6", "-cq", "18"],
        "nvidia_hevc": ["-c:v", "hevc_nvenc", "-preset", "p6", "-cq", "18"],
        "copy": ["-c:v", "libx264", "-preset", "fast", "-crf", "18"],
    }
    return list(table.get(encoder, table["nvidia_h264"]))


def write_frames_ffmpeg(
    frames: Sequence[np.ndarray],
    output_path: str,
    fps: float,
    *,
    encoder: str = "nvidia_h264",
    mute_audio: bool = True,
    target_fps: float = 0.0,
    log: Optional[LogFn] = None,
) -> None:
    ffmpeg = find_ffmpeg()
    if not ffmpeg:
        raise RuntimeError("ffmpeg not found on PATH")

    out = Path(output_path)
    out.parent.mkdir(parents=True, exist_ok=True)

    h, w = frames[0].shape[:2]
    # Ensure even dimensions for common codecs
    ew, eh = w - (w % 2), h - (h % 2)

    try:
        from ffmpeg_fps import normalize_target_fps
    except ImportError:
        import sys
        sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
        from ffmpeg_fps import normalize_target_fps

    out_fps = normalize_target_fps(target_fps) or fps
    # Subsample when lowering FPS so duration stays the same (e.g. 120 -> 30)
    export_frames: Sequence[np.ndarray] = frames
    if out_fps + 0.01 < fps and len(frames) > 2:
        duration = len(frames) / max(fps, 1e-6)
        out_n = max(2, int(round(duration * out_fps)))
        idxs = [
            min(len(frames) - 1, int(round(i * (len(frames) - 1) / max(1, out_n - 1))))
            for i in range(out_n)
        ]
        export_frames = [frames[i] for i in idxs]
        _log(log, f"FPS resample {fps:.3f} -> {out_fps:g} ({len(frames)} -> {len(export_frames)} frames)")

    with tempfile.TemporaryDirectory(prefix="hm_perfect_loop_") as tmp:
        pattern = str(Path(tmp) / "frame_%06d.png")
        for i, frame in enumerate(export_frames):
            cropped = frame[:eh, :ew]
            cv2.imwrite(str(Path(tmp) / f"frame_{i:06d}.png"), cropped)

        cmd = [
            ffmpeg,
            "-y",
            "-framerate",
            f"{out_fps:.6f}",
            "-i",
            pattern,
            *encoder_args(encoder),
            "-pix_fmt",
            "yuv420p",
            "-movflags",
            "+faststart",
        ]
        if mute_audio:
            cmd.append("-an")
        cmd.append(str(out))

        _log(log, f"Encoding {len(export_frames)} frames -> {out.name}")
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
            # Fallback to CPU if NVENC fails
            if "nvenc" in " ".join(encoder_args(encoder)).lower() or encoder.startswith("nvidia"):
                _log(log, "NVENC failed — retrying with libx264")
                cmd_cpu = [
                    ffmpeg,
                    "-y",
                    "-framerate",
                    f"{out_fps:.6f}",
                    "-i",
                    pattern,
                    *encoder_args("cpu"),
                    "-pix_fmt",
                    "yuv420p",
                    "-movflags",
                    "+faststart",
                ]
                if mute_audio:
                    cmd_cpu.append("-an")
                cmd_cpu.append(str(out))
                proc2 = subprocess.run(
                    cmd_cpu,
                    capture_output=True,
                    text=True,
                    encoding="utf-8",
                    errors="replace",
                    creationflags=flags,
                )
                if proc2.returncode != 0:
                    raise RuntimeError((proc2.stderr or err or "ffmpeg encode failed")[-2000:])
            else:
                raise RuntimeError(err[-2000:] or "ffmpeg encode failed")


def analyze_loop(
    settings: LoopSettings,
    *,
    log: Optional[LogFn] = None,
    progress: Optional[ProgressFn] = None,
) -> AnalyzeResult:
    """Pre-check: Start↔Ende similarity. Seam duration does not affect this score."""
    path = settings.input_path
    if not path or not os.path.isfile(path):
        raise FileNotFoundError(f"Input not found: {path}")

    _progress(progress, 5.0, "Prüfen…")
    fps, total, _, _ = read_video_meta(path)
    trim_start, trim_end = 0, total
    smart_used = False

    if settings.smart_trim:
        # Downscaled full pass for segment search — still no morph/encode
        _progress(progress, 15.0, "Abschnitt suchen…")
        frames, fps = load_frames(path, max_width=160, log=log, progress=progress)
        trim_start, trim_end, quality = smart_trim_range(
            frames,
            fps,
            min_loop_seconds=settings.min_loop_seconds,
            log=log,
            progress=progress,
        )
        smart_used = True
        frame_count = max(0, trim_end - trim_start)
    else:
        first, last, fps, total = read_first_last_frames(path)
        quality = frame_similarity(first, last)
        trim_end = total
        frame_count = total

    duration = frame_count / max(fps, 1e-6)
    quality_warning = quality < 0.55
    recommended = recommend_seam_seconds(quality, duration)
    method = recommend_seam_method(quality)
    _progress(progress, 100.0, "Prüfung fertig")

    if quality_warning:
        _log(
            log,
            f"Start/Ende-Aehnlichkeit: {quality:.3f} (niedrig). "
            f"Empfehlung: Methode '{method}', Seam ~{recommended:.2f}s"
            + (" oder Smart-Trim." if not smart_used else "."),
        )
    else:
        _log(
            log,
            f"Start/Ende-Aehnlichkeit: {quality:.3f} — gut. "
            f"Empfehlung: Methode '{method}' (Morph oft unnötig und erzeugt Geisterbilder).",
        )

    return AnalyzeResult(
        quality_score=quality,
        quality_warning=quality_warning,
        smart_trim_used=smart_used,
        trim_start_frame=trim_start,
        trim_end_frame=trim_end,
        frame_count=frame_count,
        fps=fps,
        duration_seconds=duration,
        recommended_seam_seconds=recommended,
        recommended_seam_method=method,
    )


def run_loop(
    settings: LoopSettings,
    *,
    log: Optional[LogFn] = None,
    progress: Optional[ProgressFn] = None,
) -> LoopResult:
    path = settings.input_path
    if not path or not os.path.isfile(path):
        raise FileNotFoundError(f"Input not found: {path}")

    _progress(progress, 2.0, "Start…")
    frames, fps = load_frames(path, log=log, progress=progress)

    trim_start, trim_end = 0, len(frames)
    smart_used = False
    if settings.smart_trim:
        _progress(progress, 40.0, "Abschnitt suchen…")
        trim_start, trim_end, _ = smart_trim_range(
            frames,
            fps,
            min_loop_seconds=settings.min_loop_seconds,
            log=log,
            progress=progress,
        )
        frames = list(frames[trim_start:trim_end])
        smart_used = True

    quality = frame_similarity(frames[0], frames[-1])
    quality_warning = quality < 0.55
    duration = len(frames) / max(fps, 1e-6)
    recommended = recommend_seam_seconds(quality, duration)
    method = resolve_seam_method(settings.seam_method, quality)
    if quality_warning:
        _log(
            log,
            f"Start/Ende-Aehnlichkeit: {quality:.3f} (niedrig) — Methode={method}, "
            f"Seam-Empfehlung ~{recommended:.2f}s.",
        )
    else:
        _log(log, f"Start/Ende-Aehnlichkeit: {quality:.3f} — Methode={method}")

    seam_frames = 0 if method == "cut" else max(2, int(round(settings.seam_seconds * fps)))
    _progress(progress, 60.0, "Naht…")
    looped = build_loop_frames(frames, seam_frames, method, log=log, progress=progress)

    _progress(progress, 88.0, "Encodieren…")
    write_frames_ffmpeg(
        looped,
        settings.output_path,
        fps,
        encoder=settings.ffmpeg_encoder,
        mute_audio=settings.mute_audio,
        target_fps=settings.target_fps,
        log=log,
    )
    _progress(progress, 100.0, "Fertig")

    return LoopResult(
        output_path=settings.output_path,
        quality_score=quality,
        quality_warning=quality_warning,
        smart_trim_used=smart_used,
        trim_start_frame=trim_start,
        trim_end_frame=trim_end,
        frame_count=len(looped),
        fps=fps,
        seam_method_used=method,
    )


def default_output_path(input_path: str, output_dir: Optional[str] = None) -> str:
    ip = Path(input_path)
    directory = Path(output_dir) if output_dir else ip.parent
    return str(directory / f"{ip.stem}_loop{ip.suffix or '.mp4'}")
