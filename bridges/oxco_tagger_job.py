#!/usr/bin/env python3
"""Oxco autotagger batch + tag routing for Hail Mary."""
from __future__ import annotations

import argparse
import json
import os
import sys
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


def main() -> int:
    parser = argparse.ArgumentParser(description="Oxco tagger bridge")
    parser.add_argument("--config-json", required=True)
    parser.add_argument("--output-json", required=True)
    args = parser.parse_args()

    data = json.loads(Path(args.config_json).read_text(encoding="utf-8"))
    out_path = Path(args.output_json)
    mode = (data.get("mode") or "process").strip().lower()

    oxco_dir = _oxco_dir()
    sys.path.insert(0, str(oxco_dir))
    import oxco_workers as ow  # noqa: E402

    logs: list[str] = []

    def log(msg: str) -> None:
        logs.append(msg)
        emit(msg)

    if mode == "distribute":
        source = Path((data.get("source_dir") or "").strip())
        rules = ow.normalize_tag_route_rules(data.get("route_rules") or [])
        moved, no_match, errors = ow.tagger_distribute_by_rules(source, rules, log)
        result = {"moved": moved, "no_match": no_match, "errors": errors, "log": logs}
        out_path.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
        return 0

    input_dir = Path((data.get("input_dir") or "").strip())
    output_dir = Path((data.get("output_dir") or "").strip())
    only_raw = data.get("only_files") or []
    only_files = [Path(p) for p in only_raw if str(p).strip()]

    ok, skipped = ow.tagger_process_folder(
        input_dir,
        output_dir,
        tag=str(data.get("tag") or "[Stash]"),
        profile_name=str(data.get("profile_name") or "Schritt1"),
        keep_suffix_csv=str(data.get("keep_suffix_csv") or ""),
        ignore_suffix_csv=str(data.get("ignore_suffix_csv") or ""),
        drop_suffix_csv=str(data.get("drop_suffix_csv") or ""),
        pattern_text=str(data.get("pattern_text") or "YYMMDDHHmmSS"),
        log=log,
        only_files=only_files if only_files else None,
        filter_buffer_seconds=float(data.get("filter_buffer_seconds", 2.0)),
        filter_noise_threshold=int(data.get("filter_noise_threshold", 15)),
        filter_pixel_threshold=int(data.get("filter_pixel_threshold", 200)),
        filter_pixel_max_threshold=int(data.get("filter_pixel_max_threshold", 0)),
        bitrate_output_suffix=str(data.get("bitrate_output_suffix") or "_bitrate"),
    )
    result = {"ok": ok, "skipped": skipped, "log": logs}
    out_path.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
