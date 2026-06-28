# -*- coding: utf-8 -*-
"""Regeln auswerten und Dateiaktionen ausführen."""

from __future__ import annotations

import os
import shutil
import time
from pathlib import Path

from config_io import Action, AppConfig, Condition, Rule, RuleCriterion
from zone_identifier import get_source_urls_for_file


# Bekannte temporäre Download-Endungen (werden still ignoriert).
_TEMP_SUFFIXES = frozenset({".crdownload", ".tmp", ".part"})


def is_temporary_download_path(path: str | os.PathLike[str]) -> bool:
    """True, wenn die Datei offenbar noch lädt oder temporär ist."""
    suffix = Path(path).suffix.lower()
    if suffix in _TEMP_SUFFIXES:
        return True
    name = Path(path).name.lower()
    # Teilweise nutzen Clients zusätzliche Muster
    if name.endswith(".crdownload") or name.endswith(".part"):
        return True
    return False


def _normalize_extension_value(value: str) -> tuple[str, str]:
    """Liefert zwei Varianten: wie eingegeben und mit führendem Punkt (falls sinnvoll)."""
    v = value.strip().lower()
    if not v:
        return "", ""
    with_dot = v if v.startswith(".") else f".{v}"
    return v, with_dot


def _match_extension_one(path: Path, condition: Condition, value: str) -> bool:
    needle_raw, needle_dot = _normalize_extension_value(value)
    if not needle_raw:
        return False
    ext = path.suffix.lower()
    if condition == "equals":
        return ext == needle_dot or ext == needle_raw
    # contains
    return needle_raw in ext or needle_dot in ext


def _match_filename_one(path: Path, condition: Condition, value: str) -> bool:
    needle = value.strip().lower()
    if not needle:
        return False
    name = path.name.lower()
    if condition == "equals":
        return name == needle
    return needle in name


def _match_source_url_one(path: Path, condition: Condition, value: str) -> bool:
    needle = value.strip().lower()
    if not needle:
        return False
    host, ref = get_source_urls_for_file(path)
    host_l = (host or "").lower()
    ref_l = (ref or "").lower()
    combined = " ".join(p for p in (host_l, ref_l) if p).strip()

    if condition == "equals":
        return needle in {host_l, ref_l} or combined == needle
    # contains: in beliebiger URL oder der Kombination
    return needle in combined or needle in host_l or needle in ref_l


def criterion_matches(criterion: RuleCriterion, path: Path) -> bool:
    """
    Prüft ein Kriterium: mindestens einer der Werte muss passen (ODER),
    Typ und Bedingung gelten für alle Werte gleich.
    """
    parts = [v.strip() for v in criterion.values if v.strip()]
    if not parts:
        return False
    if criterion.if_type == "extension":
        return any(_match_extension_one(path, criterion.condition, p) for p in parts)
    if criterion.if_type == "filename":
        return any(_match_filename_one(path, criterion.condition, p) for p in parts)
    if criterion.if_type == "source_url":
        return any(_match_source_url_one(path, criterion.condition, p) for p in parts)
    return False


def rule_matches(rule: Rule, path: str | os.PathLike[str]) -> bool:
    """
    Prüft, ob die Regel auf die Datei passt.

    Alle Kriterien müssen zutreffen (UND). Leere Kriterienliste gilt als nicht passend.
    """
    p = Path(path)
    if not rule.criteria:
        return False
    for c in rule.criteria:
        if not criterion_matches(c, p):
            return False
    return True


def make_unique_destination(dest_dir: Path, filename: str) -> Path:
    """
    Ein freier Zielpfad: bei Kollision erst _1, _2, …, danach Unix-Zeitstempel.
    """
    dest_dir.mkdir(parents=True, exist_ok=True)
    candidate = dest_dir / filename
    if not candidate.exists():
        return candidate

    stem = Path(filename).stem
    suffix = Path(filename).suffix
    for i in range(1, 10_000):
        alt = dest_dir / f"{stem}_{i}{suffix}"
        if not alt.exists():
            return alt
    ts = int(time.time())
    return dest_dir / f"{stem}_{ts}{suffix}"


def apply_first_matching_rule(cfg: AppConfig, file_path: str | os.PathLike[str]) -> Action | None:
    """
    Wendet die erste passende Regel an. Rückgabe: ausgeführte Aktion oder None.

    - move: Datei verschieben (mit Konfliktauflösung)
    - delete: Datei löschen
    - ignore: bewusst nichts tun (Regel „erfüllt“, Datei bleibt liegen)
    """
    path = Path(file_path).resolve()

    if not path.is_file():
        return None

    for rule in cfg.rules:
        if not rule_matches(rule, path):
            continue

        action: Action = rule.action
        if action == "ignore":
            return "ignore"

        if action == "delete":
            try:
                path.unlink()
            except OSError:
                pass
            return "delete"

        if action == "move":
            target_root = Path(rule.target_folder).expanduser()
            if not str(target_root):
                # Regel passt, aber kein Ziel gesetzt — nicht hier abbrechen, nächste Regel probieren.
                continue
            dest = make_unique_destination(target_root, path.name)
            try:
                shutil.move(str(path), str(dest))
            except OSError:
                # Verschieben fehlgeschlagen (z. B. noch gesperrt) – keine Endlosschleife hier
                return None
            return "move"

    return None


def wait_until_file_stable(
    file_path: str | os.PathLike[str],
    *,
    settle_delay: float,
    poll_interval: float,
    max_wait: float,
    stop_event,
) -> bool:
    """
    Wartet, bis die Dateigröße kurz hintereinander unverändert bleibt.

    Vorab wird ``settle_delay`` gewartet (Download/AV kann noch schreiben).
    ``stop_event`` ist optional (threading.Event); wenn gesetzt, Abbruch mit False.
    """
    path = str(file_path)
    deadline = time.monotonic() + max_wait

    time.sleep(max(0.0, settle_delay))

    while time.monotonic() < deadline:
        if stop_event is not None and stop_event.is_set():
            return False
        try:
            size_a = os.path.getsize(path)
        except OSError:
            return False

        time.sleep(max(0.05, poll_interval))

        if stop_event is not None and stop_event.is_set():
            return False
        try:
            size_b = os.path.getsize(path)
        except OSError:
            return False

        # Zwei gleiche Messungen hintereinander: sehr wahrscheinlich Download fertig.
        if size_a == size_b:
            return True

    return False
