#!/usr/bin/env python3
"""
Oxco compare preview — long-running worker for Hail Mary.

One JSON request per stdin line, one JSON response per stdout line (same "id"):
  {"id": 1, "cmd": "open",    "path_a": ..., "path_b": ...}
  {"id": 2, "cmd": "frame",   "index": 120, "b_offset": 0, "noise": 15, "pixel_threshold": 200, ...}
  {"id": 3, "cmd": "metrics", "source": ..., "deepfake": ..., "analysis_mode": "blob", ...}
  {"id": 4, "cmd": "close"}   (releases the video files, e.g. before moving/recycling them)
The videos stay open between requests, so playback reads frames sequentially instead of seeking every time.
Exits when stdin closes (Hail Mary quit or restarted the worker).
"""
from __future__ import annotations

import json
import os
import sys
import traceback
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))

from bridge_io import configure_stdio  # noqa: E402
from oxco_preview_job import _import_oxco, _projects_root, compose_preview  # noqa: E402

configure_stdio()
# Same cache folder oxco_compare_job.py passes to compare.py.
os.environ.setdefault("OXCO_METRICS_DIR", str(_projects_root() / "Oxco" / "metrics_cache"))
oa, ow = _import_oxco()
import cv2  # noqa: E402


class VideoSource:
    """Keeps a capture open: sequential frames are read directly, jumps use an accurate ffmpeg seek."""

    def __init__(self, path: str):
        self.path = Path(path)
        self.meta = ow.probe_preview_media(self.path)
        self.cap = None
        self.cap_next = None  # frame index the capture returns on the next read()
        self.last_index = None
        self.last_frame = None

    @property
    def frame_count(self) -> int:
        return self.meta.frame_count if self.meta else 0

    def frame(self, index: int):
        index = max(0, min(index, max(0, self.frame_count - 1))) if self.frame_count > 0 else max(0, index)
        if index == self.last_index and self.last_frame is not None:
            return self.last_frame
        sequential = self.last_index is not None and index == self.last_index + 1
        frame = None
        if sequential:
            if self.cap is None:
                self.cap = cv2.VideoCapture(str(self.path))
            if self.cap_next != index:
                self.cap.set(cv2.CAP_PROP_POS_FRAMES, index)
            ok, f = self.cap.read()
            if ok and f is not None:
                frame = f
                self.cap_next = index + 1
            else:
                self.cap_next = None
        if frame is None:
            frame = ow.read_preview_frame_bgr(self.path, self.meta, index)
            self.cap_next = None  # capture position unknown after the random-access read
        self.last_index = index
        self.last_frame = frame
        return frame

    def close(self) -> None:
        if self.cap is not None:
            self.cap.release()
        self.cap = None
        self.cap_next = None
        self.last_frame = None
        self.last_index = None


class PreviewServer:
    def __init__(self):
        self.a: VideoSource | None = None
        self.b: VideoSource | None = None

    def close(self) -> dict:
        for v in (self.a, self.b):
            if v is not None:
                v.close()
        self.a = self.b = None
        return {}

    def open(self, req: dict) -> dict:
        path_a = (req.get("path_a") or "").strip()
        path_b = (req.get("path_b") or "").strip()
        if not path_a or not Path(path_a).is_file():
            raise ValueError(f"path_a missing: {path_a}")
        self.close()
        self.a = VideoSource(path_a)
        if path_b and Path(path_b).is_file():
            self.b = VideoSource(path_b)
        m = self.a.meta
        if m is None or self.a.frame_count <= 0:
            raise ValueError("no frame count")
        return {
            "frame_count": m.frame_count,
            "fps": m.fps,
            "duration_sec": m.duration_sec,
            "width": m.width,
            "height": m.height,
            "name_a": self.a.path.name,
            "has_b": self.b is not None,
            "frame_count_b": self.b.frame_count if self.b else 0,
        }

    def frame(self, req: dict) -> dict:
        if self.a is None:
            raise ValueError("no video open")
        index = int(req.get("index", 0))
        fa = self.a.frame(index)
        if fa is None:
            raise ValueError(f"cannot read frame {index}")
        fb = None
        if self.b is not None:
            # Detected sync offset: deepfake frame = source frame + b_offset.
            ib = index + int(req.get("b_offset", 0) or 0)
            if 0 <= ib < max(1, self.b.frame_count):
                fb = self.b.frame(ib)
        out = {"frame_index": self.a.last_index, "frame_count": self.a.frame_count}
        out.update(compose_preview(oa, fa, fb, req))
        return out

    @staticmethod
    def metrics(req: dict) -> dict:
        params = oa.AnalysisParams(
            mode=str(req.get("analysis_mode") or "classic"),
            noise=int(req.get("noise", 15)),
            analysis_width=int(req.get("analysis_width", 640) or 640),
            blur=int(req.get("analysis_blur", 3) or 0),
            auto_sync=bool(req.get("auto_sync_offset", True)),
            max_sync_offset=int(req.get("max_sync_offset_frames", 12) or 0),
        )
        path, exact = oa.find_metrics_for_pair(req.get("source") or "", req.get("deepfake") or "", params)
        return {"path": path or "", "exact": exact}


def main() -> int:
    server = PreviewServer()
    handlers = {
        "open": server.open,
        "frame": server.frame,
        "metrics": server.metrics,
        "close": lambda _req: server.close(),
    }
    for raw in sys.stdin:
        raw = raw.strip()
        if not raw:
            continue
        req_id = None
        try:
            req = json.loads(raw)
            req_id = req.get("id")
            handler = handlers.get(req.get("cmd"))
            if handler is None:
                raise ValueError(f"unknown cmd: {req.get('cmd')}")
            resp = {"id": req_id, "ok": True}
            resp.update(handler(req))
        except Exception as e:  # report and keep serving
            resp = {"id": req_id, "ok": False, "error": str(e), "trace": traceback.format_exc(limit=3)}
        sys.stdout.write(json.dumps(resp, ensure_ascii=False) + "\n")
        sys.stdout.flush()
    server.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
