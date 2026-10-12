"""Run FFmpeg with live progress for Hail Mary bridges.

FFmpeg writes machine-readable progress to stdout (``-progress pipe:1``); stderr goes to a temp
file so a chatty encoder can never block the pipe. On failure the stderr tail is raised.
"""
from __future__ import annotations

import os
import subprocess
import sys
import tempfile
from typing import Callable, List, Optional

ProgressFn = Callable[[float, str], None]


def run_ffmpeg(
    command: List[str],
    *,
    total_seconds: float,
    progress: Optional[ProgressFn] = None,
    label: str = "Export",
    log: Optional[Callable[[str], None]] = None,
) -> None:
    """Run *command* (output path last) and report 0–100 % via *progress*."""
    if not command:
        raise ValueError("empty ffmpeg command")
    cmd = list(command[:-1]) + ["-progress", "pipe:1", "-nostats", command[-1]]
    if log:
        log("$ " + " ".join(cmd))

    total_us = max(1.0, float(total_seconds) * 1_000_000.0)
    err_fd, err_path = tempfile.mkstemp(prefix="hm_ffmpeg_", suffix=".log")
    os.close(err_fd)
    try:
        with open(err_path, "wb") as err:
            proc = subprocess.Popen(
                cmd,
                stdout=subprocess.PIPE,
                stderr=err,
                stdin=subprocess.DEVNULL,
                text=True,
                encoding="utf-8",
                errors="replace",
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0) if sys.platform == "win32" else 0,
            )
            assert proc.stdout is not None
            last_pct = -1.0
            for line in proc.stdout:
                key, _, value = line.strip().partition("=")
                if key in ("out_time_us", "out_time_ms") and value.lstrip("-").isdigit():
                    # Both keys carry microseconds in current FFmpeg builds.
                    pct = min(99.5, max(0.0, int(value) / total_us * 100.0))
                    if progress and pct - last_pct >= 0.5:
                        progress(pct, label)
                        last_pct = pct
            code = proc.wait()
        if code != 0:
            with open(err_path, "r", encoding="utf-8", errors="replace") as fh:
                tail = fh.read()[-1500:]
            raise RuntimeError(f"ffmpeg fehlgeschlagen (Code {code}):\n{tail}")
        if progress:
            progress(100.0, label)
    finally:
        try:
            os.unlink(err_path)
        except OSError:
            pass
