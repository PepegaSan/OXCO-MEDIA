# -*- coding: utf-8 -*-
"""DeepFilterNet: CLI (``deep-filter.exe`` im Projektordner) oder Python-``df``.

Die offizielle CLI erwartet PCM-WAV — ein kurzer FFmpeg-Schritt auf 48 kHz
Mono ist dafür nötig (kein separates „WAV-Projekt-Layout“ wie in anderen Tools).
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Callable, Optional

LogFn = Optional[Callable[[str], None]]

DEEPFILTER_SR = 48000

_THIS_DIR = Path(__file__).resolve().parent


def _no_console_flags() -> int:
    return getattr(subprocess, "CREATE_NO_WINDOW", 0)


def _bundle_search_roots() -> list[Path]:
    roots: list[Path] = []
    if getattr(sys, "frozen", False):
        exe_dir = Path(sys.executable).resolve().parent
        roots.append(exe_dir)
        internal = exe_dir / "_internal"
        if internal.is_dir():
            roots.append(internal)
        meipass = getattr(sys, "_MEIPASS", None)
        if meipass:
            roots.append(Path(meipass))
    roots.append(_THIS_DIR)
    seen: set[Path] = set()
    out: list[Path] = []
    for r in roots:
        if r in seen:
            continue
        seen.add(r)
        out.append(r)
    return out


def resolve_deepfilter_cli() -> Optional[str]:
    """Pfad zur DeepFilterNet-CLI oder ``None``."""
    candidates: list[str] = []

    for search_root in _bundle_search_roots():
        if sys.platform.startswith("win"):
            for n in ("deep-filter.exe", "deepFilter.exe"):
                candidates.append(str(search_root / n))
            for pat in ("deep-filter-*.exe", "deepFilter-*.exe"):
                candidates.extend(str(p) for p in sorted(search_root.glob(pat), reverse=True))
        else:
            for n in ("deep-filter", "deepFilter"):
                candidates.append(str(search_root / n))
            for pat in ("deep-filter-*", "deepFilter-*"):
                for p in sorted(search_root.glob(pat), reverse=True):
                    if p.is_file() and os.access(p, os.X_OK):
                        candidates.append(str(p))

    bin_dir = Path(sys.executable).resolve().parent
    if sys.platform.startswith("win"):
        for sub in (bin_dir, bin_dir / "Scripts"):
            if sub.is_dir():
                for n in (
                    "deep-filter.exe",
                    "deep-filter.cmd",
                    "deepFilter.exe",
                    "deepFilter.cmd",
                ):
                    candidates.append(str(sub / n))
    else:
        candidates.extend(str(bin_dir / n) for n in ("deep-filter", "deepFilter"))

    for cand in candidates:
        if cand and os.path.isfile(cand):
            return cand

    for name in ("deep-filter", "deepFilter"):
        hit = shutil.which(name)
        if hit:
            return hit
    return None


def extract_audio_for_deepfilter(
    ffmpeg: str,
    media_path: str,
    output_wav: str,
    *,
    start_s: float = 0.0,
    end_s: float | None = None,
    log: LogFn = None,
) -> None:
    """Ein FFmpeg-Lauf: erster Audiostream → 48 kHz mono PCM (für DeepFilter)."""
    if not os.path.isfile(media_path):
        raise FileNotFoundError(media_path)
    out = Path(output_wav)
    out.parent.mkdir(parents=True, exist_ok=True)

    cmd: list[str | float] = [
        ffmpeg,
        "-hide_banner",
        "-loglevel",
        "error",
        "-y",
    ]
    if start_s and start_s > 0:
        cmd += ["-ss", f"{float(start_s):.3f}"]
    cmd += ["-i", str(media_path)]
    if end_s is not None:
        cmd += ["-to", f"{float(end_s):.3f}"]
    cmd += [
        "-vn",
        "-map",
        "0:a:0",
        "-ac",
        "1",
        "-ar",
        str(DEEPFILTER_SR),
        "-c:a",
        "pcm_s16le",
        str(out),
    ]
    cmd_s = [str(x) for x in cmd]
    if log:
        log("FFmpeg: Segment für DeepFilter (48 kHz mono)…\n")
    r = subprocess.run(
        cmd_s,
        capture_output=True,
        text=True,
        creationflags=_no_console_flags(),
        timeout=3600,
    )
    if r.returncode != 0:
        tail = (r.stderr or "").strip().splitlines()[-8:]
        raise RuntimeError(
            "FFmpeg-Extraktion fehlgeschlagen (Code %s)\n%s"
            % (r.returncode, "\n".join(tail) or "(kein stderr)")
        )
    if not out.is_file() or out.stat().st_size == 0:
        raise RuntimeError("FFmpeg hat keine WAV erzeugt — Audiostream vorhanden?")


def _denoise_cli(
    cli: str,
    input_wav: str,
    output_wav: str,
    *,
    log: LogFn = None,
    cancel_event: object | None = None,
) -> bool:
    work = Path(tempfile.mkdtemp(prefix="df_out_"))
    try:
        cmd = [cli, "--output-dir", str(work), str(input_wav)]
        if log:
            log("DeepFilterNet: Rauschreduktion…\n")
        proc = subprocess.Popen(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            creationflags=_no_console_flags(),
        )
        assert proc.stdout
        for line in proc.stdout:
            if cancel_event is not None and getattr(cancel_event, "is_set", lambda: False)():
                proc.terminate()
                try:
                    proc.wait(timeout=8)
                except subprocess.TimeoutExpired:
                    proc.kill()
                if log:
                    log("[DeepFilterNet] abgebrochen.\n")
                raise InterruptedError("DeepFilterNet abgebrochen")
            if log and line:
                log(line)
        rc = proc.wait()
        if rc != 0:
            if log:
                log(f"[DeepFilterNet] CLI-Exit {rc}\n")
            return False
        produced = list(work.glob("*.wav"))
        if not produced:
            return False
        src = sorted(produced)[-1]
        outp = Path(output_wav)
        outp.parent.mkdir(parents=True, exist_ok=True)
        if outp.is_file():
            outp.unlink()
        shutil.move(str(src), str(outp))
        return True
    finally:
        shutil.rmtree(work, ignore_errors=True)


def _denoise_python(
    input_wav: str,
    output_wav: str,
    *,
    log: LogFn = None,
) -> None:
    try:
        from df.enhance import enhance, init_df, load_audio, save_audio  # type: ignore
    except ImportError as e:
        raise RuntimeError(
            "DeepFilterNet nicht gefunden. ``deep-filter.exe`` in den App-Ordner legen "
            "oder ``pip install deepfilternet``.\n"
            f"Details: {e}"
        ) from e

    if log:
        log("DeepFilterNet (Python): Modell …\n")
    model, df_state, _ = init_df()
    sr = df_state.sr()
    audio, _ = load_audio(input_wav, sr=sr)
    if log:
        log("DeepFilterNet (Python): Verarbeitung …\n")
    enhanced = enhance(model, df_state, audio)
    outp = Path(output_wav)
    outp.parent.mkdir(parents=True, exist_ok=True)
    save_audio(str(outp), enhanced, sr)


def denoise_wav_file(
    input_wav: str,
    output_wav: str,
    *,
    log: LogFn = None,
    cancel_event: object | None = None,
) -> None:
    if not os.path.isfile(input_wav):
        raise FileNotFoundError(input_wav)
    cli = resolve_deepfilter_cli()
    if cli:
        try:
            ok = _denoise_cli(cli, input_wav, output_wav, log=log, cancel_event=cancel_event)
        except InterruptedError:
            raise
        if ok:
            if log:
                log(f"DeepFilterNet: fertig → {Path(output_wav).name}\n")
            return
    _denoise_python(input_wav, output_wav, log=log)
    if log:
        log(f"DeepFilterNet (Python): fertig → {Path(output_wav).name}\n")


def run_deepfilter_pipeline(
    ffmpeg: str,
    media_in: str,
    clean_wav_out: str,
    *,
    trim_start: float = 0.0,
    trim_end: float | None = None,
    log: LogFn = None,
    cancel_event: object | None = None,
) -> None:
    """FFmpeg → temporäre WAV → DeepFilter → ``clean_wav_out`` (Datei liegt außerhalb des Temp)."""
    with tempfile.TemporaryDirectory(prefix="acc_df_") as td_s:
        td = Path(td_s)
        raw = td / "raw.wav"
        extract_audio_for_deepfilter(
            ffmpeg,
            media_in,
            str(raw),
            start_s=trim_start,
            end_s=trim_end,
            log=log,
        )
        if cancel_event is not None and getattr(cancel_event, "is_set", lambda: False)():
            raise InterruptedError("Abgebrochen")
        denoise_wav_file(str(raw), clean_wav_out, log=log, cancel_event=cancel_event)


# Abwärtskompatibel falls Import unter altem Namen
extract_audio_wav_segment = extract_audio_for_deepfilter
