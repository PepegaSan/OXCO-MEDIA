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
    """Sekunden aus ``12.5``, ``30s``, ``1:30`` oder ``1:02:03.5`` (wie TimeFieldHelper in der App)."""
    s = (text or "").strip().replace(",", ".")
    if not s:
        return None
    if s.lower().endswith("s") and ":" not in s:
        s = s[:-1].strip()
    try:
        if ":" in s:
            total = 0.0
            for part in s.split(":"):
                total = total * 60 + float(part)
            return total if total >= 0 else None
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

    def _hex(key: str, default: str) -> str:
        v = str(raw.get(key) or default).strip().lstrip("#").upper()
        return v if len(v) == 6 else default

    align = str(raw.get("align") or "left").strip().lower()
    animation = str(raw.get("animation") or "none").strip().lower()
    try:
        anim_duration = max(0.05, min(float(raw.get("anim_duration", 0.4)), 5.0))
    except (TypeError, ValueError):
        anim_duration = 0.4
    return {
        "text": str(raw.get("text") or ""),
        "from": str(raw.get("from") or "").strip(),
        "to": str(raw.get("to") or "").strip(),
        "fontsize": _i("fontsize", 42, 12, 200),
        "color": col,
        "px": _i("px", 80, 0, 20000),
        "py": _i("py", 80, 0, 20000),
        "layout_width": _i("layout_width", 0, 0, 8192),
        "layout_height": _i("layout_height", 0, 0, 8192),
        "line_spacing": _i("line_spacing", 0, -120, 120),
        "box_border": _i("box_border", 3, 0, 40),
        "box_enabled": box_en,
        "font_path": str(raw.get("font_path") or "").strip(),
        "italic_font_path": str(raw.get("italic_font_path") or "").strip(),
        "bold": _segment_bool(raw.get("bold")),
        "italic": _segment_bool(raw.get("italic")),
        "strike": _segment_bool(raw.get("strike")),
        "align": align if align in ("left", "center", "right") else "left",
        "outline_width": _i("outline_width", 0, 0, 40),
        "outline_color": _hex("outline_color", "000000"),
        "shadow": _segment_bool(raw.get("shadow")),
        "box_color": _hex("box_color", "000000"),
        "box_opacity": _i("box_opacity", 35, 0, 100),
        "box_radius": _i("box_radius", 10, 0, 200),
        "animation": animation if animation in ("none", "fade", "slide", "pop") else "none",
        "anim_duration": anim_duration,
    }


def segment_to_overlay(raw: dict[str, Any]):
    ensure_sub_imports()
    from overlay_core import TimedTextOverlay  # noqa: E402

    dn = normalize_segment(raw)
    # Fehlende Schriften nicht stillschweigend verwerfen — der Renderer meldet sie als Warnung.
    font_path = dn["font_path"] or None
    ifp = dn["italic_font_path"]
    italic_font_path = ifp if ifp and Path(ifp).is_file() else None
    return TimedTextOverlay(
        text=dn["text"],
        fontcolor_hex=dn["color"],
        fontsize=dn["fontsize"],
        pos_x=dn["px"],
        pos_y=dn["py"],
        layout_width=dn["layout_width"],
        layout_height=dn["layout_height"],
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
        align=dn["align"],
        outline_width=dn["outline_width"],
        outline_hex=dn["outline_color"],
        shadow=bool(dn["shadow"]),
        box_hex=dn["box_color"],
        box_opacity=dn["box_opacity"],
        box_radius=dn["box_radius"],
        animation=dn["animation"],
        anim_duration=dn["anim_duration"],
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
    """Map UI label or wire id → ffmpeg -c:v name.

    Important: UI labels like ``libx H.264`` start with ``lib`` but are *not*
    valid encoder names — always go through ``map_codec_to_lib``.
    """
    ensure_sub_imports()
    from overlay_core import map_codec_to_lib  # noqa: E402

    return map_codec_to_lib(codec_setting)
