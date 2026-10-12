#!/usr/bin/env python3
"""Oxco compare preview: probe + rendered frame (side-by-side / diff overlay)."""
from __future__ import annotations

import argparse
import base64
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))

from bridge_io import configure_stdio, emit

configure_stdio()


def _projects_root() -> Path:
    raw = os.environ.get("HAIL_MARY_PROJECTS_ROOT", "").strip()
    if raw:
        return Path(raw)
    return Path.home() / "Projects"


def _oxco_dir() -> Path:
    """Vendored Oxco-Quellen (im Paket enthalten) bevorzugen, sonst externes Repo."""
    vendored = ROOT / "vendor" / "oxco"
    if (vendored / "oxco_workers.py").is_file():
        return vendored
    return _projects_root() / "Oxco"


def _import_oxco():
    oxco_dir = _oxco_dir()
    sys.path.insert(0, str(oxco_dir))
    import oxco_analysis as oa  # noqa: E402
    import oxco_workers as ow  # noqa: E402

    return oa, ow


def cmd_probe(data: dict, out_path: Path) -> int:
    _, ow = _import_oxco()
    path_a = (data.get("path_a") or "").strip()
    path_b = (data.get("path_b") or "").strip()
    if not path_a:
        emit("ERROR: path_a fehlt")
        return 1
    pa = Path(path_a)
    if not pa.is_file():
        emit(f"ERROR: Datei nicht gefunden: {pa}")
        return 1

    meta_a = ow.probe_preview_media(pa)
    meta_b = None
    path_b_resolved = ""
    if path_b:
        pb = Path(path_b)
        if pb.is_file():
            meta_b = ow.probe_preview_media(pb)
            path_b_resolved = str(pb)

    def _meta_dict(m):
        if m is None:
            return None
        return {
            "duration_sec": m.duration_sec,
            "fps": m.fps,
            "frame_count": m.frame_count,
            "width": m.width,
            "height": m.height,
        }

    out = {
        "path_a": str(pa),
        "path_b": path_b_resolved,
        "meta_a": _meta_dict(meta_a),
        "meta_b": _meta_dict(meta_b),
        "frame_count": meta_a.frame_count if meta_a else 0,
        "width": meta_a.width if meta_a else 0,
        "height": meta_a.height if meta_a else 0,
        "name_a": pa.name,
    }
    out_path.write_text(json.dumps(out, ensure_ascii=False), encoding="utf-8")
    return 0


def _to_bgr(frame):
    import cv2  # noqa: E402

    if frame is None:
        return None
    if frame.ndim == 2:
        return cv2.cvtColor(frame, cv2.COLOR_GRAY2BGR)
    if frame.shape[2] == 4:
        return cv2.cvtColor(frame, cv2.COLOR_BGRA2BGR)
    return frame


def _overlay(base_bgr, mask, box, opacity: float = 0.5):
    """Magenta = changed pixels (after noise filter); green box = largest region (blob mode)."""
    import cv2  # noqa: E402
    import numpy as np  # noqa: E402

    tint = cv2.merge([mask, np.zeros_like(mask), mask])
    out = cv2.addWeighted(base_bgr, 1.0, tint, opacity, 0)
    if box is not None:
        x, y, w, h = box
        cv2.rectangle(out, (x, y), (x + w, y + h), (0, 220, 0), 2)
    return out


def preview_params(oa, data: dict):
    return oa.AnalysisParams(
        mode=str(data.get("analysis_mode") or "classic"),
        noise=int(data.get("noise", 15)),
        analysis_width=int(data.get("analysis_width", 640) or 640),
        blur=int(data.get("analysis_blur", 3) or 0),
    ).normalized()


def compose_preview(oa, fa, fb, data: dict) -> dict:
    """
    Side-by-side / overlay image + the same measurement the analysis uses.
    fa/fb: BGR frames (fb may be None). Returns the JSON payload (without frame_index/frame_count).
    """
    import cv2  # noqa: E402
    import numpy as np  # noqa: E402

    pixel_threshold = int(data.get("pixel_threshold", 200))
    pixel_max = int(data.get("pixel_max", 0) or 0)
    side_by_side = bool(data.get("side_by_side", False))
    overlay = bool(data.get("overlay", True))
    max_width = int(data.get("max_width", 1280))
    params = preview_params(oa, data)

    fa = _to_bgr(fa)
    ho, wo = fa.shape[:2]
    fb = _to_bgr(fb) if fb is not None else None
    if fb is not None and (fb.shape[1], fb.shape[0]) != (wo, ho):
        fb = cv2.resize(fb, (wo, ho), interpolation=cv2.INTER_LINEAR)

    diff_count = 0
    total_changed = 0
    diff_over = False
    mask = None
    box = None
    if fb is not None:
        diff_count, total_changed, mask, box = oa.preview_measure(fa, fb, params)
        diff_over = diff_count > pixel_threshold and (pixel_max <= 0 or diff_count <= pixel_max)

    if side_by_side and fb is not None:
        left, right = fa.copy(), fb.copy()
        if overlay and mask is not None:
            left = _overlay(left, mask, box)
            right = _overlay(right, mask, box)
        cv2.putText(left, "A", (16, 40), cv2.FONT_HERSHEY_SIMPLEX, 1.0, (0, 220, 0), 2)
        cv2.putText(right, "B", (16, 40), cv2.FONT_HERSHEY_SIMPLEX, 1.0, (60, 60, 255), 2)
        out_bgr = np.hstack([left, right])
    else:
        out_bgr = fa.copy()
        if overlay and mask is not None:
            out_bgr = _overlay(out_bgr, mask, box)

    h, w = out_bgr.shape[:2]
    if max_width > 0 and w > max_width:
        scale = max_width / w
        out_bgr = cv2.resize(out_bgr, (max(1, int(w * scale)), max(1, int(h * scale))), interpolation=cv2.INTER_AREA)

    ok, jpg = cv2.imencode(".jpg", out_bgr, [int(cv2.IMWRITE_JPEG_QUALITY), 85])
    if not ok:
        raise RuntimeError("JPEG encode failed")
    return {
        "diff_count": diff_count,
        "total_changed": total_changed,
        "analysis_mode": params.mode,
        "diff_over_threshold": diff_over,
        "image_base64": base64.standard_b64encode(jpg.tobytes()).decode("ascii"),
        "width": int(out_bgr.shape[1]),
        "height": int(out_bgr.shape[0]),
    }


def cmd_render(data: dict, out_path: Path) -> int:
    oa, ow = _import_oxco()

    path_a = (data.get("path_a") or "").strip()
    path_b = (data.get("path_b") or "").strip()
    frame_index = int(data.get("frame_index", 0))

    if not path_a or not Path(path_a).is_file():
        emit("ERROR: path_a ungültig")
        return 1

    pa = Path(path_a)
    meta_a = ow.probe_preview_media(pa)
    meta_b = None
    pb_path = None
    if path_b and Path(path_b).is_file():
        pb_path = Path(path_b)
        meta_b = ow.probe_preview_media(pb_path)

    total = meta_a.frame_count if meta_a else 0
    if total <= 0:
        emit("ERROR: Keine Frame-Anzahl")
        return 1

    frame_index = max(0, min(frame_index, total - 1))
    fa = ow.read_preview_frame_bgr(pa, meta_a, frame_index)
    if fa is None:
        emit("ERROR: Frame konnte nicht gelesen werden")
        return 1
    fb = ow.read_preview_frame_bgr(pb_path, meta_b, frame_index) if pb_path is not None else None

    payload = {"frame_index": frame_index, "frame_count": total}
    payload.update(compose_preview(oa, fa, fb, data))
    out_path.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="Oxco preview bridge")
    parser.add_argument("--action", required=True, choices=["probe", "render"])
    parser.add_argument("--config-json", required=True)
    parser.add_argument("--output-json", required=True)
    args = parser.parse_args()

    data = json.loads(Path(args.config_json).read_text(encoding="utf-8"))
    out_path = Path(args.output_json)
    if args.action == "probe":
        rc = cmd_probe(data, out_path)
    else:
        rc = cmd_render(data, out_path)
    if rc == 0:
        emit(f"OUTPUT:{out_path}")
    return rc


if __name__ == "__main__":
    raise SystemExit(main())
