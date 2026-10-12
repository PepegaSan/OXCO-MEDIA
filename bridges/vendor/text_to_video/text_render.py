"""Text-Overlay-Raster: ein Renderer für Vorschau und Export.

Jedes Overlay wird mit Pillow zu einem RGBA-Sprite gerendert. Die Vorschau zeigt genau dieses
PNG über dem Video, der Export legt dasselbe PNG per FFmpeg *overlay* darüber — dadurch sind
Schrift, Größe und Position in beiden Fällen identisch.

Positionssemantik: ``pos_x``/``pos_y`` ist die linke obere Ecke der Inhaltsbox (Textblock plus
Hintergrund-Polsterung). Schatten/Kontur dürfen darüber hinausragen; ``origin_x``/``origin_y``
geben an, wo die Inhaltsbox im Sprite beginnt.
"""

from __future__ import annotations

import math
import os
import sys
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Any, List, Optional, Tuple


class TextRenderError(RuntimeError):
    pass


ANIMATIONS = ("none", "fade", "slide", "pop")
ALIGNMENTS = ("left", "center", "right")


@dataclass
class TimedTextOverlay:
    """Text mit eigenem Erscheinungszeitfenster und Darstellung."""

    text: str
    fontcolor_hex: str
    fontsize: int
    pos_x: int
    pos_y: int
    # 0 = natürliche Textgröße; positive Werte skalieren die Inhaltsbox frei.
    layout_width: int = 0
    layout_height: int = 0
    #: Zusätzliche Polsterung (px) um den Text, wenn der Hintergrund aktiv ist.
    box_border_w: int = 3
    box_enabled: bool = True
    line_spacing: int = 0
    text_visible_from_sec: Optional[float] = None
    text_visible_to_sec: Optional[float] = None
    # Leer/None = Standard-Schrift; sonst Pfad zu .ttf/.otf/.ttc
    font_path: Optional[str] = None
    # Optional: eigene Italic-Datei (echtes Kursiv statt Scherung).
    italic_font_path: Optional[str] = None
    bold: bool = False
    italic: bool = False
    strike: bool = False
    align: str = "left"
    outline_width: int = 0
    outline_hex: str = "000000"
    shadow: bool = False
    box_hex: str = "000000"
    box_opacity: int = 35
    box_radius: int = 10
    animation: str = "none"
    anim_duration: float = 0.4


@dataclass
class RenderedSprite:
    path: Path
    width: int
    height: int
    origin_x: int
    origin_y: int
    content_width: int
    content_height: int
    warning: str = ""


def overlay_visual_lines(content: str) -> List[str]:
    """Zeilen wie der Nutzer sie mit Enter trennt (auch leere Zeilen)."""
    return (content or "").replace("\r\n", "\n").replace("\r", "\n").split("\n")


def overlay_has_visible_text(overlay: TimedTextOverlay) -> bool:
    return any(line.strip() for line in overlay_visual_lines(overlay.text))


def line_step_px(fontsize: int, line_spacing: int) -> int:
    sz = max(8, min(int(fontsize), 400))
    ls = max(-120, min(int(line_spacing), 120))
    return max(4, sz + ls)


def hex_to_rgb(hex_color: str) -> Tuple[int, int, int]:
    h = (hex_color or "").strip().lstrip("#")
    if len(h) != 6:
        h = "FFFFFF"
    try:
        return int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16)
    except ValueError:
        return 255, 255, 255


# --------------------------------------------------------------------------- fonts

_SYSTEM_FONT: Optional[Path] = None


def _windows_font_dirs() -> List[Path]:
    dirs: List[Path] = []
    win = os.environ.get("WINDIR", r"C:\Windows")
    dirs.append(Path(win) / "Fonts")
    local = os.environ.get("LOCALAPPDATA", "").strip()
    if local:
        dirs.append(Path(local) / "Microsoft" / "Windows" / "Fonts")
    return dirs


def system_font_path() -> Path:
    """Standardschrift (Segoe UI / Arial / ENV ``VIDEO_TEXT_FONTFILE``)."""
    global _SYSTEM_FONT
    if _SYSTEM_FONT is not None and _SYSTEM_FONT.is_file():
        return _SYSTEM_FONT

    candidates: List[Path] = []
    env = (os.environ.get("VIDEO_TEXT_FONTFILE") or "").strip()
    if env:
        candidates.append(Path(env))
    if sys.platform == "win32":
        for d in _windows_font_dirs():
            candidates.extend([d / "segoeui.ttf", d / "arial.ttf"])
    else:
        candidates.extend(
            [
                Path("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"),
                Path("/usr/share/fonts/TTF/DejaVuSans.ttf"),
            ]
        )
    for c in candidates:
        if c.is_file():
            _SYSTEM_FONT = c.resolve()
            return _SYSTEM_FONT
    raise TextRenderError(
        "Keine Schriftdatei (.ttf) gefunden. Umgebungsvariable VIDEO_TEXT_FONTFILE setzen."
    )


# Windows-Familien: Regular-Dateiname → (Bold, Italic, BoldItalic)
_WIN_STYLE_FILES = {
    "arial": ("arialbd.ttf", "ariali.ttf", "arialbi.ttf"),
    "segoeui": ("segoeuib.ttf", "segoeuii.ttf", "segoeuiz.ttf"),
    "calibri": ("calibrib.ttf", "calibrii.ttf", "calibriz.ttf"),
    "verdana": ("verdanab.ttf", "verdanai.ttf", "verdanaz.ttf"),
    "tahoma": ("tahomabd.ttf", None, None),
    "times": ("timesbd.ttf", "timesi.ttf", "timesbi.ttf"),
    "georgia": ("georgiab.ttf", "georgiai.ttf", "georgiaz.ttf"),
    "consola": ("consolab.ttf", "consolai.ttf", "consolaz.ttf"),
    "trebuc": ("trebucbd.ttf", "trebucit.ttf", "trebucbi.ttf"),
    "comic": ("comicbd.ttf", "comici.ttf", "comicz.ttf"),
    "cour": ("courbd.ttf", "couri.ttf", "courbi.ttf"),
    "candara": ("candarab.ttf", "candarai.ttf", "candaraz.ttf"),
    "corbel": ("corbelb.ttf", "corbeli.ttf", "corbelz.ttf"),
    "constan": ("constanb.ttf", "constani.ttf", "constanz.ttf"),
    "pala": ("palab.ttf", "palai.ttf", "palabi.ttf"),
    "framd": (None, "framdit.ttf", None),
    "lucon": (None, None, None),
}

_STYLE_WORDS = {
    0: ("Bold",),
    1: ("Italic", "Oblique"),
    2: ("BoldItalic", "Bold Italic", "BoldOblique"),
}


def find_style_variant(regular: Path, bold: bool, italic: bool) -> Optional[Path]:
    """Sucht die echte Bold-/Italic-Datei zur gewählten Schrift."""
    if not (bold or italic) or not regular.is_file():
        return None
    key = 2 if (bold and italic) else (0 if bold else 1)
    parent = regular.resolve().parent
    stem = regular.stem

    mapped = _WIN_STYLE_FILES.get(stem.lower())
    if mapped and mapped[key]:
        for d in [parent, *_windows_font_dirs()]:
            p = d / mapped[key]
            if p.is_file():
                return p

    bases = [stem]
    for tok in ("-Regular", "_Regular", " Regular", "Regular", "-Roman", "-Book", "-Normal"):
        if stem.endswith(tok) and len(stem) > len(tok):
            bases.insert(0, stem[: -len(tok)])
            break
    exts = []
    for e in (regular.suffix, ".ttf", ".otf"):
        if e and e not in exts:
            exts.append(e)
    for base in bases:
        for word in _STYLE_WORDS[key]:
            for sep in ("-", "_", " ", ""):
                for ext in exts:
                    p = parent / f"{base}{sep}{word}{ext}"
                    if p.is_file() and p.resolve() != regular.resolve():
                        return p
    return None


def load_pil_font(font_path: str, size: int):
    """TTF/OTF/TTC für Pillow laden; bei Collections einige Indizes probieren."""
    from PIL import ImageFont

    try:
        return ImageFont.truetype(font_path, size)
    except OSError:
        pass
    for index in range(1, 8):
        try:
            return ImageFont.truetype(font_path, size, index=index)
        except OSError:
            continue
    return None


def _apply_variation(font: Any, want_bold: bool, want_italic: bool) -> Tuple[bool, bool]:
    """Variable Fonts (z. B. Bahnschrift): benannte Instanz statt künstlichem Stil.

    Gibt zurück, ob Bold/Italic danach noch künstlich erzeugt werden müssen.
    """
    try:
        raw = font.get_variation_names()
    except Exception:
        return want_bold, want_italic
    names = [n.decode("utf-8", "replace") if isinstance(n, bytes) else str(n) for n in raw]
    if not names:
        return want_bold, want_italic

    targets: List[str] = []
    if want_bold and want_italic:
        targets += ["Bold Italic", "BoldItalic"]
    if want_bold:
        targets += ["Bold", "SemiBold"]
    elif want_italic:
        targets += ["Italic"]
    for t in targets:
        for n in names:
            if n.replace(" ", "").lower() == t.replace(" ", "").lower():
                try:
                    font.set_variation_by_name(n)
                except Exception:
                    continue
                low = n.lower()
                return want_bold and "bold" not in low, want_italic and "italic" not in low
    return want_bold, want_italic


def _load_face(ov: TimedTextOverlay, size: int):
    """Liefert (font, faux_bold, faux_italic, warning)."""
    warning = ""
    base: Optional[Path] = None
    if ov.font_path:
        p = Path(ov.font_path)
        if p.is_file():
            base = p
        else:
            warning = f"Schrift nicht gefunden: {p.name} – Standardschrift verwendet"
    if base is None:
        base = system_font_path()

    want_bold, want_italic = bool(ov.bold), bool(ov.italic)
    candidates: List[Tuple[Path, bool, bool]] = []
    if want_italic and ov.italic_font_path and Path(ov.italic_font_path).is_file():
        candidates.append((Path(ov.italic_font_path), want_bold, False))
    if want_bold or want_italic:
        v = find_style_variant(base, want_bold, want_italic)
        if v is not None:
            candidates.append((v, False, False))
        if want_bold and want_italic:
            vb = find_style_variant(base, True, False)
            if vb is not None:
                candidates.append((vb, False, True))
            vi = find_style_variant(base, False, True)
            if vi is not None:
                candidates.append((vi, True, False))
    candidates.append((base, want_bold, want_italic))

    for path, fb, fi in candidates:
        font = load_pil_font(str(path), size)
        if font is not None:
            fb, fi = _apply_variation(font, fb, fi) if (fb or fi) else (fb, fi)
            return font, fb, fi, warning

    fallback = system_font_path()
    font = load_pil_font(str(fallback), size)
    if font is None:
        raise TextRenderError(f"Schriftdatei konnte nicht geladen werden: {base.name}")
    warning = f"Schrift konnte nicht geladen werden: {base.name} – Standardschrift verwendet"
    return font, want_bold, want_italic, warning


# --------------------------------------------------------------------------- rendering


def _shear(im: Any, slant: float, pivot_y: int) -> Any:
    """Horizontale Scherung (oben nach rechts); Zeile *pivot_y* bleibt an ihrer x-Position."""
    from PIL import Image

    w, h = im.size
    extra = int(math.ceil(slant * max(0, pivot_y)))
    try:
        aff, res = Image.Transform.AFFINE, Image.Resampling.BICUBIC
    except AttributeError:  # Pillow < 9.1
        aff, res = Image.AFFINE, Image.BICUBIC  # type: ignore[attr-defined]
    # Ausgabe (x, y) liest Eingabe (x - slant*(pivot_y - y), y)
    data = (1.0, slant, -slant * pivot_y, 0.0, 1.0, 0.0)
    return im.transform((w + extra, h), aff, data, resample=res, fillcolor=(0, 0, 0, 0))


def render_overlay_sprite(
    work_dir: Path,
    ov: TimedTextOverlay,
    *,
    max_width: int = 0,
    max_height: int = 0,
) -> RenderedSprite:
    """Rendert *ov* als RGBA-PNG in *work_dir*."""
    try:
        from PIL import Image, ImageDraw, ImageFilter
    except ImportError as exc:
        raise TextRenderError("Pillow wird benötigt (pip install Pillow).") from exc

    sz = max(8, min(int(ov.fontsize), 400))
    hi = 3 if sz < 48 else 2  # Supersampling für saubere Kanten
    s = sz * hi
    font, faux_bold, faux_italic, warning = _load_face(ov, s)

    lines = overlay_visual_lines(ov.text) or [""]
    step = line_step_px(ov.fontsize, ov.line_spacing) * hi

    # Stabile Zeilenmetrik (unabhängig vom eingegebenen Text → Box springt beim Tippen nicht)
    try:
        asc_box = font.getbbox("Hdlk", anchor="ls")
        desc_box = font.getbbox("gjpqy", anchor="ls")
        asc_h = max(1, -int(asc_box[1]))
        desc_h = max(0, int(desc_box[3]))
    except Exception:
        a, d = font.getmetrics()
        asc_h, desc_h = int(a * 0.8), int(d)

    bold_w = max(1, int(round(s / 26))) if faux_bold else 0
    outline_w = max(0, min(int(ov.outline_width), 40)) * hi
    stroke_total = bold_w + outline_w

    widths = [font.getlength(line) if line.strip() else 0.0 for line in lines]
    block_w = int(math.ceil(max(widths))) if widths else 0
    block_w = max(block_w, s // 3)
    block_h = asc_h + (len(lines) - 1) * step + desc_h
    slant = 0.2 if faux_italic else 0.0
    if slant:
        block_w += int(math.ceil(slant * (block_h - desc_h)))

    align = ov.align if ov.align in ALIGNMENTS else "left"
    margin = stroke_total + s  # großzügig: Akzente, Überhänge, Kursiv

    text_rgb = hex_to_rgb(ov.fontcolor_hex)
    outline_rgb = hex_to_rgb(ov.outline_hex)
    lw, lh = block_w + 2 * margin, block_h + 2 * margin
    text_layer = Image.new("RGBA", (lw, lh), (0, 0, 0, 0))
    dr = ImageDraw.Draw(text_layer)

    strike_y_rel = 0
    if ov.strike:
        try:
            xb = font.getbbox("x", anchor="ls")
            strike_y_rel = int((xb[1] + xb[3]) / 2)
        except Exception:
            strike_y_rel = -int(asc_h * 0.35)
    strike_w = max(hi, int(round(s / 15)))

    for i, line in enumerate(lines):
        if not line.strip():
            continue
        lw_i = widths[i]
        if align == "center":
            x = margin + (block_w - lw_i) / 2
        elif align == "right":
            x = margin + block_w - lw_i
        else:
            x = margin
        y = margin + asc_h + i * step
        if outline_w:
            o_fill = (*outline_rgb, 255)
            dr.text((x, y), line, font=font, anchor="ls", fill=o_fill,
                    stroke_width=stroke_total, stroke_fill=o_fill)
            if ov.strike:
                dr.line((x - hi, y + strike_y_rel, x + lw_i + hi, y + strike_y_rel),
                        fill=o_fill, width=strike_w + 2 * outline_w)
        fill = (*text_rgb, 255)
        if bold_w:
            dr.text((x, y), line, font=font, anchor="ls", fill=fill,
                    stroke_width=bold_w, stroke_fill=fill)
        else:
            dr.text((x, y), line, font=font, anchor="ls", fill=fill)
        if ov.strike:
            dr.line((x - hi, y + strike_y_rel, x + lw_i + hi, y + strike_y_rel),
                    fill=fill, width=strike_w)

    if slant:
        text_layer = _shear(text_layer, slant, margin + block_h - desc_h)

    # Inhaltsbox (Textblock + Polsterung) in Text-Layer-Koordinaten
    if ov.box_enabled:
        bb = max(0, min(int(ov.box_border_w), 80)) * hi
        pad_x = bb + int(round(s * 0.32))
        pad_y = bb + int(round(s * 0.16))
    else:
        pad_x = pad_y = 0
    cx0, cy0 = margin - pad_x, margin - pad_y
    cx1, cy1 = margin + block_w + pad_x, margin + block_h + pad_y

    shadow_off = max(hi, int(round(s * 0.05))) if ov.shadow else 0
    shadow_blur = max(hi, int(round(s * 0.09))) if ov.shadow else 0
    ext = max(pad_x, pad_y) + shadow_off + shadow_blur * 3 + hi
    cw, ch = text_layer.size[0] + 2 * ext, text_layer.size[1] + 2 * ext
    canvas = Image.new("RGBA", (cw, ch), (0, 0, 0, 0))
    cx0, cy0, cx1, cy1 = cx0 + ext, cy0 + ext, cx1 + ext, cy1 + ext

    if ov.box_enabled:
        box_rgb = hex_to_rgb(ov.box_hex)
        alpha = int(round(255 * max(0, min(int(ov.box_opacity), 100)) / 100))
        radius = min(max(0, int(ov.box_radius)) * hi, (cy1 - cy0) // 2, (cx1 - cx0) // 2)
        bd = ImageDraw.Draw(canvas)
        try:
            bd.rounded_rectangle((cx0, cy0, cx1 - 1, cy1 - 1), radius=radius, fill=(*box_rgb, alpha))
        except AttributeError:
            bd.rectangle((cx0, cy0, cx1 - 1, cy1 - 1), fill=(*box_rgb, alpha))
    canvas.alpha_composite(text_layer, (ext, ext))

    if ov.shadow:
        a = canvas.split()[3]
        a = a.point(lambda v: int(v * 0.6))
        sh = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
        sh.putalpha(a)
        sh = sh.filter(ImageFilter.GaussianBlur(shadow_blur))
        shadowed = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
        shadowed.alpha_composite(sh, (shadow_off, shadow_off))
        shadowed.alpha_composite(canvas)
        canvas = shadowed

    # Zuschneiden: sichtbare Pixel ∪ Inhaltsbox, auf Vielfache von *hi* ausgerichtet
    bbox = canvas.split()[3].getbbox() or (cx0, cy0, cx1, cy1)
    x0 = min(bbox[0], cx0) // hi * hi
    y0 = min(bbox[1], cy0) // hi * hi
    x1 = -(-max(bbox[2], cx1) // hi) * hi
    y1 = -(-max(bbox[3], cy1) // hi) * hi
    canvas = canvas.crop((x0, y0, x1, y1))

    try:
        lanczos = Image.Resampling.LANCZOS
    except AttributeError:
        lanczos = Image.LANCZOS  # type: ignore[attr-defined]
    out_w, out_h = max(1, (x1 - x0) // hi), max(1, (y1 - y0) // hi)
    canvas = canvas.resize((out_w, out_h), resample=lanczos)
    origin_x = int(round((cx0 - x0) / hi))
    origin_y = int(round((cy0 - y0) / hi))
    content_w = max(1, int(round((cx1 - cx0) / hi)))
    content_h = max(1, int(round((cy1 - cy0) / hi)))

    if ov.layout_width > 0 and ov.layout_height > 0:
        tw, th = int(ov.layout_width), int(ov.layout_height)
        if max_width > 0:
            tw = min(tw, max_width)
        if max_height > 0:
            th = min(th, max_height)
        tw, th = max(8, tw), max(8, th)
        sx, sy = tw / content_w, th / content_h
        canvas = canvas.resize(
            (max(1, int(round(out_w * sx))), max(1, int(round(out_h * sy)))),
            resample=lanczos,
        )
        origin_x = int(round(origin_x * sx))
        origin_y = int(round(origin_y * sy))
        content_w, content_h = tw, th

    png = work_dir / f"_vtsp_{uuid.uuid4().hex[:12]}.png"
    canvas.save(png, format="PNG")
    return RenderedSprite(
        path=png,
        width=canvas.size[0],
        height=canvas.size[1],
        origin_x=origin_x,
        origin_y=origin_y,
        content_width=content_w,
        content_height=content_h,
        warning=warning,
    )
