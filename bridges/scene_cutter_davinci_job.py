#!/usr/bin/env python3
"""Hail Mary Bridge: Szenen nach DaVinci Resolve exportieren (vendored davinci_api)."""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

BRIDGE_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(BRIDGE_DIR))
sys.path.insert(0, str(BRIDGE_DIR / "vendor" / "scene_cutter"))

from bridge_io import emit  # noqa: E402
from davinci_export import export_scenes_to_resolve  # noqa: E402


def _load_scenes(args: argparse.Namespace) -> list[tuple[float, float]]:
    if args.scenes_file:
        raw = json.loads(Path(args.scenes_file).read_text(encoding="utf-8-sig"))
    elif args.scenes_json:
        raw = json.loads(args.scenes_json)
    else:
        raise ValueError("Weder --scenes-file noch --scenes-json angegeben")

    scenes: list[tuple[float, float]] = []
    for pair in raw:
        if not isinstance(pair, (list, tuple)) or len(pair) < 2:
            raise ValueError("Ungueltiges Szenen-Paar")
        st, en = float(pair[0]), float(pair[1])
        if en <= st:
            raise ValueError(f"Ende muss nach Start liegen: {st} -> {en}")
        scenes.append((st, en))
    return scenes


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True)
    parser.add_argument("--preset", default="YouTube - 1080p")
    parser.add_argument("--output-dir", default=None)
    parser.add_argument("--player-fps", type=float, default=25.0)
    parser.add_argument("--resolve-exe", default=None)
    parser.add_argument("--resolve-modules", default=None)
    parser.add_argument("--resolve-dll", default=None)
    parser.add_argument("--scenes-json", default=None)
    parser.add_argument("--scenes-file", default=None)
    args = parser.parse_args()

    try:
        scenes = _load_scenes(args)
    except (OSError, json.JSONDecodeError, TypeError, ValueError) as exc:
        emit(f"ERROR: Szenen ungueltig: {exc}")
        return 1

    src = Path(args.input).expanduser().resolve()
    out_dir = (
        Path(args.output_dir).expanduser().resolve()
        if args.output_dir
        else Path.home() / "Videos"
    )

    out = None
    try:
        od, name = export_scenes_to_resolve(
            src,
            scenes,
            preset=args.preset,
            output_dir=out_dir,
            player_fps=args.player_fps,
            resolve_exe=args.resolve_exe,
            resolve_modules=args.resolve_modules,
            resolve_dll=args.resolve_dll,
            log=emit,
        )
        out = od / name
    except Exception as exc:
        emit(f"ERROR: {exc}")
        return 1
    finally:
        if args.scenes_file:
            try:
                Path(args.scenes_file).unlink(missing_ok=True)
            except OSError:
                pass

    emit(f"OUTPUT:{out}.mp4")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
