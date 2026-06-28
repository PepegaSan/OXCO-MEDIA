#!/usr/bin/env python3
"""Rename _bitrate suffix files in a folder."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

BRIDGE_ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(BRIDGE_ROOT))
sys.path.insert(0, str(BRIDGE_ROOT / "vendor" / "bitrate"))

from bridge_io import emit  # noqa: E402
from mass_bitrate_core import apply_renames, collect_rename_pairs_all, iter_rename_roots  # noqa: E402


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--config-json", required=True)
    parser.add_argument("--preview-only", action="store_true")
    args = parser.parse_args()

    config = json.loads(Path(args.config_json).read_text(encoding="utf-8"))
    roots = iter_rename_roots(config)
    if not roots:
        emit("FEHLER: input_folder/output_folder ungueltig")
        return 1

    pairs, conflicts, scanned = collect_rename_pairs_all(config)

    if args.preview_only:
        emit(f"Vorschau: {scanned} gescannt, {len(pairs)} umbenennbar, {conflicts} uebersprungen")
        for old, new in pairs[:25]:
            emit(f"  {Path(old).name} -> {Path(new).name}")
        if len(pairs) > 25:
            emit(f"  ... und {len(pairs) - 25} weitere")
        return 0

    if not pairs:
        emit("Keine Dateien zum Umbenennen")
        return 0

    ok, err = apply_renames(pairs)
    emit(f"Umbenennen fertig: {ok} ok, {err} fehler, {conflicts} Konflikte")
    return 0 if err == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
