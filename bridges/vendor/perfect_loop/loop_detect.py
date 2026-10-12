"""Motion-aware loop point detection.

A loop that plays frames ``s .. e-1`` and then jumps back to ``s`` is seamless when the frame
that *would* follow, ``f[e]``, looks like ``f[s]`` — and the frames around both points move the
same way. So the cost of a candidate compares short frame windows along the diagonal of the
distance matrix (``D[s+j, e+j]`` for small ``j``), not just a single image pair. Costs are
expressed in units of the clip's typical frame-to-frame change, which makes scores comparable
between calm and fast footage.
"""
from __future__ import annotations

from dataclasses import dataclass
from typing import Callable, List, Optional, Sequence, Tuple

import cv2
import numpy as np

ProgressFn = Callable[[float, str], None]

FEATURE_WIDTH = 96
COLOR_WIDTH = 16
COLOR_WEIGHT = 0.5
# Frames compared on each side of the seam (motion continuity) and their weights.
WINDOW_OFFSETS = (-2, -1, 0, 1, 2)
WINDOW_WEIGHTS = (0.5, 1.0, 2.0, 1.0, 0.5)
# Sensor/codec noise floor in gray levels; keeps static clips from exploding the ratio.
NOISE_FLOOR = 0.8
# Coarse search keeps the distance matrix small; the winner is refined at full frame rate.
MAX_COARSE_SAMPLES = 1200
# Small bonus so longer loops win between near-equal candidates.
LENGTH_BONUS = 0.15


@dataclass
class LoopCandidate:
    start: int  # first frame of the loop (relative to the analysed window)
    end: int  # exclusive: the loop jumps back after frame end-1
    cost: float  # seam mismatch in "typical frame steps" (0 = perfect)
    score: float  # 0..1, higher is better


def frame_feature(frame: np.ndarray) -> np.ndarray:
    """Compact uint8 feature: blurred gray thumbnail + coarse color thumbnail."""
    h, w = frame.shape[:2]
    gh = max(2, int(round(h * FEATURE_WIDTH / max(1, w))))
    gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY) if frame.ndim == 3 else frame
    small = cv2.resize(gray, (FEATURE_WIDTH, gh), interpolation=cv2.INTER_AREA)
    small = cv2.GaussianBlur(small, (3, 3), 0)
    parts = [small.reshape(-1)]
    if frame.ndim == 3:
        ch = max(1, int(round(h * COLOR_WIDTH / max(1, w))))
        color = cv2.resize(frame, (COLOR_WIDTH, ch), interpolation=cv2.INTER_AREA)
        parts.append(color.reshape(-1))
    return np.concatenate(parts).astype(np.uint8)


def _weighted(features: np.ndarray, gray_len: int) -> np.ndarray:
    """Scale so ||a-b|| = sqrt(MSE_gray + COLOR_WEIGHT * MSE_color) in gray levels."""
    f = features.astype(np.float32)
    n_color = f.shape[1] - gray_len
    f[:, :gray_len] *= 1.0 / np.sqrt(gray_len)
    if n_color > 0:
        f[:, gray_len:] *= np.sqrt(COLOR_WEIGHT / n_color)
    return f


def _gray_len(sample_frame_shape: Tuple[int, int]) -> int:
    h, w = sample_frame_shape
    return FEATURE_WIDTH * max(2, int(round(h * FEATURE_WIDTH / max(1, w))))


def pairwise_distances(a: np.ndarray, b: np.ndarray) -> np.ndarray:
    sq = (a * a).sum(1)[:, None] + (b * b).sum(1)[None, :] - 2.0 * (a @ b.T)
    return np.sqrt(np.maximum(sq, 0.0))


def typical_step(weighted: np.ndarray) -> float:
    """Median frame-to-frame change (gray levels), clamped by the noise floor."""
    if len(weighted) < 2:
        return NOISE_FLOOR
    steps = np.linalg.norm(np.diff(weighted, axis=0), axis=1)
    return float(max(NOISE_FLOOR, np.median(steps)))


def cost_to_score(cost: float) -> float:
    """0 → 1.0; one frame step of mismatch → ~0.8; five steps → ~0.25."""
    c = max(0.0, float(cost))
    return float(1.0 / (1.0 + (c / 2.5) ** 1.5))


def _diagonal_cost(dist: np.ndarray, offsets: Sequence[int], weights: Sequence[float]) -> np.ndarray:
    """cost[s, e] = weighted mean of dist[s+j, e+j] over the offsets that stay in range."""
    n = dist.shape[0]
    total = np.zeros_like(dist)
    wsum = np.zeros_like(dist)
    for j, w in zip(offsets, weights):
        shifted = np.full_like(dist, np.nan)
        lo, hi = max(0, -j), min(n, n - j)
        shifted[lo:hi, lo:hi] = dist[lo + j : hi + j, lo + j : hi + j]
        valid = ~np.isnan(shifted)
        total[valid] += w * shifted[valid]
        wsum[valid] += w
    with np.errstate(invalid="ignore", divide="ignore"):
        out = total / wsum
    out[wsum == 0] = np.inf
    return out


def _seam_cost_lookup(get: Callable[[int], Optional[np.ndarray]], start: int, end: int, step: float) -> float:
    total = 0.0
    wsum = 0.0
    for j, w in zip(WINDOW_OFFSETS, WINDOW_WEIGHTS):
        a, b = get(start + j), get(end + j)
        if a is not None and b is not None:
            total += w * float(np.linalg.norm(a - b))
            wsum += w
    if wsum > 0:
        return total / wsum / step
    # f[end] is not available (range ends at the last frame): judge the actual jump instead.
    # A perfect jump looks like one normal step; a freeze (0) or a big jump both cost.
    last, first = get(end - 1), get(start)
    if last is None or first is None:
        return float("inf")
    jump = float(np.linalg.norm(last - first)) / step
    return abs(jump - 1.0)


def seam_cost(weighted: np.ndarray, start: int, end: int, step: float) -> float:
    """Seam mismatch for playing ``start..end-1`` then ``start``, in frame steps."""
    n = len(weighted)
    return _seam_cost_lookup(lambda i: weighted[i] if 0 <= i < n else None, start, end, step)


def find_loop_candidates(
    features: np.ndarray,
    frame_shape: Tuple[int, int],
    fps: float,
    *,
    min_loop_seconds: float = 1.5,
    max_candidates: int = 5,
    progress: Optional[ProgressFn] = None,
) -> List[LoopCandidate]:
    """Best loop windows inside the analysed frames, best first."""
    n = len(features)
    weighted = _weighted(features, _gray_len(frame_shape))
    step = typical_step(weighted)
    min_len = max(4, int(round(min_loop_seconds * fps)))
    if n < min_len + 1:
        cost = seam_cost(weighted, 0, n, step)
        return [LoopCandidate(0, n, cost, cost_to_score(cost))]

    stride = max(1, int(np.ceil(n / MAX_COARSE_SAMPLES)))
    idx = np.arange(0, n, stride)
    coarse = weighted[idx]
    if progress:
        progress(55.0, "Abstände berechnen…")
    dist = pairwise_distances(coarse, coarse)
    # Offsets in coarse units: ±1 sample still captures motion direction.
    cost = _diagonal_cost(dist, (-1, 0, 1), (1.0, 2.0, 1.0)) / step

    m = len(idx)
    s_grid, e_grid = np.meshgrid(np.arange(m), np.arange(m), indexing="ij")
    length = (e_grid - s_grid) * stride
    objective = cost - LENGTH_BONUS * (length / max(1, n))
    objective[length < min_len] = np.inf
    # f[e] must exist for the strong (successor) comparison.
    objective[:, idx >= n - 1] = np.inf

    suppress = max(1, int(round(0.4 * fps / stride)))
    candidates: List[LoopCandidate] = []
    work = objective.copy()
    for _ in range(max_candidates):
        flat = int(np.argmin(work))
        cs, ce = divmod(flat, m)
        if not np.isfinite(work[cs, ce]):
            break
        s, e = _refine(weighted, int(idx[cs]), int(idx[ce]), stride, min_len, step)
        c = seam_cost(weighted, s, e, step)
        candidates.append(LoopCandidate(s, e, c, cost_to_score(c)))
        # Alternatives should differ in length — the same loop shifted in time is not a real choice.
        work[np.abs(length - (e - s)) <= suppress * stride] = np.inf

    candidates.sort(key=lambda c: c.cost - LENGTH_BONUS * ((c.end - c.start) / max(1, n)))
    return candidates


def _refine(weighted: np.ndarray, s0: int, e0: int, radius: int, min_len: int, step: float) -> Tuple[int, int]:
    """Exact frame search around a coarse hit."""
    n = len(weighted)
    if radius <= 1:
        return s0, e0
    best = (np.inf, s0, e0)
    for s in range(max(0, s0 - radius), min(n, s0 + radius + 1)):
        for e in range(max(s + min_len, e0 - radius), min(n - 1, e0 + radius) + 1):
            c = seam_cost(weighted, s, e, step)
            if c < best[0]:
                best = (c, s, e)
    return best[1], best[2]


def score_sparse(
    features_by_index: dict,
    frame_shape: Tuple[int, int],
    start: int,
    end: int,
) -> Tuple[float, float]:
    """Seam cost for a fixed range from frame windows around its ends.

    ``features_by_index`` maps absolute frame index → feature. Returns (cost, typical step).
    Only consecutive indices contribute to the typical step, so window gaps never count.
    """
    keys = sorted(features_by_index)
    stacked = _weighted(np.stack([features_by_index[k] for k in keys]), _gray_len(frame_shape))
    lookup = {k: stacked[i] for i, k in enumerate(keys)}
    steps = [
        float(np.linalg.norm(lookup[k + 1] - lookup[k]))
        for k in keys
        if k + 1 in lookup
    ]
    step = float(max(NOISE_FLOOR, np.median(steps))) if steps else NOISE_FLOOR
    return _seam_cost_lookup(lookup.get, start, end, step), step


def motion_level(features: np.ndarray, frame_shape: Tuple[int, int]) -> float:
    """Typical frame step in gray levels (used to pick seam methods)."""
    return typical_step(_weighted(features, _gray_len(frame_shape)))
