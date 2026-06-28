#!/usr/bin/env python3
"""Hail Mary Bridge: Intro/Outro schneiden — nutzt vendored FFmpeg-Modul + optional Original-Resolve-CLI."""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from pathlib import Path

BRIDGE_DIR = Path(__file__).resolve().parent
VENDOR_DIR = BRIDGE_DIR / "vendor" / "intro_cutter"
sys.path.insert(0, str(VENDOR_DIR))

from intro_cut_ffmpeg import run_ffmpeg_cut  # noqa: E402


def _emit(line: str) -> None:
    print(line, flush=True)


def _projects_root() -> Path:
    env = os.environ.get("HAIL_MARY_PROJECTS_ROOT", "").strip().strip('"')
    if env:
        return Path(env).resolve()
    return BRIDGE_DIR.parent.parent.resolve()


def _load_intro_cutter_settings() -> dict:
    base = os.environ.get("LOCALAPPDATA") or str(Path.home())
    path = Path(base) / "IntroCutter" / "settings.json"
    if not path.is_file():
        return {}
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {}


def _resolve_env_from_settings(settings: dict) -> dict[str, str]:
    env = os.environ.copy()
    for key, setting_key in (
        ("INTRO_CUTTER_RESOLVE_EXE", "resolve_exe"),
        ("INTRO_CUTTER_FUSIONSCRIPT_DLL", "fusionscript_dll"),
        ("INTRO_CUTTER_SCRIPTING_MODULES", "scripting_modules"),
    ):
        val = (settings.get(setting_key) or "").strip()
        if val:
            env[key] = val
    return env


def run_ffmpeg_mode(args: argparse.Namespace) -> int:
    src = Path(args.input).expanduser().resolve()
    if not src.is_file():
        _emit(f"ERROR: Datei nicht gefunden: {src}")
        return 1

    def log(msg: str) -> None:
        _emit(msg)

    try:
        out = run_ffmpeg_cut(
            src,
            args.intro,
            args.outro,
            args.vcodec,
            args.vbitrate,
            args.acodec,
            args.abitrate,
            video_bitrate_auto=args.vbitrate_auto,
            output_dir=args.output_dir,
            log=log,
        )
    except Exception as exc:
        _emit(f"ERROR: {exc}")
        return 1

    _emit(f"OUTPUT:{out}")
    return 0


def run_resolve_mode(args: argparse.Namespace) -> int:
    src = Path(args.input).expanduser().resolve()
    if not src.is_file():
        _emit(f"ERROR: Datei nicht gefunden: {src}")
        return 1

    vendored_resolve = VENDOR_DIR / "intro_cut_resolve.py"
    resolve_script = vendored_resolve if vendored_resolve.is_file() else _projects_root() / "Intro cuter" / "intro_cut_resolve.py"
    if not resolve_script.is_file():
        _emit(f"ERROR: Resolve-CLI nicht gefunden: {resolve_script}")
        return 1

    cmd = [
        sys.executable,
        str(resolve_script),
        "--input",
        str(src),
        "--intro",
        str(args.intro),
        "--outro",
        str(args.outro),
    ]
    if args.preset:
        cmd += ["--preset", args.preset]
    if args.output_dir:
        cmd += ["--output-dir", str(Path(args.output_dir).expanduser().resolve())]

    settings = _load_intro_cutter_settings()
    env = _resolve_env_from_settings(settings)

    _emit("Resolve-Job startet…")
    proc = subprocess.run(
        cmd,
        env=env,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
    )
    if proc.stdout:
        for line in proc.stdout.splitlines():
            _emit(line)
    if proc.stderr:
        for line in proc.stderr.splitlines():
            _emit(line)
    if proc.returncode != 0:
        _emit(f"ERROR: Resolve-Job fehlgeschlagen (Code {proc.returncode})")
        return proc.returncode

    out_dir = Path(args.output_dir).expanduser().resolve() if args.output_dir else src.parent
    name = args.output_name or f"{src.stem}_resolve_cut"
    guess = out_dir / f"{name}{src.suffix or '.mp4'}"
    _emit(f"OUTPUT:{guess}")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="Hail Mary Intro Cutter Bridge")
    parser.add_argument("--input", required=True)
    parser.add_argument("--intro", type=float, default=3.0)
    parser.add_argument("--outro", type=float, default=2.0)
    parser.add_argument("--mode", choices=("ffmpeg", "resolve"), default="ffmpeg")
    parser.add_argument("--output-dir", default=None)
    parser.add_argument("--preset", default="YouTube - 1080p")
    parser.add_argument("--output-name", default=None)
    parser.add_argument("--vcodec", default="libx264")
    parser.add_argument("--vbitrate", default="8M")
    parser.add_argument("--vbitrate-auto", action="store_true")
    parser.add_argument("--acodec", default="aac")
    parser.add_argument("--abitrate", default="192k")
    args = parser.parse_args()

    if args.mode == "resolve":
        return run_resolve_mode(args)
    return run_ffmpeg_mode(args)


if __name__ == "__main__":
    raise SystemExit(main())
