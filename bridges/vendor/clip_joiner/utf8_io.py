"""UTF-8 / Windows-Pfad-Hilfen (wie Oxco compare.py)."""

from __future__ import annotations

import os
import sys
import unicodedata
from pathlib import Path


def ensure_utf8_stdio() -> None:
    """Windows-Konsole + Worker-Stdout auf UTF-8 (Oxco: _ensure_stdio_utf8)."""
    try:
        if hasattr(sys.stdout, "reconfigure"):
            sys.stdout.reconfigure(encoding="utf-8", errors="replace", line_buffering=True)
        if hasattr(sys.stderr, "reconfigure"):
            sys.stderr.reconfigure(encoding="utf-8", errors="replace", line_buffering=True)
    except Exception:
        pass


def enable_python_utf8_mode() -> None:
    os.environ.setdefault("PYTHONUTF8", "1")
    os.environ.setdefault("PYTHONIOENCODING", "utf-8")


def decode_tkdnd_path(raw: str) -> str:
    """Tk DnD liefert auf Windows UTF-8 oft als latin-1/cp1252-String."""
    s = (raw or "").strip().strip('"')
    if not s:
        return s

    candidates: list[str] = [s]
    for enc in ("latin-1", "cp1252"):
        try:
            candidates.append(s.encode(enc).decode("utf-8"))
        except (UnicodeError, UnicodeDecodeError):
            continue

    for c in candidates:
        n = unicodedata.normalize("NFC", c)
        if os.path.exists(n):
            return n

    return unicodedata.normalize("NFC", s)


def canonical_media_path(path: str) -> str:
    """Existierenden Medienpfad kanonisch auflösen (Unicode NFC + resolve)."""
    p = decode_tkdnd_path(path) if path else path
    p = unicodedata.normalize("NFC", str(p).strip().strip('"'))
    if not p:
        raise ValueError("Leerer Pfad")
    resolved = Path(p)
    if not resolved.is_file():
        raise FileNotFoundError(p)
    return str(resolved.resolve())


def to_resolve_import_path(path: str) -> str:
    """Resolve ImportMedia: abspath + Forward-Slashes (Oxco compare.py)."""
    return os.path.abspath(canonical_media_path(path)).replace("\\", "/")


def ascii_temp_dir(prefix: str = "cj_") -> str:
    """Temp nur unter LOCALAPPDATA/ClipJoin — kurze ASCII-Pfade fuer FFmpeg/Resolve."""
    import tempfile

    base = os.environ.get("LOCALAPPDATA") or tempfile.gettempdir()
    root = os.path.join(base, "ClipJoin")
    os.makedirs(root, exist_ok=True)
    return tempfile.mkdtemp(prefix=prefix, dir=root)


def format_wh(w: int, h: int) -> str:
    return f"{w}x{h}"
