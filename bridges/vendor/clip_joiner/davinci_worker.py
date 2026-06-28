#!/usr/bin/env python3
"""DaVinci-Worker — eigener Prozess (Hauptthread), JSON-Job als Argument."""

from __future__ import annotations

import json
import sys
from pathlib import Path

from utf8_io import enable_python_utf8_mode, ensure_utf8_stdio

enable_python_utf8_mode()
ensure_utf8_stdio()

_ROOT = Path(__file__).resolve().parent
if str(_ROOT) not in sys.path:
    sys.path.insert(0, str(_ROOT))

from resolve_pipeline import (  # noqa: E402
    ResolvePipelineError,
    join_and_render,
    resolve_davinci_api_path,
)


def main() -> int:
    if len(sys.argv) != 2:
        print("Usage: davinci_worker.py <job.json>", file=sys.stderr)
        return 2
    job_path = Path(sys.argv[1])
    try:
        with open(job_path, "r", encoding="utf-8") as f:
            job = json.load(f)
    except (OSError, json.JSONDecodeError) as ex:
        print(f"Job-Datei unlesbar: {ex}", file=sys.stderr)
        return 2

    try:
        join_and_render(
            clips=job["clips"],
            output_dir=job["output_dir"],
            output_name=job["output_name"],
            davinci_api_path=resolve_davinci_api_path(job.get("davinci_api_path", "")),
            preset_name=job.get("preset_name", "YouTube - 1080p"),
            width=int(job.get("width", 1920)),
            height=int(job.get("height", 1080)),
            analysis_fps=float(job.get("analysis_fps", 25.0)),
            timeout_s=float(job.get("timeout_s", 3600.0)),
        )
        return 0
    except ResolvePipelineError as ex:
        print(f"FEHLER: {ex}", file=sys.stderr)
        return 1
    except Exception as ex:
        print(f"FEHLER: {ex}", file=sys.stderr)
        raise


if __name__ == "__main__":
    raise SystemExit(main())
