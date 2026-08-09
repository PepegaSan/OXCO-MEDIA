"""Optional target FPS helpers for Hail Mary FFmpeg bridges."""
from __future__ import annotations

from typing import Optional, Sequence


def normalize_target_fps(value) -> Optional[float]:
    """Return positive fps or None (keep source rate)."""
    try:
        fps = float(value)
    except (TypeError, ValueError):
        return None
    if fps <= 0:
        return None
    # Clamp to sane range
    if fps < 1:
        fps = 1.0
    if fps > 240:
        fps = 240.0
    return fps


def fps_output_args(target_fps) -> list[str]:
    """Args for constant output frame rate (after filters / before output file)."""
    fps = normalize_target_fps(target_fps)
    if fps is None:
        return []
    # -r sets CFR output; fps_mode cfr helps with odd source timing (e.g. 120→30)
    return ["-r", f"{fps:g}", "-fps_mode", "cfr"]


def fps_filter_clause(target_fps) -> str:
    """Filter graph fragment to append after a video chain (leading comma)."""
    fps = normalize_target_fps(target_fps)
    if fps is None:
        return ""
    return f",fps={fps:g}"


def apply_fps_to_vchains(vchains: Sequence[str], target_fps) -> list[str]:
    """Append fps filter to each trim chain like …setpts=…[v0]."""
    clause = fps_filter_clause(target_fps)
    if not clause:
        return list(vchains)
    out: list[str] = []
    for chain in vchains:
        # Insert before the final output label [vN]
        if "]" not in chain:
            out.append(chain + clause)
            continue
        head, _, label = chain.rpartition("[")
        out.append(f"{head}{clause}[{label}")
    return out
