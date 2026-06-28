#!/usr/bin/env python3
"""Oxco compare (single pair) bridge for Hail Mary."""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import threading
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))

from bridge_io import configure_stdio, emit

configure_stdio()


def _projects_root() -> Path:
    raw = os.environ.get("HAIL_MARY_PROJECTS_ROOT", "").strip()
    if raw:
        return Path(raw)
    return Path.home() / "Projects"


def _oxco_dir() -> Path:
    """Vendored Oxco-Quellen (im Paket enthalten) bevorzugen, sonst externes Repo."""
    vendored = ROOT / "vendor" / "oxco"
    if (vendored / "oxco_workers.py").is_file():
        return vendored
    return _projects_root() / "Oxco"


def _coerce_filter_bool(value, default: bool = False) -> bool:
    """JSON/C# booleans, 0/1, or strings — never treat \"false\" as True."""
    if value is None:
        return default
    if isinstance(value, bool):
        return value
    if isinstance(value, (int, float)):
        return value != 0
    if isinstance(value, str):
        v = value.strip().lower()
        if v in ("0", "false", "no", "off", "nein", ""):
            return False
        if v in ("1", "true", "yes", "on", "ja"):
            return True
    return bool(value)


def main() -> int:
    parser = argparse.ArgumentParser(description="Oxco compare bridge")
    parser.add_argument("--config-json", required=True)
    args = parser.parse_args()

    config_path = Path(args.config_json)
    if not config_path.is_file():
        emit(f"Konfiguration nicht gefunden: {config_path}")
        return 1

    data = json.loads(config_path.read_text(encoding="utf-8"))
    source = (data.get("source") or "").strip()
    deepfake = (data.get("deepfake") or "").strip()
    if not source or not deepfake:
        emit("Quelle und Deepfake-Pfad erforderlich.")
        return 1

    oxco_dir = _oxco_dir()
    if not oxco_dir.is_dir():
        emit(f"Oxco-Ordner fehlt: {oxco_dir}")
        return 1

    sys.path.insert(0, str(oxco_dir))
    try:
        import oxco_workers as ow
    except ImportError as exc:
        emit(f"Import fehlgeschlagen: {exc}")
        return 1

    ini_path = ow.compare_settings_path()
    if not ini_path.is_file():
        emit(f"settings.ini fehlt: {ini_path}")
        return 1

    base_ini = ow.read_settings_ini(ini_path)
    filters = data.get("filters") or {}
    phase = (data.get("pipeline_phase") or "full").strip().lower()
    checkpoint_path = (data.get("checkpoint_path") or "").strip()
    extra_args: list[str] = []
    env_extra: dict[str, str] = {}
    if checkpoint_path:
        env_extra["OXCO_CHECKPOINT_PATH"] = checkpoint_path

    enable_ffmpeg = _coerce_filter_bool(filters.get("enable_ffmpeg"), False)
    enable_davinci = _coerce_filter_bool(filters.get("enable_davinci"), True)
    export_unique = _coerce_filter_bool(filters.get("export_unique"), True)

    if phase == "analysis":
        extra_args.append("--defer-davinci")
        if not enable_ffmpeg and not enable_davinci:
            emit("FEHLER: Pipeline-Analyse braucht mindestens FFmpeg oder DaVinci in den Filtern.")
            return 1
        emit(
            f"[Oxco] Pipeline-Analyse: FFmpeg={'an' if enable_ffmpeg else 'aus'}, "
            f"DaVinci={'aus (nach Analyse)' if enable_davinci else 'aus'}"
        )
    elif phase == "davinci_export":
        extra_args.append("--davinci-export-only")
        enable_ffmpeg = False
        enable_davinci = True
        if not checkpoint_path:
            emit("FEHLER: checkpoint_path fehlt fuer davinci_export.")
            return 1
        emit("[Oxco] Pipeline: nur DaVinci-Export aus Checkpoint.")
    else:
        emit(
            f"[Oxco] Export-Filter: FFmpeg={'an' if enable_ffmpeg else 'aus'}, "
            f"DaVinci={'an' if enable_davinci else 'aus'}"
        )
        if not enable_ffmpeg and not enable_davinci:
            emit("FEHLER: Mindestens FFmpeg- oder DaVinci-Export muss aktiv sein.")
            return 1

    retry_export_only = bool(data.get("retry_export_only", False)) and phase == "full"
    try:
        patched = ow.apply_compare_overrides(
            base_ini,
            final_export_dir=str(filters.get("export_dir") or ""),
            language=str(filters.get("language") or "de"),
            buffer_seconds=float(filters.get("buffer_seconds", 2.0)),
            pixel_noise=int(filters.get("pixel_noise", 15)),
            changed_pixels=int(filters.get("changed_pixels", 200)),
            changed_pixels_max=int(filters.get("changed_pixels_max", 0)),
            enable_ffmpeg=enable_ffmpeg,
            ffmpeg_target=str(filters.get("ffmpeg_target") or "deepfake"),
            enable_davinci=enable_davinci,
            davinci_timeout=int(filters.get("davinci_timeout", 1800)),
            export_avoid_overwrite=export_unique,
            davinci_api_path=str(filters.get("davinci_api_path") or ""),
            davinci_render_preset=str(filters.get("davinci_render_preset") or ""),
            davinci_exe_path=str(filters.get("davinci_exe_path") or ""),
            davinci_startup_wait_seconds=int(filters.get("davinci_startup_wait", 20)),
            ffmpeg_encoder=str(filters.get("ffmpeg_encoder") or ""),
        )
    except (TypeError, ValueError) as exc:
        emit(f"Filter ungueltig: {exc}")
        return 1

    done = threading.Event()
    result: dict = {"rc": 1, "err": None}

    def log_line(msg: str) -> None:
        emit(msg)

    def on_done(rc: int, err: str | None) -> None:
        result["rc"] = rc
        result["err"] = err
        done.set()

    ow.run_compare_subprocess(
        source,
        deepfake,
        patched,
        log_line,
        on_done,
        retry_export_only=retry_export_only,
        extra_args=extra_args or None,
        env_extra=env_extra or None,
    )
    if not done.wait(timeout=float(data.get("timeout_seconds", 7200))):
        emit("ERROR: Compare-Timeout")
        return 1

    if result["err"]:
        emit(f"ERROR: {result['err']}")
    return int(result["rc"])


if __name__ == "__main__":
    raise SystemExit(main())
