# -*- coding: utf-8 -*-
"""Projekt- bzw. Installationsverzeichnis (wichtig für PyInstaller-EXE)."""

from __future__ import annotations

import os
import sys
from pathlib import Path
from typing import Optional


def is_nuitka_compiled() -> bool:
    """Nuitka setzt kein sys.frozen; stattdessen __compiled__ im __main__-Modul."""
    m = sys.modules.get("__main__")
    if m is None:
        return False
    return "__compiled__" in getattr(m, "__dict__", ())


def nuitka_user_invoked_executable() -> Optional[Path]:
    """
    Bei Nuitka --onefile: Pfad zur .exe, die der Nutzer wirklich gestartet hat
    (nicht die entpackte Kopie unter %TEMP%\\onefile_…).

    Sonst None. Wichtig für Autostart, config.json und AV-Heuristiken
    („Programm aus Temp will in Autostart“).
    """
    if not is_nuitka_compiled():
        return None
    raw = os.environ.get("NUITKA_ONEFILE_ARGV0") or os.environ.get("NUITKA_ORIGINAL_ARGV0")
    if raw:
        p = Path(raw.strip('"'))
        if p.is_file():
            return p.absolute()
    m = sys.modules.get("__main__")
    if m is not None:
        c = getattr(m, "__compiled__", None)
        if c is not None:
            for attr in ("original_argv0", "onefile_argv0"):
                v = getattr(c, attr, None)
                if v:
                    p = Path(str(v).strip('"'))
                    if p.is_file():
                        return p.absolute()
    return None


def application_base_dir() -> Path:
    """
    Ordner für config.json, Logdatei und Export-Dialog-Start.

    - Normal: Verzeichnis von main.py / diesem Paket
    - PyInstaller --onefile/--onedir: Ordner der .exe (schreibbar)
    - Nuitka: Ordner der vom Nutzer gestarteten .exe (Onefile: Bootstrap-Pfad), sonst argv[0]
    """
    if getattr(sys, "frozen", False):
        return Path(sys.executable).absolute().parent
    if is_nuitka_compiled():
        outer = nuitka_user_invoked_executable()
        if outer is not None:
            return outer.parent
        return Path(sys.argv[0]).absolute().parent
    return Path(__file__).resolve().parent
