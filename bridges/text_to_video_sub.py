"""Import helpers for Text-to-Video (overlay_core) — vendored first, Sub fallback."""
from __future__ import annotations

import os
import sys
from pathlib import Path
from typing import Any, Optional

BRIDGE_DIR = Path(__file__).resolve().parent
VENDOR_DIR = BRIDGE_DIR / "vendor" / "text_to_video"


def sub_dir() -> Path:
    """Resolve the directory that contains overlay_core.py."""
    if (VENDOR_DIR / "overlay_core.py").is_file():
        return VENDOR_DIR

    root = os.environ.get("HAIL_MARY_PROJECTS_ROOT", "").strip()
    if root:
        legacy = Path(root).expanduser().resolve() / "Sub"
        if (legacy / "overlay_core.py").is_file():
            return legacy

    raise RuntimeError(
        "Text-to-Video core not found. Expected vendored "
        f"bridges/vendor/text_to_video/overlay_core.py"
        + (f" or {{ProjectsRoot}}/Sub (ProjectsRoot={root})" if root else "")
    )


def ensure_sub_imports() -> None:
    path = str(sub_dir())
    if path not in sys.path:
        sys.path.insert(0, path)


def parse_optional_seconds(text: str) -> Optional[float]:
    s = (text or "").strip().replace(",", ".")
    if not s:
        return None
    try:
        return float(s)
    except ValueError:
        return None


def _segment_bool(raw: object) -> bool:
    if isinstance(raw, bool):
        return raw
    try:
        return bool(int(raw))
    except (TypeError, ValueError):
        return False


def normalize_segment(raw: dict[str, Any]) -> dict[str, Any]:
    def _i(key: str, default: int, lo: int, hi: int) -> int:
        try:
            return max(lo, min(int(raw.get(key, default)), hi))
        except (TypeError, ValueError):
            return default

    col = str(raw.get("color") or "FFFFFF").strip().lstrip("#").upper()
    if len(col) != 6:
        col = "FFFFFF"
    box_en = True
    if "box_enabled" in raw:
        box_en = _segment_bool(raw.get("box_enabled"))
    return {
        "text": str(raw.get("text") or ""),
        "from": str(raw.get("from") or "").strip(),
        "to": str(raw.get("to") or "").strip(),
        "fontsize": _i("fontsize", 42, 12, 200),
        "color": col,
        "px": _i("px", 80, 0, 20000),
        "py": _i("py", 80, 0, 20000),
        "line_spacing": _i("line_spacing", -12, -120, 120),
        "box_border": _i("box_border", 3, 0, 40),
        "box_enabled": box_en,
        "font_path": str(raw.get("font_path") or "").strip(),
        "italic_font_path": str(raw.get("italic_font_path") or "").strip(),
        "bold": _segment_bool(raw.get("bold")),
        "italic": _segment_bool(raw.get("italic")),
        "strike": _segment_bool(raw.get("strike")),
    }


def segment_to_overlay(raw: dict[str, Any]):
    ensure_sub_imports()
    from overlay_core import TimedTextOverlay  # noqa: E402

    dn = normalize_segment(raw)
    fp = dn["font_path"]
    font_path = fp if fp and Path(fp).is_file() else None
    ifp = dn["italic_font_path"]
    italic_font_path = ifp if ifp and Path(ifp).is_file() else None
    return TimedTextOverlay(
        text=dn["text"],
        fontcolor_hex=dn["color"],
        fontsize=dn["fontsize"],
        pos_x=dn["px"],
        pos_y=dn["py"],
        box_border_w=dn["box_border"],
        box_enabled=bool(dn.get("box_enabled", True)),
        line_spacing=dn["line_spacing"],
        text_visible_from_sec=parse_optional_seconds(dn["from"]),
        text_visible_to_sec=parse_optional_seconds(dn["to"]),
        font_path=font_path,
        italic_font_path=italic_font_path,
        bold=bool(dn["bold"]),
        italic=bool(dn["italic"]),
        strike=bool(dn["strike"]),
    )


def overlays_from_config(segments: list[dict[str, Any]], draft: Optional[dict[str, Any]] = None):
    ensure_sub_imports()
    from overlay_core import overlay_segment_has_visible_text  # noqa: E402

    merged = [segment_to_overlay(s) for s in segments if isinstance(s, dict)]
    if draft and isinstance(draft, dict):
        ov = segment_to_overlay(draft)
        if overlay_segment_has_visible_text(ov):
            merged.append(ov)
    return [o for o in merged if overlay_segment_has_visible_text(o)]


def resolve_encoder(codec_setting: str) -> str:
    ensure_sub_imports()
    from overlay_core import map_codec_to_lib  # noqa: E402

    s = (codec_setting or "").strip()
    if s.startswith("lib"):
        return s
    return map_codec_to_lib(s)
