"""Safe console output for Hail Mary bridge scripts on Windows."""
from __future__ import annotations

import sys

_configured = False


def configure_stdio() -> None:
    global _configured
    if _configured:
        return
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is not None:
            try:
                reconfigure(encoding="utf-8", errors="replace")
            except Exception:
                pass
    _configured = True


def sanitize(text: str) -> str:
    return (
        text.replace("\u2192", "->")
        .replace("\u2026", "...")
        .replace("\u00d7", "x")
        .replace("\u2713", "[x]")
    )


def emit(line: str) -> None:
    configure_stdio()
    text = sanitize(line)
    try:
        print(text, flush=True)
    except UnicodeEncodeError:
        safe = text.encode("ascii", errors="replace").decode("ascii")
        print(safe, flush=True)


def emit_progress(percent: float, label: str) -> None:
    emit(f"HM_PROGRESS:{percent:.1f}:{label}")


def emit_progress_end() -> None:
    emit("HM_PROGRESS_END")


def emit_progress_fraction(current: int, total: int, label: str) -> None:
    if total <= 0:
        emit_progress(100.0, label)
        return
    pct = current / total * 100.0
    emit_progress(pct, f"{label} {current}/{total}")


def emit_batch_item(path: str, status: str) -> None:
    """Structured batch row status for C# UI (path may contain colons on Windows)."""
    emit(f"HM_BATCH_ITEM|{path}|{status}")
