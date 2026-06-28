#!/usr/bin/env python3
"""Entfernt _introcut / _resolve_cut aus Dateinamen im Ausgabeordner."""

from __future__ import annotations

import argparse
import json
import shutil
import sys
from pathlib import Path
from typing import List, Tuple

BRIDGE_ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(BRIDGE_ROOT))

from bridge_io import emit  # noqa: E402

VIDEO_EXTENSIONS = {
    ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".ts", ".flv", ".mpg", ".mpeg",
}

STRIP_SUFFIXES = ("_introcut", "_resolve_cut")


def _paths_conflict(a: Path, b: Path) -> bool:
    try:
        return a.resolve() == b.resolve()
    except Exception:
        return str(a).lower() == str(b).lower()


def collect_rename_pairs(root: Path, recursive: bool) -> Tuple[List[Tuple[str, str]], int, int]:
    pattern = "**/*" if recursive else "*"
    pairs: List[Tuple[str, str]] = []
    conflicts = 0
    scanned = 0
    for path in root.glob(pattern):
        if not path.is_file():
            continue
        if path.suffix.lower() not in VIDEO_EXTENSIONS:
            continue
        scanned += 1
        stem = path.stem
        new_stem = None
        for suffix in STRIP_SUFFIXES:
            if stem.endswith(suffix):
                new_stem = stem[: -len(suffix)]
                break
        if not new_stem:
            continue
        target = path.with_name(f"{new_stem}{path.suffix}")
        if _paths_conflict(path, target) or target.exists():
            conflicts += 1
            continue
        pairs.append((str(path), str(target)))
    pairs.sort(key=lambda item: item[0].lower())
    return pairs, conflicts, scanned


def apply_renames(pairs: List[Tuple[str, str]]) -> Tuple[int, int]:
    ok = err = 0
    for old, new in pairs:
        try:
            Path(old).rename(new)
            ok += 1
        except OSError:
            try:
                shutil.move(old, new)
                ok += 1
            except OSError as exc:
                emit(f"FEHLER: {Path(old).name} -> {Path(new).name}: {exc}")
                err += 1
    return ok, err


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--config-json", required=True)
    parser.add_argument("--preview-only", action="store_true")
    args = parser.parse_args()

    config = json.loads(Path(args.config_json).read_text(encoding="utf-8"))
    root = Path(config.get("folder", "")).expanduser()
    if not root.is_dir():
        emit("FEHLER: Ordner ungueltig oder nicht gefunden")
        return 1

    recursive = bool(config.get("recursive", True))
    pairs, conflicts, scanned = collect_rename_pairs(root, recursive)

    if args.preview_only:
        emit(f"Vorschau: {scanned} Video(s) gescannt, {len(pairs)} umbenennbar, {conflicts} uebersprungen")
        for old, new in pairs[:25]:
            emit(f"  {Path(old).name} -> {Path(new).name}")
        if len(pairs) > 25:
            emit(f"  ... und {len(pairs) - 25} weitere")
        return 0

    if not pairs:
        emit("Keine Dateien mit _introcut oder _resolve_cut gefunden")
        return 0

    ok, err = apply_renames(pairs)
    emit(f"Umbenennen fertig: {ok} ok, {err} Fehler, {conflicts} Konflikte")
    return 0 if err == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
