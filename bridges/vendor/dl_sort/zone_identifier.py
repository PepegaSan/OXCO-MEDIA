# -*- coding: utf-8 -*-
"""
Windows Zone.Identifier (Alternate Data Stream) auslesen.

Viele Browser schreiben beim Download Metadaten in den ADS „Zone.Identifier“.
Dort können HostUrl und ReferrerUrl stehen – nützlich als Filterkriterium.
"""

from __future__ import annotations

import configparser
import io
import os
import sys
from pathlib import Path
from typing import Optional


ZONE_STREAM_NAME = "Zone.Identifier"


def _is_windows() -> bool:
    return sys.platform == "win32"


def read_zone_identifier_text(file_path: str | os.PathLike[str]) -> Optional[str]:
    """
    Liest den Rohinhalt von :Zone.Identifier, falls vorhanden.

    Unter Windows kann in vielen Fällen direkt ``open("pfad:Zone.Identifier")``
    verwendet werden. Gibt None zurück, wenn kein Stream existiert oder ein
    Fehler auftritt (nicht-Windows, Berechtigung, kein NTFS, …).
    """
    if not _is_windows():
        return None

    path = Path(file_path)
    ads_path = f"{path}:{ZONE_STREAM_NAME}"

    try:
        # UTF-8 mit Fallback: ältere Einträge können ANSI sein
        with open(ads_path, "r", encoding="utf-8", errors="replace") as fh:
            return fh.read()
    except OSError:
        try:
            with open(ads_path, "r", encoding="cp1252", errors="replace") as fh:
                return fh.read()
        except OSError:
            return None


def _parse_zone_urls_line_based(text: str) -> tuple[Optional[str], Optional[str]]:
    """Fallback: schlichte Zeilen wie ``HostUrl=https://...`` erkennen."""
    host: Optional[str] = None
    ref: Optional[str] = None
    for line in text.splitlines():
        line = line.strip()
        if "=" not in line or line.startswith(";"):
            continue
        key, _, val = line.partition("=")
        k = key.strip().lower()
        v = val.strip()
        if k == "hosturl":
            host = v or None
        elif k == "referrerurl":
            ref = v or None
    return host, ref


def parse_zone_urls(zone_text: Optional[str]) -> tuple[Optional[str], Optional[str]]:
    """
    Extrahiert HostUrl und ReferrerUrl aus dem Zone-Identifier-Inhalt.

    Die Datei ist INI-ähnlich; configparser erwartet eine Sektion – wir
    fügen synthetisch [ZoneTransfer] hinzu, falls nötig.
    """
    if not zone_text or not zone_text.strip():
        return None, None

    raw = zone_text.strip()
    if not raw.lower().startswith("[zonetransfer]"):
        raw = "[ZoneTransfer]\n" + raw

    parser = configparser.ConfigParser(interpolation=None)
    try:
        parser.read_file(io.StringIO(raw))
    except configparser.Error:
        return _parse_zone_urls_line_based(zone_text)

    if not parser.has_section("ZoneTransfer"):
        return _parse_zone_urls_line_based(zone_text)

    host = parser.get("ZoneTransfer", "HostUrl", fallback=None)
    ref = parser.get("ZoneTransfer", "ReferrerUrl", fallback=None)
    host = host.strip() if host else None
    ref = ref.strip() if ref else None
    if host or ref:
        return host or None, ref or None
    return _parse_zone_urls_line_based(zone_text)


def get_source_urls_for_file(file_path: str | os.PathLike[str]) -> tuple[Optional[str], Optional[str]]:
    """
    Bequeme Hilfsfunktion: Rohinhalt lesen und URLs zurückgeben (Host, Referrer).
    """
    text = read_zone_identifier_text(file_path)
    return parse_zone_urls(text)


def combined_url_search_text(file_path: str | os.PathLike[str]) -> str:
    """
    Ein String, gegen den „enthält“/„ist gleich“ für Quell-URL geprüft wird.
    Host und Referrer werden zusammengefügt (klein geschrieben für Vergleich).
    """
    host, ref = get_source_urls_for_file(file_path)
    parts = [p for p in (host, ref) if p]
    return " ".join(parts).lower()
