"""
Oxco compare — analysis core (no GUI, no DaVinci).

Pipeline:
  1. Decode source + deepfake as grayscale frame pairs (OpenCV or ffmpeg pipe, both prefetched in threads).
  2. Optionally detect a constant frame offset between the two files (dropped/duplicated first frames).
  3. Per frame pair compute a difference metric:
       classic — number of changed pixels at full resolution (legacy behaviour, thresholds unchanged).
       blob    — area of the largest connected changed region at a fixed analysis width, after blur +
                 morphological opening (resolution-independent, ignores scattered codec noise).
  4. Turn the per-frame metric into keep/cut segments (pure functions below; cheap, so filters can be
     re-applied from cached metrics without decoding again).
"""

from __future__ import annotations

import hashlib
import json
import os
import queue
import subprocess
import tempfile
import threading
import time
from collections import deque
from dataclasses import dataclass
from typing import Callable, Iterable, List, Optional, Sequence, Tuple

import cv2
import numpy as np

ANALYSIS_MODES = ("classic", "blob")
METRICS_VERSION = 1

_NO_WINDOW = subprocess.CREATE_NO_WINDOW if hasattr(subprocess, "CREATE_NO_WINDOW") else 0


# ——— Parameters ———


@dataclass
class AnalysisParams:
    """Everything that changes the per-frame metric (part of the metrics cache key)."""

    mode: str = "classic"
    noise: int = 15
    analysis_width: int = 640
    blur: int = 3
    hwaccel: bool = False
    auto_sync: bool = True
    max_sync_offset: int = 12

    def normalized(self) -> "AnalysisParams":
        mode = self.mode if self.mode in ANALYSIS_MODES else "classic"
        blur = max(0, int(self.blur))
        if blur > 0 and blur % 2 == 0:
            blur += 1
        return AnalysisParams(
            mode=mode,
            noise=max(0, min(255, int(self.noise))),
            analysis_width=max(64, min(3840, int(self.analysis_width))),
            blur=blur,
            hwaccel=bool(self.hwaccel),
            auto_sync=bool(self.auto_sync),
            max_sync_offset=max(0, min(120, int(self.max_sync_offset))),
        )

    def cache_key(self) -> dict:
        p = self.normalized()
        key = {
            "v": METRICS_VERSION,
            "mode": p.mode,
            "noise": p.noise,
            "auto_sync": p.auto_sync,
            "max_sync_offset": p.max_sync_offset if p.auto_sync else 0,
        }
        if p.mode == "blob":
            key["analysis_width"] = p.analysis_width
            key["blur"] = p.blur
        return key


@dataclass
class FilterParams:
    """Post-processing of the metric — changing these never requires decoding again."""

    pixel_threshold: int = 200
    pixel_max: int = 0
    buffer_frames: int = 0
    min_diff_run: int = 1
    min_keep_frames: int = 0
    cut_padding: int = 0


# ——— Per-frame metrics ———


def classic_metric(gray_s: np.ndarray, gray_d: np.ndarray, noise: int) -> Tuple[int, int]:
    """Legacy compare.py measure: changed pixel count. Returns (metric, total_changed)."""
    diff = cv2.absdiff(gray_s, gray_d)
    _, mask = cv2.threshold(diff, int(noise), 255, cv2.THRESH_BINARY)
    n = int(cv2.countNonZero(mask))
    return n, n


_OPEN_KERNEL = cv2.getStructuringElement(cv2.MORPH_RECT, (3, 3))
_CLOSE_KERNEL = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (5, 5))


def blob_mask(gray_s: np.ndarray, gray_d: np.ndarray, noise: int, blur: int) -> np.ndarray:
    if blur and blur > 1:
        gray_s = cv2.GaussianBlur(gray_s, (blur, blur), 0)
        gray_d = cv2.GaussianBlur(gray_d, (blur, blur), 0)
    diff = cv2.absdiff(gray_s, gray_d)
    _, mask = cv2.threshold(diff, int(noise), 255, cv2.THRESH_BINARY)
    # Opening removes isolated pixels and 1-px edge lines that codec noise produces around contrast edges.
    mask = cv2.morphologyEx(mask, cv2.MORPH_OPEN, _OPEN_KERNEL)
    # Closing bridges thin gaps inside a changed region (e.g. where a background edge happens to match
    # the swapped face's brightness), so a patchy swap still counts as one region.
    return cv2.morphologyEx(mask, cv2.MORPH_CLOSE, _CLOSE_KERNEL)


def largest_blob(mask: np.ndarray) -> Tuple[int, Optional[Tuple[int, int, int, int]]]:
    """Area and bounding box (x, y, w, h) of the largest connected region in a binary mask."""
    if cv2.countNonZero(mask) == 0:
        return 0, None
    n, _labels, stats, _ = cv2.connectedComponentsWithStats(mask, connectivity=8)
    if n <= 1:
        return 0, None
    areas = stats[1:, cv2.CC_STAT_AREA]
    i = int(np.argmax(areas)) + 1
    x, y, w, h = (int(v) for v in stats[i, :4])
    return int(stats[i, cv2.CC_STAT_AREA]), (x, y, w, h)


def blob_metric(gray_s: np.ndarray, gray_d: np.ndarray, noise: int, blur: int) -> Tuple[int, int]:
    """Largest connected changed region. Returns (largest_area, total_changed)."""
    mask = blob_mask(gray_s, gray_d, noise, blur)
    total = int(cv2.countNonZero(mask))
    if total == 0:
        return 0, 0
    area, _ = largest_blob(mask)
    return area, total


def analysis_size(src_w: int, src_h: int, analysis_width: int) -> Tuple[int, int]:
    """Even-sized analysis frame with source aspect; never upscales."""
    if src_w <= 0 or src_h <= 0:
        return 0, 0
    w = min(int(analysis_width), int(src_w))
    w = max(2, w - (w % 2))
    h = int(round(src_h * w / float(src_w)))
    h = max(2, h - (h % 2))
    return w, h


# ——— Frame readers ———


class _ThreadedReader:
    """Runs a blocking frame producer in a thread so both files decode in parallel with the analysis."""

    _END = object()

    def __init__(self, produce: Callable[[], Optional[np.ndarray]], cleanup: Callable[[], None], maxsize: int = 32):
        self._produce = produce
        self._cleanup = cleanup
        self._q: "queue.Queue[object]" = queue.Queue(maxsize=maxsize)
        self._stop = threading.Event()
        self._error: Optional[BaseException] = None
        self._thread = threading.Thread(target=self._run, daemon=True)
        self._thread.start()

    def _put(self, item: object) -> bool:
        while not self._stop.is_set():
            try:
                self._q.put(item, timeout=0.2)
                return True
            except queue.Full:
                continue
        return False

    def _run(self) -> None:
        try:
            while not self._stop.is_set():
                frame = self._produce()
                if frame is None:
                    break
                if not self._put(frame):
                    break
        except BaseException as e:  # surfaced via .error
            self._error = e
        finally:
            self._put(self._END)

    @property
    def error(self) -> Optional[BaseException]:
        return self._error

    def read(self) -> Optional[np.ndarray]:
        item = self._q.get()
        if item is self._END:
            self._q.put(self._END)  # keep returning None on further reads
            return None
        return item  # type: ignore[return-value]

    def close(self) -> None:
        self._stop.set()
        try:
            while True:
                self._q.get_nowait()
        except queue.Empty:
            pass
        try:
            self._cleanup()
        except Exception:
            pass
        self._thread.join(timeout=5)


def open_opencv_reader(path: str, target_wh: Optional[Tuple[int, int]] = None) -> _ThreadedReader:
    """Grayscale frames via OpenCV; resized to target_wh (w, h) when given and different."""
    cap = cv2.VideoCapture(path)
    if not cap.isOpened():
        raise RuntimeError(f"OpenCV cannot open video: {path}")

    def produce() -> Optional[np.ndarray]:
        ok, frame = cap.read()
        if not ok or frame is None:
            return None
        gray = frame if frame.ndim == 2 else cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
        if target_wh and (gray.shape[1], gray.shape[0]) != tuple(target_wh):
            interp = cv2.INTER_AREA if gray.shape[1] > target_wh[0] else cv2.INTER_LINEAR
            gray = cv2.resize(gray, tuple(target_wh), interpolation=interp)
        return gray

    return _ThreadedReader(produce, cap.release)


_FPS_MODE_SUPPORT: dict = {}


def _ffmpeg_passthrough_args(ffmpeg_exe: str) -> List[str]:
    """`-fps_mode passthrough` (FFmpeg >= 5.1), else legacy `-vsync passthrough`."""
    if ffmpeg_exe not in _FPS_MODE_SUPPORT:
        ok = False
        try:
            r = subprocess.run(
                [ffmpeg_exe, "-hide_banner", "-v", "error", "-f", "lavfi", "-i", "nullsrc=s=16x16:d=0.04",
                 "-fps_mode", "passthrough", "-f", "null", "-"],
                capture_output=True, timeout=20, creationflags=_NO_WINDOW,
            )
            ok = r.returncode == 0
        except (OSError, subprocess.SubprocessError):
            ok = False
        _FPS_MODE_SUPPORT[ffmpeg_exe] = ok
    return ["-fps_mode", "passthrough"] if _FPS_MODE_SUPPORT[ffmpeg_exe] else ["-vsync", "passthrough"]


def open_ffmpeg_reader(
    path: str,
    ffmpeg_exe: str,
    target_wh: Tuple[int, int],
    hwaccel: bool = False,
) -> _ThreadedReader:
    """
    Grayscale frames at target_wh straight from ffmpeg (scaling + gray conversion in ffmpeg's threads).
    out_range=full keeps luma levels close to OpenCV's BGR→gray so preview and analysis agree.
    """
    w, h = int(target_wh[0]), int(target_wh[1])
    cmd = [ffmpeg_exe, "-hide_banner", "-nostdin", "-v", "error"]
    if hwaccel:
        cmd += ["-hwaccel", "auto"]
    cmd += [
        "-i", path,
        "-map", "0:v:0",
        "-vf", f"scale=w={w}:h={h}:flags=area:out_range=full,format=gray",
        *_ffmpeg_passthrough_args(ffmpeg_exe),
        "-f", "rawvideo", "-pix_fmt", "gray", "-",
    ]
    proc = subprocess.Popen(
        cmd,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        stdin=subprocess.DEVNULL,
        creationflags=_NO_WINDOW,
        bufsize=0,
    )
    frame_bytes = w * h
    stderr_tail: deque = deque(maxlen=12)

    def drain_stderr() -> None:
        assert proc.stderr is not None
        for raw in proc.stderr:
            stderr_tail.append(raw.decode("utf-8", errors="replace").rstrip())

    threading.Thread(target=drain_stderr, daemon=True).start()

    def produce() -> Optional[np.ndarray]:
        assert proc.stdout is not None
        buf = bytearray(frame_bytes)
        view = memoryview(buf)
        got = 0
        while got < frame_bytes:
            n = proc.stdout.readinto(view[got:])
            if not n:
                break
            got += n
        if got < frame_bytes:
            rc = proc.poll()
            if rc not in (None, 0) and got == 0 and stderr_tail:
                raise RuntimeError("ffmpeg decode failed: " + " | ".join(stderr_tail))
            return None
        return np.frombuffer(buf, dtype=np.uint8).reshape(h, w)

    def cleanup() -> None:
        if proc.poll() is None:
            proc.kill()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            pass
        for s in (proc.stdout, proc.stderr):
            try:
                if s:
                    s.close()
            except OSError:
                pass

    return _ThreadedReader(produce, cleanup)


class PeekableReader:
    """Lets the sync detector look ahead without losing frames for the main analysis."""

    def __init__(self, reader: _ThreadedReader):
        self._reader = reader
        self._buf: deque = deque()
        self.consumed = 0

    def peek(self, n: int) -> List[np.ndarray]:
        while len(self._buf) < n:
            f = self._reader.read()
            if f is None:
                break
            self._buf.append(f)
        return list(self._buf)[:n]

    def read(self) -> Optional[np.ndarray]:
        f = self._buf.popleft() if self._buf else self._reader.read()
        if f is not None:
            self.consumed += 1
        return f

    def skip(self, n: int) -> int:
        skipped = 0
        for _ in range(max(0, n)):
            if self.read() is None:
                break
            skipped += 1
        return skipped

    @property
    def error(self) -> Optional[BaseException]:
        return self._reader.error

    def close(self) -> None:
        self._buf.clear()
        self._reader.close()


# ——— Sync offset ———


def detect_sync_offset(
    src_frames: Sequence[np.ndarray],
    df_frames: Sequence[np.ndarray],
    max_offset: int,
    min_overlap: int = 24,
) -> Tuple[int, float, float]:
    """
    Find shift s so that deepfake frame k matches source frame k + s.
    Returns (shift, score_at_0, best_score); shift is 0 unless clearly better than no shift.
    Score = median over frame pairs of mean absolute difference (small thumbnails, robust to the swapped face).
    """
    if not src_frames or not df_frames or max_offset <= 0:
        return 0, 0.0, 0.0

    def thumb(f: np.ndarray) -> np.ndarray:
        h, w = f.shape[:2]
        tw = min(160, w)
        th = max(2, int(round(h * tw / float(w))))
        return cv2.resize(f, (tw, th), interpolation=cv2.INTER_AREA).astype(np.int16)

    ts = [thumb(f) for f in src_frames]
    td = [thumb(f) for f in df_frames]
    if ts[0].shape != td[0].shape:
        td = [cv2.resize(t.astype(np.uint8), (ts[0].shape[1], ts[0].shape[0])).astype(np.int16) for t in td]

    scores = {}
    for s in range(-max_offset, max_offset + 1):
        vals = []
        for k in range(len(td)):
            j = k + s
            if 0 <= j < len(ts):
                vals.append(float(np.mean(np.abs(ts[j] - td[k]))))
        if len(vals) >= min_overlap:
            scores[s] = float(np.median(vals))
    if 0 not in scores or len(scores) < 2:
        return 0, scores.get(0, 0.0), scores.get(0, 0.0)
    best = min(scores, key=lambda s: (scores[s], abs(s)))
    s0 = scores[0]
    sb = scores[best]
    # Static intros or near-identical content give no signal — only shift on a clear win.
    if best != 0 and sb < 0.75 * s0 and (s0 - sb) > 0.5:
        return best, s0, sb
    return 0, s0, sb


# ——— Analysis driver ———


@dataclass
class AnalysisResult:
    metric: List[int]
    total_changed: List[int]
    src_offset: int = 0
    df_offset: int = 0
    width: int = 0
    height: int = 0
    reader: str = "opencv"
    seconds: float = 0.0
    sync_scores: Tuple[float, float] = (0.0, 0.0)

    @property
    def frames(self) -> int:
        return len(self.metric)


def analyze_pair(
    source_video: str,
    deepfake_video: str,
    params: AnalysisParams,
    *,
    src_wh: Tuple[int, int],
    ffmpeg_exe: Optional[str],
    total_hint: int = 0,
    progress: Optional[Callable[[int, int], None]] = None,
    log: Callable[[str], None] = print,
) -> AnalysisResult:
    p = params.normalized()
    t0 = time.time()
    src_w, src_h = int(src_wh[0] or 0), int(src_wh[1] or 0)
    if src_w <= 0 or src_h <= 0:
        cap = cv2.VideoCapture(source_video)
        try:
            src_w = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH) or 0)
            src_h = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT) or 0)
        finally:
            cap.release()

    if p.mode == "blob":
        aw, ah = analysis_size(src_w, src_h, p.analysis_width)
    else:
        aw, ah = src_w, src_h

    use_ffmpeg = p.mode == "blob" and bool(ffmpeg_exe) and aw > 0 and ah > 0
    if use_ffmpeg:
        rs = open_ffmpeg_reader(source_video, ffmpeg_exe, (aw, ah), p.hwaccel)
        rd = open_ffmpeg_reader(deepfake_video, ffmpeg_exe, (aw, ah), p.hwaccel)
        reader_name = "ffmpeg"
    else:
        target_s = (aw, ah) if (p.mode == "blob" and aw > 0) else None
        rs = open_opencv_reader(source_video, target_s)
        if target_s is None:
            # Classic: deepfake follows the source size (first frame decides when probing failed).
            first = rs.read()
            if first is None:
                rs.close()
                return AnalysisResult([], [], reader="opencv", seconds=time.time() - t0)
            aw, ah = first.shape[1], first.shape[0]
            rs = _prepend(rs, first)
        rd = open_opencv_reader(deepfake_video, (aw, ah))
        reader_name = "opencv"

    src = PeekableReader(rs)
    df = PeekableReader(rd)
    metric: List[int] = []
    totals: List[int] = []
    src_off = df_off = 0
    sync_scores = (0.0, 0.0)
    expected = max(0, int(total_hint))
    try:
        if p.auto_sync and p.max_sync_offset > 0:
            n = 72 + p.max_sync_offset
            shift, s0, sb = detect_sync_offset(src.peek(n), df.peek(n), p.max_sync_offset)
            sync_scores = (s0, sb)
            if shift > 0:
                src_off = src.skip(shift)
            elif shift < 0:
                df_off = df.skip(-shift)

        measure = (
            (lambda a, b: blob_metric(a, b, p.noise, p.blur))
            if p.mode == "blob"
            else (lambda a, b: classic_metric(a, b, p.noise))
        )
        expected = max(0, int(total_hint) - max(src_off, df_off))
        while True:
            fs = src.read()
            fd = df.read()
            if fs is None or fd is None:
                break
            m, t = measure(fs, fd)
            metric.append(m)
            totals.append(t)
            if progress and (len(metric) % 30 == 0):
                progress(len(metric), expected)
        for r in (src, df):
            if r.error is not None:
                raise RuntimeError(str(r.error))
    finally:
        src.close()
        df.close()
    if progress:
        progress(len(metric), max(expected, len(metric)))

    return AnalysisResult(
        metric=metric,
        total_changed=totals,
        src_offset=src_off,
        df_offset=df_off,
        width=aw,
        height=ah,
        reader=reader_name,
        seconds=time.time() - t0,
        sync_scores=sync_scores,
    )


class _prepend:
    """Reader wrapper that yields one already-read frame first."""

    def __init__(self, reader: _ThreadedReader, first: np.ndarray):
        self._reader = reader
        self._first: Optional[np.ndarray] = first

    def read(self) -> Optional[np.ndarray]:
        if self._first is not None:
            f, self._first = self._first, None
            return f
        return self._reader.read()

    @property
    def error(self) -> Optional[BaseException]:
        return self._reader.error

    def close(self) -> None:
        self._reader.close()


# ——— Metric → segments (pure) ———


def _runs(mask: Sequence[bool]) -> Iterable[Tuple[int, int, bool]]:
    """Half-open runs (start, end, value)."""
    n = len(mask)
    if n == 0:
        return
    start = 0
    cur = bool(mask[0])
    for i in range(1, n):
        v = bool(mask[i])
        if v != cur:
            yield start, i, cur
            start = i
            cur = v
    yield start, n, cur


def diff_flags(metric: Sequence[int], pixel_threshold: int, pixel_max: int = 0) -> List[bool]:
    """True = swap visible (frame differs). pixel_max > 0 ignores frames that differ too much (global change)."""
    thr = int(pixel_threshold)
    mx = int(pixel_max or 0)
    if mx > 0:
        return [thr < m <= mx for m in metric]
    return [m > thr for m in metric]


def drop_short_true_runs(flags: Sequence[bool], min_len: int) -> List[bool]:
    """Diff runs shorter than min_len frames are treated as noise spikes (no difference)."""
    out = list(flags)
    if min_len <= 1:
        return out
    for a, b, v in _runs(flags):
        if v and (b - a) < min_len:
            for i in range(a, b):
                out[i] = False
    return out


def keep_from_flags(flags: Sequence[bool], buffer_frames: int) -> List[bool]:
    """
    Legacy Oxco rule: frames with a difference are kept; stretches without difference are kept only when
    they last at least buffer_frames (e.g. no face in shot) — shorter ones are swap dropouts and get cut.
    """
    keep = [True] * len(flags)
    if buffer_frames <= 0:
        return keep
    for a, b, v in _runs(flags):
        if not v and (b - a) < buffer_frames:
            for i in range(a, b):
                keep[i] = False
    return keep


def pad_cuts(keep: Sequence[bool], padding: int) -> List[bool]:
    """Grow every cut by `padding` frames on both sides (the swap usually fades in/out)."""
    out = list(keep)
    if padding <= 0:
        return out
    n = len(keep)
    for a, b, v in _runs(keep):
        if not v:
            for i in range(max(0, a - padding), min(n, b + padding)):
                out[i] = False
    return out


def drop_short_keeps(keep: Sequence[bool], min_len: int) -> List[bool]:
    """Kept islands shorter than min_len between/next to cuts would only flash — cut them too."""
    out = list(keep)
    if min_len <= 1:
        return out
    runs = list(_runs(keep))
    if len(runs) <= 1:
        return out
    for a, b, v in runs:
        if v and (b - a) < min_len:
            for i in range(a, b):
                out[i] = False
    return out


def mask_to_seqs(keep: Sequence[bool]) -> List[Tuple[int, int, bool]]:
    return [(a, b, v) for a, b, v in _runs(keep)]


def compute_segments(metric: Sequence[int], fp: FilterParams) -> Tuple[List[Tuple[int, int, bool]], List[bool]]:
    """Returns (all_seqs, diff_flags) — all_seqs are half-open (start, end, keep) over analysis frame indices."""
    flags = diff_flags(metric, fp.pixel_threshold, fp.pixel_max)
    flags = drop_short_true_runs(flags, fp.min_diff_run)
    keep = keep_from_flags(flags, fp.buffer_frames)
    keep = pad_cuts(keep, fp.cut_padding)
    keep = drop_short_keeps(keep, fp.min_keep_frames)
    return mask_to_seqs(keep), flags


def shift_seqs(seqs: Sequence[Tuple[int, int, bool]], offset: int) -> List[Tuple[int, int, bool]]:
    """Map analysis-index segments to frame numbers of one file (skipped leading frames)."""
    if not offset:
        return list(seqs)
    return [(a + offset, b + offset, v) for a, b, v in seqs]


def segment_summary(seqs: Sequence[Tuple[int, int, bool]]) -> Tuple[int, int, int]:
    """(kept_frames, cut_frames, cut_count)."""
    kept = sum(b - a for a, b, v in seqs if v)
    cut = sum(b - a for a, b, v in seqs if not v)
    return kept, cut, sum(1 for _a, _b, v in seqs if not v)


# ——— Metrics cache ———


def metrics_cache_dir() -> str:
    d = (os.environ.get("OXCO_METRICS_DIR") or "").strip()
    if not d:
        d = os.path.join(tempfile.gettempdir(), "oxco_metrics")
    return d


def _file_sig(path: str) -> dict:
    st = os.stat(path)
    return {"path": os.path.normcase(os.path.abspath(path)), "size": st.st_size, "mtime_ns": st.st_mtime_ns}


def metrics_cache_path(source_video: str, deepfake_video: str, params: AnalysisParams) -> Optional[str]:
    try:
        ident = {"src": _file_sig(source_video), "df": _file_sig(deepfake_video), "params": params.cache_key()}
    except OSError:
        return None
    h = hashlib.sha1(json.dumps(ident, sort_keys=True).encode("utf-8")).hexdigest()[:20]
    stem = os.path.splitext(os.path.basename(deepfake_video))[0][:60]
    safe = "".join(c if c.isalnum() or c in "-_." else "_" for c in stem)
    return os.path.join(metrics_cache_dir(), f"{safe}_{h}.json")


def _pair_pointer_path(source_video: str, deepfake_video: str) -> str:
    key = os.path.normcase(os.path.abspath(source_video)) + "|" + os.path.normcase(os.path.abspath(deepfake_video))
    h = hashlib.sha1(key.encode("utf-8")).hexdigest()[:20]
    return os.path.join(metrics_cache_dir(), f"latest_{h}.ptr")


def find_metrics_for_pair(source_video: str, deepfake_video: str, params: Optional[AnalysisParams]) -> Tuple[Optional[str], bool]:
    """
    (path, exact): the cache file for exactly these analysis params if present, else the newest analysis of the
    pair with any params (exact=False), else (None, False).
    """
    if params is not None:
        p = metrics_cache_path(source_video, deepfake_video, params)
        if p and os.path.isfile(p):
            return p, True
    try:
        with open(_pair_pointer_path(source_video, deepfake_video), encoding="utf-8") as f:
            latest = f.read().strip()
    except OSError:
        return None, False
    return (latest, False) if latest and os.path.isfile(latest) else (None, False)


def load_metrics(path: Optional[str]) -> Optional[dict]:
    if not path or not os.path.isfile(path):
        return None
    try:
        with open(path, encoding="utf-8") as f:
            data = json.load(f)
    except (OSError, json.JSONDecodeError):
        return None
    if data.get("version") != METRICS_VERSION or not isinstance(data.get("metric"), list):
        return None
    return data


def save_metrics(path: Optional[str], payload: dict, keep_files: int = 200) -> bool:
    if not path:
        return False
    try:
        os.makedirs(os.path.dirname(path), exist_ok=True)
        tmp = path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(payload, f, separators=(",", ":"))
        os.replace(tmp, path)
        src, df = payload.get("source_video"), payload.get("deepfake_video")
        if src and df:
            ptr = _pair_pointer_path(src, df)
            with open(ptr + ".tmp", "w", encoding="utf-8") as f:
                f.write(path)
            os.replace(ptr + ".tmp", ptr)
    except OSError:
        return False
    _prune_cache(os.path.dirname(path), keep_files)
    return True


def _prune_cache(folder: str, keep_files: int) -> None:
    try:
        files = [os.path.join(folder, n) for n in os.listdir(folder) if n.endswith(".json")]
        if len(files) <= keep_files:
            return
        files.sort(key=lambda p: os.path.getmtime(p))
        for p in files[: len(files) - keep_files]:
            try:
                os.remove(p)
            except OSError:
                pass
    except OSError:
        pass


def build_metrics_payload(
    source_video: str,
    deepfake_video: str,
    params: AnalysisParams,
    result: AnalysisResult,
    fps_float: float,
) -> dict:
    p = params.normalized()
    return {
        "version": METRICS_VERSION,
        "source_video": os.path.abspath(source_video),
        "deepfake_video": os.path.abspath(deepfake_video),
        "params": p.cache_key(),
        "fps": float(fps_float),
        "width": result.width,
        "height": result.height,
        "reader": result.reader,
        "src_offset": result.src_offset,
        "df_offset": result.df_offset,
        "analysis_seconds": round(result.seconds, 2),
        "metric": [int(v) for v in result.metric],
        "total_changed": [int(v) for v in result.total_changed],
    }


def result_from_payload(data: dict) -> AnalysisResult:
    return AnalysisResult(
        metric=[int(v) for v in data.get("metric") or []],
        total_changed=[int(v) for v in data.get("total_changed") or []],
        src_offset=int(data.get("src_offset") or 0),
        df_offset=int(data.get("df_offset") or 0),
        width=int(data.get("width") or 0),
        height=int(data.get("height") or 0),
        reader=str(data.get("reader") or "cache"),
        seconds=0.0,
    )


# ——— Preview helpers (single frame, same math as the analysis) ———


def to_gray(frame_bgr: np.ndarray) -> np.ndarray:
    if frame_bgr.ndim == 2:
        return frame_bgr
    if frame_bgr.shape[2] == 4:
        return cv2.cvtColor(frame_bgr, cv2.COLOR_BGRA2GRAY)
    return cv2.cvtColor(frame_bgr, cv2.COLOR_BGR2GRAY)


def preview_measure(
    frame_a_bgr: np.ndarray,
    frame_b_bgr: np.ndarray,
    params: AnalysisParams,
) -> Tuple[int, int, np.ndarray, Optional[Tuple[int, int, int, int]]]:
    """
    Returns (metric, total_changed, mask_at_frame_a_size, largest_blob_box_at_frame_a_size).
    frame_b must already be resized to frame_a's size.
    """
    p = params.normalized()
    ga = to_gray(frame_a_bgr)
    gb = to_gray(frame_b_bgr)
    h, w = ga.shape[:2]
    if p.mode != "blob":
        diff = cv2.absdiff(ga, gb)
        _, mask = cv2.threshold(diff, p.noise, 255, cv2.THRESH_BINARY)
        n = int(cv2.countNonZero(mask))
        return n, n, mask, None
    aw, ah = analysis_size(w, h, p.analysis_width)
    sa = cv2.resize(ga, (aw, ah), interpolation=cv2.INTER_AREA) if (aw, ah) != (w, h) else ga
    sb = cv2.resize(gb, (aw, ah), interpolation=cv2.INTER_AREA) if (aw, ah) != (w, h) else gb
    small = blob_mask(sa, sb, p.noise, p.blur)
    total = int(cv2.countNonZero(small))
    area, box = largest_blob(small)
    mask = cv2.resize(small, (w, h), interpolation=cv2.INTER_NEAREST) if (aw, ah) != (w, h) else small
    if box is not None and (aw, ah) != (w, h):
        fx, fy = w / float(aw), h / float(ah)
        box = (int(box[0] * fx), int(box[1] * fy), int(box[2] * fx), int(box[3] * fy))
    return area, total, mask, box
