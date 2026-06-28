#!/usr/bin/env python3
"""Oxco compare preview: probe + rendered frame (side-by-side / diff overlay)."""
from __future__ import annotations

import argparse
import base64
import json
import os
import sys
from io import BytesIO
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
    import oxco_player as op  # noqa: E402
    import oxco_workers as ow  # noqa: E402

    return op, ow


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


def cmd_render(data: dict, out_path: Path) -> int:
    op, ow = _import_oxco()
    import cv2  # noqa: E402
    import numpy as np  # noqa: E402
    from PIL import Image  # noqa: E402

    path_a = (data.get("path_a") or "").strip()
    path_b = (data.get("path_b") or "").strip()
    frame_index = int(data.get("frame_index", 0))
    noise = int(data.get("noise", 15))
    pixel_threshold = int(data.get("pixel_threshold", 200))
    side_by_side = bool(data.get("side_by_side", False))
    overlay = bool(data.get("overlay", True))
    max_width = int(data.get("max_width", 1280))

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
    fb = None
    if pb_path is not None:
        fb = ow.read_preview_frame_bgr(pb_path, meta_b, frame_index)

    if fa is None:
        emit("ERROR: Frame konnte nicht gelesen werden")
        return 1

    fa = op._to_bgr(fa)
    if fa is None:
        emit("ERROR: Frame-Konvertierung fehlgeschlagen")
        return 1

    ho, wo = fa.shape[:2]
    diff_count = 0
    diff_over = False
    if fb is not None:
        fb_u = op._to_bgr(fb)
        if fb_u is not None:
            fb_u = op._match_size(fb_u, wo, ho)
            diff_count = op._compare_diff_count(fa, fb_u, noise)
            diff_over = diff_count > pixel_threshold

    if side_by_side and fb is not None:
        fb_u = op._to_bgr(fb)
        if fb_u is not None:
            fb_u = op._match_size(fb_u, wo, ho)
            left = fa.copy()
            right = fb_u.copy()
            if overlay:
                left = op._apply_diff_overlay(left, fb_u, noise, 0.5)
                right = op._apply_diff_overlay(fb_u, fa, noise, 0.5)
            cv2.putText(left, "A", (16, 40), cv2.FONT_HERSHEY_SIMPLEX, 1.0, (0, 220, 0), 2)
            cv2.putText(right, "B", (16, 40), cv2.FONT_HERSHEY_SIMPLEX, 1.0, (60, 60, 255), 2)
            out_bgr = np.hstack([left, right])
        else:
            out_bgr = fa
    else:
        out_bgr = fa.copy()
        if fb is not None and overlay:
            fb_u = op._to_bgr(fb)
            if fb_u is not None:
                fb_u = op._match_size(fb_u, wo, ho)
                out_bgr = op._apply_diff_overlay(out_bgr, fb_u, noise, 0.5)

    rgb = cv2.cvtColor(out_bgr, cv2.COLOR_BGR2RGB)
    h, w = rgb.shape[:2]
    if max_width > 0 and w > max_width:
        scale = max_width / w
        nw = max(1, int(w * scale))
        nh = max(1, int(h * scale))
        rgb = cv2.resize(rgb, (nw, nh), interpolation=cv2.INTER_AREA)

    img = Image.fromarray(rgb)
    buf = BytesIO()
    img.save(buf, format="JPEG", quality=85)
    b64 = base64.standard_b64encode(buf.getvalue()).decode("ascii")

    payload = {
        "frame_index": frame_index,
        "frame_count": total,
        "diff_count": diff_count,
        "diff_over_threshold": diff_over,
        "image_base64": b64,
        "width": int(rgb.shape[1]),
        "height": int(rgb.shape[0]),
    }
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
