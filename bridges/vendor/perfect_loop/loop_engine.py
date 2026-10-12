"""Seamless video loop: optional smart trim + optical-flow seam morph + FFmpeg encode."""
from __future__ import annotations

import os
import shutil
import subprocess
import tempfile
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Optional, Sequence

import base64

import cv2
import numpy as np

from loop_detect import (
    LoopCandidate,
    cost_to_score,
    find_loop_candidates,
    frame_feature,
    score_sparse,
)

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
    ffmpeg_encoder: str = "cpu"
    mute_audio: bool = True
    # cut = export segment as-is (best when already similar)
    # crossfade = short alpha blend, no warp
    # morph = optical-flow morph (only for harder mismatches)
    # pingpong = forward then backward (boomerang) — always seamless, changes the look
    # auto = pick from quality score
    seam_method: str = "auto"
    target_fps: float = 0.0
    loop_count: int = 1
    preview_max_width: int = 0
    crf: int = 0
    range_start_seconds: float = 0.0
    range_end_seconds: float = 0.0


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
    range_start_seconds: float = 0.0
    range_end_seconds: float = 0.0
    #: Seam cost in "typical frame steps" (0 = perfect).
    seam_cost: float = 0.0
    #: Best alternatives from the search, (start_s, end_s, score) — empty without search.
    candidates: list = None  # type: ignore[assignment]
    #: JPEG base64 of the frames before / after the seam: [end-2, end-1, start, start+1].
    seam_thumbs: list = None  # type: ignore[assignment]


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
    tw = max(2, int(max_width) - (int(max_width) % 2))
    th = max(2, int(round(h * (tw / max(1, w)))))
    th -= th % 2
    return cv2.resize(frame, (tw, th), interpolation=cv2.INTER_AREA)


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


def resolve_frame_range(fps: float, total: int, start_sec: float, end_sec: float) -> tuple[int, int]:
    """User in/out → [start, end) frames. end_sec <= 0 means until the last frame."""
    start = int(round(max(0.0, float(start_sec or 0.0)) * fps))
    start = max(0, min(max(0, total - 4), start))
    if end_sec is None or float(end_sec) <= 0:
        end = total
    else:
        end = int(round(float(end_sec) * fps))
    end = max(start + 4, min(total, end))
    return start, end


def read_frame_at(path: str, index: int) -> np.ndarray:
    cap = cv2.VideoCapture(path)
    if not cap.isOpened():
        raise RuntimeError(f"Cannot open video: {path}")
    if index > 0:
        cap.set(cv2.CAP_PROP_POS_FRAMES, float(index))
    ok, frame = cap.read()
    cap.release()
    if not ok or frame is None:
        raise RuntimeError(f"Cannot read frame {index}: {path}")
    return frame


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


def extract_features(
    path: str,
    start: int,
    end: int,
    *,
    progress: Optional[ProgressFn] = None,
) -> tuple[np.ndarray, tuple[int, int], float]:
    """Stream-decode ``[start, end)`` into compact features (no full frames kept in memory)."""
    fps, total, _, _ = read_video_meta(path)
    end = min(end, total)
    cap = cv2.VideoCapture(path)
    if not cap.isOpened():
        raise RuntimeError(f"Cannot open video: {path}")
    if start > 0:
        cap.set(cv2.CAP_PROP_POS_FRAMES, float(start))
    feats: list[np.ndarray] = []
    shape = (0, 0)
    expected = max(1, end - start)
    while start + len(feats) < end:
        ok, frame = cap.read()
        if not ok or frame is None:
            break
        if not feats:
            shape = frame.shape[:2]
        feats.append(frame_feature(frame))
        if len(feats) % 30 == 0:
            _progress(progress, 5.0 + 45.0 * (len(feats) / expected), "Frames analysieren…")
    cap.release()
    if len(feats) < 4:
        raise RuntimeError(f"Need at least 4 frames, got {len(feats)} from {path}")
    return np.stack(feats), shape, fps


def _read_indices(path: str, indices: Sequence[int]) -> dict[int, np.ndarray]:
    """Decode a few frames by index (sequential reads inside each contiguous run)."""
    out: dict[int, np.ndarray] = {}
    cap = cv2.VideoCapture(path)
    if not cap.isOpened():
        raise RuntimeError(f"Cannot open video: {path}")
    pos = -1
    for i in sorted(set(indices)):
        if i != pos:
            cap.set(cv2.CAP_PROP_POS_FRAMES, float(i))
        ok, frame = cap.read()
        pos = i + 1
        if ok and frame is not None:
            out[i] = frame
    cap.release()
    return out


def range_seam_cost(path: str, start: int, end: int, total: int) -> tuple[float, dict[int, np.ndarray]]:
    """Seam cost of a fixed range from small frame windows around both ends (fast)."""
    idx = [i for i in range(start - 3, start + 4) if 0 <= i < total]
    idx += [i for i in range(end - 4, end + 3) if 0 <= i < total]
    # A mid-clip window makes the typical frame step less dependent on the ends.
    mid = (start + end) // 2
    idx += [i for i in range(mid - 3, mid + 3) if start <= i < end]
    frames = _read_indices(path, idx)
    if not frames:
        raise RuntimeError(f"Cannot read frames: {path}")
    shape = next(iter(frames.values())).shape[:2]
    feats = {i: frame_feature(f) for i, f in frames.items()}
    cost, _ = score_sparse(feats, shape, start, end)
    return cost, frames


def seam_thumbnails(frames: dict[int, np.ndarray], start: int, end: int, width: int = 200) -> list[str]:
    """[end-2, end-1 | start, start+1] as JPEG base64 (missing frames are skipped)."""
    out: list[str] = []
    for i in (end - 2, end - 1, start, start + 1):
        f = frames.get(i)
        if f is None:
            out.append("")
            continue
        f = _maybe_downscale(f, width)
        ok, buf = cv2.imencode(".jpg", f, [int(cv2.IMWRITE_JPEG_QUALITY), 82])
        out.append(base64.b64encode(buf.tobytes()).decode("ascii") if ok else "")
    return out


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
    """Pick gentlest method that usually works for the match quality.

    Ping-pong is never chosen automatically — it changes how the clip looks.
    """
    if quality_score >= 0.70:
        return "cut"
    if quality_score >= 0.40:
        return "crossfade"
    return "morph"


def resolve_seam_method(requested: str, quality_score: float) -> str:
    method = (requested or "auto").strip().lower()
    if method in ("cut", "crossfade", "morph", "pingpong"):
        return method
    return recommend_seam_method(quality_score)


def smart_trim_range(
    frames: Sequence[np.ndarray],
    fps: float,
    *,
    min_loop_seconds: float = 1.5,
    log: Optional[LogFn] = None,
    progress: Optional[ProgressFn] = None,
) -> tuple[int, int, float]:
    """Best (start, end_exclusive) inside already loaded frames."""
    feats = np.stack([frame_feature(f) for f in frames])
    best = find_loop_candidates(
        feats, frames[0].shape[:2], fps, min_loop_seconds=min_loop_seconds, max_candidates=1, progress=progress
    )[0]
    _log(log, f"Smart trim: frames {best.start}..{best.end - 1} (score={best.score:.3f})")
    return best.start, best.end, best.score


def _warp_flow(img: np.ndarray, flow: np.ndarray, factor: float) -> np.ndarray:
    h, w = flow.shape[:2]
    flow_scaled = flow * factor
    grid_x, grid_y = np.meshgrid(np.arange(w, dtype=np.float32), np.arange(h, dtype=np.float32))
    map_x = (grid_x + flow_scaled[:, :, 0]).astype(np.float32)
    map_y = (grid_y + flow_scaled[:, :, 1]).astype(np.float32)
    return cv2.remap(img, map_x, map_y, interpolation=cv2.INTER_LINEAR, borderMode=cv2.BORDER_REPLICATE)


def _optical_flow(ga: np.ndarray, gb: np.ndarray) -> np.ndarray:
    """Dense flow a→b. DIS at capped resolution is less ghosty than full-res Farneback."""
    h, w = ga.shape[:2]
    work_a, work_b, scale = ga, gb, 1.0
    if w > 720:
        scale = 720.0 / w
        nw = max(2, int(round(w * scale)) & ~1)
        nh = max(2, int(round(h * scale)) & ~1)
        work_a = cv2.resize(ga, (nw, nh), interpolation=cv2.INTER_AREA)
        work_b = cv2.resize(gb, (nw, nh), interpolation=cv2.INTER_AREA)

    flow = None
    create = getattr(cv2, "DISOpticalFlow_create", None)
    if create is None:
        dis_cls = getattr(cv2, "DISOpticalFlow", None)
        create = getattr(dis_cls, "create", None) if dis_cls is not None else None
    if create is not None:
        try:
            preset = getattr(cv2, "DISOPTICAL_FLOW_PRESET_MEDIUM", 1)
            flow = create(preset).calc(work_a, work_b, None)
        except cv2.error:
            flow = None
    if flow is None:
        flow = cv2.calcOpticalFlowFarneback(work_a, work_b, None, 0.5, 3, 15, 3, 5, 1.2, 0)

    if scale != 1.0:
        flow = cv2.resize(flow, (w, h), interpolation=cv2.INTER_LINEAR)
        flow *= 1.0 / scale
    return flow


def morph_pair(a: np.ndarray, b: np.ndarray, alpha: float) -> np.ndarray:
    """Optical-flow morph mixed with a dissolve so large motion does not ghost."""
    ga = cv2.cvtColor(a, cv2.COLOR_BGR2GRAY)
    gb = cv2.cvtColor(b, cv2.COLOR_BGR2GRAY)
    flow_ab = _optical_flow(ga, gb)
    flow_ba = _optical_flow(gb, ga)
    warped_a = _warp_flow(a, flow_ab, alpha)
    warped_b = _warp_flow(b, flow_ba, 1.0 - alpha)
    morph = cv2.addWeighted(warped_a, 1.0 - alpha, warped_b, alpha, 0)
    fade = crossfade_pair(a, b, alpha)
    mag = float(np.mean(np.linalg.norm(flow_ab, axis=2)))
    fade_w = float(np.clip(mag / 14.0, 0.18, 0.70))
    return cv2.addWeighted(morph, 1.0 - fade_w, fade, fade_w, 0)


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
    if method == "pingpong":
        _log(log, "Seam method: ping-pong (forward + reverse)")
        _progress(progress, 85.0, "Ping-Pong…")
        # Drop both turnaround frames from the reverse pass so no frame is shown twice.
        return list(frames) + list(frames[-2:0:-1])
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
        t = (i + 1) / (seam + 1)
        alpha = t * t * (3.0 - 2.0 * t)
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


def _clamp_crf(value: object, default: int) -> int:
    try:
        parsed = int(round(float(value)))  # type: ignore[arg-type]
    except (TypeError, ValueError):
        return default
    if parsed <= 0:
        return default
    return max(1, min(51, parsed))


def encoder_args(encoder: str, crf: object = 0) -> list[str]:
    defaults = {
        "cpu": 18,
        "cpu_hevc": 22,
        "nvidia_h264": 18,
        "nvidia_hevc": 18,
        "copy": 18,
    }
    key = encoder if encoder in defaults else "cpu"
    q = str(_clamp_crf(crf, defaults[key]))
    table = {
        "cpu": ["-c:v", "libx264", "-preset", "fast", "-crf", q],
        "cpu_hevc": ["-c:v", "libx265", "-preset", "fast", "-crf", q],
        "nvidia_h264": ["-c:v", "h264_nvenc", "-preset", "p6", "-cq", q],
        "nvidia_hevc": ["-c:v", "hevc_nvenc", "-preset", "p6", "-cq", q],
        "copy": ["-c:v", "libx264", "-preset", "fast", "-crf", q],
    }
    return list(table.get(encoder, table["cpu"]))


def write_frames_ffmpeg(
    frames: Sequence[np.ndarray],
    output_path: str,
    fps: float,
    *,
    encoder: str = "cpu",
    mute_audio: bool = True,
    target_fps: float = 0.0,
    crf: int = 0,
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
            *encoder_args(encoder, crf),
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
            if "nvenc" in " ".join(encoder_args(encoder, crf)).lower() or encoder.startswith("nvidia"):
                _log(log, "NVENC failed — retrying with libx264")
                cmd_cpu = [
                    ffmpeg,
                    "-y",
                    "-framerate",
                    f"{out_fps:.6f}",
                    "-i",
                    pattern,
                    *encoder_args("cpu", crf),
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


def _repeat_encoded_loop(output_path: str, loop_count: int, *, log: Optional[LogFn] = None) -> None:
    """Write the seamless cycle N times into the file (preview still plays one cycle)."""
    count = max(1, int(loop_count))
    if count <= 1:
        return
    ffmpeg = find_ffmpeg()
    if not ffmpeg:
        raise RuntimeError("ffmpeg not found on PATH")
    src = Path(output_path)
    tmp = src.with_name(f"{src.stem}.__looprep__{src.suffix or '.mp4'}")
    cmd = [
        ffmpeg,
        "-y",
        "-stream_loop",
        str(count - 1),
        "-i",
        str(src),
        "-c",
        "copy",
        "-movflags",
        "+faststart",
        str(tmp),
    ]
    _log(log, f"Repeating loop {count}x")
    flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    proc = subprocess.run(
        cmd,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        creationflags=flags,
    )
    if proc.returncode != 0 or not tmp.is_file():
        err = (proc.stderr or proc.stdout or "").strip()
        if tmp.exists():
            tmp.unlink(missing_ok=True)
        raise RuntimeError(err[-2000:] or "ffmpeg loop repeat failed")
    tmp.replace(src)


def analyze_loop(
    settings: LoopSettings,
    *,
    log: Optional[LogFn] = None,
    progress: Optional[ProgressFn] = None,
) -> AnalyzeResult:
    """Pre-check of the seam. With smart_trim, searches the best loop window and alternatives."""
    path = settings.input_path
    if not path or not os.path.isfile(path):
        raise FileNotFoundError(f"Input not found: {path}")

    _progress(progress, 5.0, "Prüfen…")
    fps, total, _, _ = read_video_meta(path)
    win_start, win_end = resolve_frame_range(
        fps, total, settings.range_start_seconds, settings.range_end_seconds
    )
    trim_start, trim_end = win_start, win_end
    smart_used = False
    candidates: list[LoopCandidate] = []

    if settings.smart_trim:
        # One extra frame so the window end can be judged against its successor.
        feats, shape, fps = extract_features(path, win_start, min(total, win_end + 1), progress=progress)
        candidates = find_loop_candidates(
            feats, shape, fps, min_loop_seconds=settings.min_loop_seconds, progress=progress
        )
        best = candidates[0]
        trim_start = win_start + best.start
        trim_end = win_start + best.end
        smart_used = True

    _progress(progress, 90.0, "Naht bewerten…")
    cost, frames = range_seam_cost(path, trim_start, trim_end, total)
    quality = cost_to_score(cost)
    frame_count = max(0, trim_end - trim_start)
    duration = frame_count / max(fps, 1e-6)
    quality_warning = quality < 0.55
    recommended = recommend_seam_seconds(quality, duration)
    method = recommend_seam_method(quality)
    _progress(progress, 100.0, "Prüfung fertig")
    _log(log, f"Naht: {cost:.2f} Bildschritte Abweichung → Score {quality:.3f}, Empfehlung '{method}'")

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
        range_start_seconds=trim_start / max(fps, 1e-6),
        range_end_seconds=trim_end / max(fps, 1e-6),
        seam_cost=cost,
        candidates=[
            (
                (win_start + c.start) / max(fps, 1e-6),
                (win_start + c.end) / max(fps, 1e-6),
                c.score,
            )
            for c in candidates
        ],
        seam_thumbs=seam_thumbnails(frames, trim_start, trim_end),
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
    fps, total, _, _ = read_video_meta(path)
    win_start, win_end = resolve_frame_range(
        fps, total, settings.range_start_seconds, settings.range_end_seconds
    )
    max_w = settings.preview_max_width if settings.preview_max_width and settings.preview_max_width > 0 else None
    frames, fps = load_frames(
        path, start=win_start, end=win_end, max_width=max_w, log=log, progress=progress
    )

    trim_start, trim_end = win_start, win_end
    smart_used = False
    if settings.smart_trim:
        _progress(progress, 40.0, "Abschnitt suchen…")
        local_s, local_e, _ = smart_trim_range(
            frames,
            fps,
            min_loop_seconds=settings.min_loop_seconds,
            log=log,
            progress=progress,
        )
        frames = list(frames[local_s:local_e])
        trim_start = win_start + local_s
        trim_end = win_start + local_e
        smart_used = True

    cost, _ = range_seam_cost(path, trim_start, trim_end, total)
    quality = cost_to_score(cost)
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

    seam_frames = 0 if method in ("cut", "pingpong") else max(2, int(round(settings.seam_seconds * fps)))
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
        crf=settings.crf,
        log=log,
    )
    repeats = max(1, int(settings.loop_count or 1))
    if repeats > 1:
        _progress(progress, 96.0, "Wiederholen…")
        _repeat_encoded_loop(settings.output_path, repeats, log=log)
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
