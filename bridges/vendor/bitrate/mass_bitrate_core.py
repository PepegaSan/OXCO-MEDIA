"""Kernlogik aus Videobitratechanger/mass_bitrate_gui.py (Kopie, Original unveraendert)."""

from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import time
from concurrent.futures import FIRST_COMPLETED, ThreadPoolExecutor, as_completed, wait
from dataclasses import asdict, dataclass
from pathlib import Path
from threading import Event, Lock
from typing import Any, Callable, Dict, List, Optional, Set, Tuple

VIDEO_EXTENSIONS = {
    ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".ts", ".flv",
}

# Stash/Browser ohne NAS-HW: nur H.264 gilt als OK (HEVC/AV1/VP9 → Problemfall).
FRIENDLY_VIDEO_CODECS = frozenset({"h264"})

SCAN_MODE_BITRATE = "bitrate"
SCAN_MODE_PROBLEM_CODECS = "problem_codecs"
SCAN_MODE_BITRATE_OR_CODECS = "bitrate_or_codecs"

RULE_ORDER = [2160, 1440, 1080, 720, 480, 360, 0]

BUILTIN_PRESETS: Dict[str, Dict[int, int]] = {
    "Standard": {2160: 12000, 1440: 8000, 1080: 5000, 720: 2800, 480: 1500, 360: 900, 0: 700},
    "Leicht reduziert": {2160: 8000, 1440: 6000, 1080: 4000, 720: 2000, 480: 1000, 360: 800, 0: 700},
    "Reduziert": {2160: 6000, 1440: 4000, 1080: 3000, 720: 1500, 480: 800, 360: 600, 0: 500},
}


@dataclass
class VideoInfo:
    path: str
    width: int
    height: int
    source_size_bytes: Optional[int]
    source_kbps: Optional[int]
    target_rule_kbps: Optional[int]
    effective_target_kbps: Optional[int]
    estimated_output_bytes: Optional[int]
    estimated_saved_bytes: Optional[int]
    estimated_saved_pct: Optional[float]
    action: str
    reason: str
    video_codec: Optional[str] = None


def is_problem_codec(codec: Optional[str]) -> bool:
    if not codec:
        return True
    return codec.strip().lower() not in FRIENDLY_VIDEO_CODECS


def run_ffprobe(path: Path) -> Tuple[Optional[int], Optional[int], Optional[int], Optional[str]]:
    cmd = [
        "ffprobe", "-v", "error", "-print_format", "json",
        "-show_entries", "stream=width,height,bit_rate,codec_name", "-select_streams", "v:0",
        "-show_entries", "format=bit_rate", str(path),
    ]
    completed = subprocess.run(
        cmd, capture_output=True, text=True, encoding="utf-8", errors="replace",
        check=False, creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
    )
    if completed.returncode != 0:
        return None, None, None, None
    try:
        payload = json.loads(completed.stdout or "{}")
    except json.JSONDecodeError:
        return None, None, None, None
    streams = payload.get("streams") or []
    video_stream = streams[0] if streams else None
    if not video_stream:
        return None, None, None, None
    width = int(video_stream["width"]) if video_stream.get("width") is not None else None
    height = int(video_stream["height"]) if video_stream.get("height") is not None else None
    codec_raw = video_stream.get("codec_name")
    codec = str(codec_raw).strip().lower() if codec_raw else None
    stream_bitrate_raw = video_stream.get("bit_rate")
    format_bitrate_raw = (payload.get("format") or {}).get("bit_rate")
    bitrate_bps = None
    for raw in (stream_bitrate_raw, format_bitrate_raw):
        if raw is None:
            continue
        try:
            bitrate_bps = int(raw)
            break
        except (TypeError, ValueError):
            continue
    kbps = int(bitrate_bps / 1000) if bitrate_bps and bitrate_bps > 0 else None
    return width, height, kbps, codec


def estimate_sizes(source_size_bytes: int, source_kbps: int, target_kbps: int) -> Tuple[int, int, float]:
    if source_size_bytes <= 0 or source_kbps <= 0 or target_kbps <= 0:
        return source_size_bytes, 0, 0.0
    ratio = min(1.0, target_kbps / source_kbps)
    estimated_output = int(source_size_bytes * ratio)
    saved = max(0, source_size_bytes - estimated_output)
    saved_pct = (saved / source_size_bytes) * 100.0 if source_size_bytes > 0 else 0.0
    return estimated_output, saved, saved_pct


def pick_rule_for_short_side(width: int, height: int, rules: Dict[int, int]) -> int:
    """Bitrate-Stufe nach kürzerer Kante (1080×1920 → 1080p, nicht 1440p)."""
    short_side = min(int(width), int(height))
    for threshold in sorted(rules.keys(), reverse=True):
        if short_side >= threshold:
            return rules[threshold]
    return rules[min(rules.keys())]


def iter_video_files(folder: Path, recursive: bool) -> List[Path]:
    pattern = "**/*" if recursive else "*"
    files = [p for p in folder.glob(pattern) if p.is_file() and p.suffix.lower() in VIDEO_EXTENSIONS]
    files.sort()
    return files


def parse_rules(rule_values: Dict[str, str]) -> Dict[int, int]:
    rules: Dict[int, int] = {}
    for threshold in RULE_ORDER:
        raw = rule_values.get(str(threshold), str(BUILTIN_PRESETS["Standard"][threshold]))
        rules[threshold] = int(str(raw).strip())
    return rules


def analyze_file(
    file_path: Path,
    rules: Dict[int, int],
    only_lower: bool,
    scan_mode: str = SCAN_MODE_BITRATE,
) -> VideoInfo:
    width, height, source_kbps, video_codec = run_ffprobe(file_path)
    if not width or not height:
        return VideoInfo(
            path=str(file_path), width=0, height=0,
            source_size_bytes=file_path.stat().st_size if file_path.exists() else None,
            source_kbps=None, target_rule_kbps=None, effective_target_kbps=None,
            estimated_output_bytes=None, estimated_saved_bytes=None, estimated_saved_pct=None,
            action="skip", reason="Aufloesung unbekannt", video_codec=video_codec,
        )
    source_size = file_path.stat().st_size if file_path.exists() else None
    rule = pick_rule_for_short_side(width, height, rules)
    codec_problem = is_problem_codec(video_codec)
    codec_label = video_codec or "?"
    mode = (scan_mode or SCAN_MODE_BITRATE).strip().lower()

    # Nur problematische Codecs: gute Codecs immer skippen (Bitrate egal).
    if mode == SCAN_MODE_PROBLEM_CODECS and not codec_problem:
        return VideoInfo(
            path=str(file_path), width=width, height=height, source_size_bytes=source_size,
            source_kbps=source_kbps, target_rule_kbps=rule, effective_target_kbps=None,
            estimated_output_bytes=None, estimated_saved_bytes=None, estimated_saved_pct=None,
            action="skip", reason=f"Codec OK ({codec_label})", video_codec=video_codec,
        )

    if source_kbps is None:
        # Ohne Bitrate: in Codec-Modi trotzdem konvertieren (Ziel = Regelbitrate).
        if mode in {SCAN_MODE_PROBLEM_CODECS, SCAN_MODE_BITRATE_OR_CODECS} and codec_problem:
            return VideoInfo(
                path=str(file_path), width=width, height=height, source_size_bytes=source_size,
                source_kbps=None, target_rule_kbps=rule, effective_target_kbps=rule,
                estimated_output_bytes=None, estimated_saved_bytes=None, estimated_saved_pct=None,
                action="convert", reason=f"Problematischer Codec ({codec_label})",
                video_codec=video_codec,
            )
        return VideoInfo(
            path=str(file_path), width=width, height=height, source_size_bytes=source_size,
            source_kbps=None, target_rule_kbps=rule, effective_target_kbps=None,
            estimated_output_bytes=None, estimated_saved_bytes=None, estimated_saved_pct=None,
            action="skip", reason="Bitrate unbekannt", video_codec=video_codec,
        )

    effective_target = min(source_kbps, rule)
    est_out = est_save = est_save_pct = None
    if source_size is not None:
        est_out, est_save, est_save_pct = estimate_sizes(source_size, source_kbps, effective_target)

    bitrate_needs_convert = not (only_lower and effective_target >= source_kbps)

    if mode == SCAN_MODE_PROBLEM_CODECS:
        # Qualität möglichst halten: Quellbitrate als Ziel, nicht zwingend senken.
        return VideoInfo(
            path=str(file_path), width=width, height=height, source_size_bytes=source_size,
            source_kbps=source_kbps, target_rule_kbps=rule, effective_target_kbps=source_kbps,
            estimated_output_bytes=source_size, estimated_saved_bytes=0, estimated_saved_pct=0.0,
            action="convert", reason=f"Problematischer Codec ({codec_label})",
            video_codec=video_codec,
        )

    if bitrate_needs_convert:
        return VideoInfo(
            path=str(file_path), width=width, height=height, source_size_bytes=source_size,
            source_kbps=source_kbps, target_rule_kbps=rule, effective_target_kbps=effective_target,
            estimated_output_bytes=est_out, estimated_saved_bytes=est_save, estimated_saved_pct=est_save_pct,
            action="convert", reason="Reduzieren", video_codec=video_codec,
        )

    if mode == SCAN_MODE_BITRATE_OR_CODECS and codec_problem:
        return VideoInfo(
            path=str(file_path), width=width, height=height, source_size_bytes=source_size,
            source_kbps=source_kbps, target_rule_kbps=rule, effective_target_kbps=source_kbps,
            estimated_output_bytes=source_size, estimated_saved_bytes=0, estimated_saved_pct=0.0,
            action="convert", reason=f"Problematischer Codec ({codec_label})",
            video_codec=video_codec,
        )

    return VideoInfo(
        path=str(file_path), width=width, height=height, source_size_bytes=source_size,
        source_kbps=source_kbps, target_rule_kbps=rule, effective_target_kbps=effective_target,
        estimated_output_bytes=est_out, estimated_saved_bytes=est_save, estimated_saved_pct=est_save_pct,
        action="skip", reason="Keine Einsparung", video_codec=video_codec,
    )


def scan_folder(
    input_folder: str,
    recursive: bool,
    only_lower: bool,
    rule_values: Dict[str, str],
    progress_cb: Optional[Callable[[int, int], None]] = None,
    scan_mode: str = SCAN_MODE_BITRATE,
) -> List[VideoInfo]:
    in_path = Path(input_folder)
    rules = parse_rules(rule_values)
    mode = (scan_mode or SCAN_MODE_BITRATE).strip().lower()
    files = iter_video_files(in_path, recursive)
    total = len(files)
    if total == 0:
        return []
    workers = min(8, max(2, (os.cpu_count() or 4)))
    rows_map: Dict[Path, VideoInfo] = {}
    processed = 0
    with ThreadPoolExecutor(max_workers=workers) as pool:
        future_map = {
            pool.submit(analyze_file, fp, rules, only_lower, mode): fp for fp in files
        }
        for future in as_completed(future_map):
            fp = future_map[future]
            try:
                rows_map[fp] = future.result()
            except Exception:
                rows_map[fp] = VideoInfo(
                    path=str(fp), width=0, height=0,
                    source_size_bytes=fp.stat().st_size if fp.exists() else None,
                    source_kbps=None, target_rule_kbps=None, effective_target_kbps=None,
                    estimated_output_bytes=None, estimated_saved_bytes=None, estimated_saved_pct=None,
                    action="skip", reason="Scan-Fehler", video_codec=None,
                )
            processed += 1
            if progress_cb and (processed == total or processed % max(1, total // 40) == 0):
                progress_cb(processed, total)
    return [rows_map[p] for p in files]


def clamp_crf(value: Any, default: int = 20) -> int:
    try:
        crf = int(value)
    except (TypeError, ValueError):
        crf = default
    return max(0, min(51, crf))


def clamp_int(value: Any, default: int, lo: int, hi: int) -> int:
    try:
        parsed = int(value)
    except (TypeError, ValueError):
        parsed = default
    return max(lo, min(hi, parsed))


# Windows: BELOW_NORMAL_PRIORITY_CLASS — Encode neben Spielen freundlicher.
_BELOW_NORMAL_PRIORITY_CLASS = 0x00004000


def build_ffmpeg_cmd(
    src: Path,
    dst: Path,
    target_kbps: Optional[int],
    codec: str,
    audio_mode: str,
    *,
    use_crf: bool = False,
    crf: int = 20,
    threads: int = 0,
    x264_preset: str = "medium",
    target_fps: Optional[float] = None,
) -> List[str]:
    cmd = [
        "ffmpeg", "-y", "-hide_banner", "-loglevel", "warning",
    ]
    if threads and threads > 0:
        cmd.extend(["-threads", str(int(threads))])
    cmd.extend(["-i", str(src), "-c:v", codec])
    crf_val = clamp_crf(crf)
    preset = (x264_preset or "medium").strip() or "medium"
    if use_crf:
        if codec in {"h264_nvenc", "hevc_nvenc"}:
            cmd.extend([
                "-rc:v", "vbr", "-cq:v", str(crf_val), "-preset", "p5",
                "-profile:v", "high" if codec == "h264_nvenc" else "main",
            ])
        elif codec == "libvpx-vp9":
            cmd.extend(["-b:v", "0", "-crf", str(crf_val)])
        else:
            # libx264 / libx265
            cmd.extend(["-crf", str(crf_val)])
            if codec in {"libx264", "libx265"}:
                cmd.extend(["-preset", preset])
    else:
        kbps = int(target_kbps or 0)
        if kbps <= 0:
            raise ValueError("target_kbps fehlt fuer Bitrate-Encode")
        cmd.extend([
            "-b:v", f"{kbps}k", "-maxrate", f"{kbps}k", "-bufsize", f"{kbps * 2}k",
        ])
        if codec in {"h264_nvenc", "hevc_nvenc"}:
            cmd.extend([
                "-rc:v", "vbr", "-cq:v", "23", "-preset", "p5",
                "-profile:v", "high" if codec == "h264_nvenc" else "main",
            ])
        elif codec in {"libx264", "libx265"}:
            cmd.extend(["-preset", preset])
    if audio_mode == "aac_128k":
        cmd.extend(["-c:a", "aac", "-b:a", "128k"])
    else:
        cmd.extend(["-c:a", "copy"])
    try:
        from ffmpeg_fps import fps_output_args
    except ImportError:
        import sys
        from pathlib import Path as _P
        sys.path.insert(0, str(_P(__file__).resolve().parents[2]))
        from ffmpeg_fps import fps_output_args
    cmd.extend(fps_output_args(target_fps))
    if dst.suffix.lower() in {".mp4", ".m4v", ".mov"}:
        cmd.extend(["-movflags", "+faststart"])
    cmd.append(str(dst))
    return cmd


def _ffmpeg_creationflags(low_priority: bool) -> int:
    flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
    if os.name == "nt" and low_priority:
        flags |= _BELOW_NORMAL_PRIORITY_CLASS
    return flags


def _same_folder(a: Path, b: Path) -> bool:
    try:
        return a.resolve() == b.resolve()
    except Exception:
        return str(a).lower() == str(b).lower()


def _paths_conflict(a: Path, b: Path) -> bool:
    try:
        return a.resolve() == b.resolve()
    except Exception:
        return str(a).lower() == str(b).lower()


def effective_suffix(input_root: Path, output_root: Path, source: Path, planned_out: Path, suffix: str) -> str:
    raw = suffix.strip()
    if raw:
        return raw
    if _same_folder(input_root, output_root):
        try:
            if source.name.lower() != planned_out.name.lower():
                return ""
        except Exception:
            return "_bitrate"
        return "_bitrate"
    return ""


def is_valid_output(path: Path) -> bool:
    if not path.exists() or path.stat().st_size <= 0:
        return False
    w, h, _, _ = run_ffprobe(path)
    return bool(w and h)


def _try_delete_source(src: Path, final_out: Path, emit_fn) -> None:
    """Original im Eingangsordner löschen — nur wenn von der Ausgabedatei verschieden."""
    try:
        src_res = src.resolve()
        out_res = final_out.resolve()
        if src_res == out_res:
            return
        if not src_res.is_file():
            return
        src_res.unlink()
        emit_fn(f"Original gelöscht: {src.name}")
    except OSError as exc:
        emit_fn(f"WARN: Original nicht gelöscht ({src.name}): {exc}")


def _try_strip_autobitrate_suffix(
    output_path: Path,
    source_path: Path,
    emit_fn,
    *,
    marker: str = "_bitrate",
) -> Path:
    """Entfernt _bitrate vom Ausgabedateinamen; ersetzt das Original wenn noetig."""
    stem = output_path.stem
    if not stem.endswith(marker):
        return output_path
    new_stem = stem[: -len(marker)]
    if not new_stem:
        return output_path
    target = output_path.with_name(f"{new_stem}{output_path.suffix}")
    if _paths_conflict(output_path, target):
        return output_path
    try:
        output_res = output_path.resolve()
        target_res = target.resolve()
        source_res = source_path.resolve()
    except Exception:
        output_res = output_path
        target_res = target
        source_res = source_path
    if target.exists():
        if target_res == source_res:
            try:
                source_path.unlink()
                emit_fn(f"Original entfernt fuer Suffix-Rename: {source_path.name}")
            except OSError as exc:
                emit_fn(f"WARN: _bitrate nicht entfernt ({output_path.name}): {exc}")
                return output_path
        elif target_res != output_res:
            try:
                target.unlink()
                emit_fn(f"Bestehende Datei ersetzt: {target.name}")
            except OSError as exc:
                emit_fn(f"WARN: _bitrate nicht entfernt — Ziel blockiert ({target.name}): {exc}")
                return output_path
    try:
        output_path.rename(target)
        emit_fn(f"Suffix entfernt: {output_path.name} -> {target.name}")
        return target
    except OSError as exc:
        emit_fn(f"WARN: Suffix-Rename fehlgeschlagen ({output_path.name}): {exc}")
        return output_path


def _kill_process_tree(proc: subprocess.Popen) -> None:
    if proc.poll() is not None:
        return
    try:
        if os.name == "nt":
            subprocess.run(
                ["taskkill", "/PID", str(proc.pid), "/T", "/F"],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                check=False,
                creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
            )
        else:
            proc.kill()
    except Exception:
        try:
            proc.kill()
        except Exception:
            pass
    try:
        proc.wait(timeout=5)
    except Exception:
        pass


def _safe_unlink(path: Path) -> None:
    try:
        if path.exists():
            path.unlink()
    except Exception:
        pass


def convert_rows(
    config: Dict[str, Any],
    rows: List[Dict[str, Any]],
    emit_fn,
    progress_cb: Optional[Callable[[int, int], None]] = None,
    stop_event: Optional[Event] = None,
) -> int:
    input_root = Path(config["input_folder"])
    output_root = Path(config["output_folder"])
    output_root.mkdir(parents=True, exist_ok=True)
    codec = config.get("codec", "libx264")
    audio_mode = config.get("audio_mode", "copy")
    suffix = config.get("suffix", "_bitrate")
    output_mp4 = bool(config.get("output_mp4", False))
    strip_suffix = bool(config.get("strip_autobitrate_suffix", False))
    post_action = config.get("post_success_action", "keep")
    rate_control = str(config.get("rate_control") or "bitrate").strip().lower()
    crf_value = clamp_crf(config.get("crf", 20))
    parallel_jobs = clamp_int(config.get("parallel_jobs", 1), 1, 1, 8)
    ffmpeg_threads = clamp_int(config.get("ffmpeg_threads", 6), 6, 0, 64)
    low_priority = bool(config.get("low_priority", True))
    x264_preset = str(config.get("x264_preset") or "medium").strip() or "medium"
    try:
        from ffmpeg_fps import normalize_target_fps
    except ImportError:
        import sys
        from pathlib import Path as _P
        sys.path.insert(0, str(_P(__file__).resolve().parents[2]))
        from ffmpeg_fps import normalize_target_fps
    target_fps = normalize_target_fps(config.get("target_fps"))
    creationflags = _ffmpeg_creationflags(low_priority)
    stop = stop_event or Event()

    def row_uses_crf(row: Dict[str, Any]) -> bool:
        if rate_control == "crf":
            return True
        if rate_control == "auto":
            reason = str(row.get("reason") or "")
            return reason.startswith("Problematischer Codec")
        return False

    jobs = [
        r for r in rows
        if r.get("action") == "convert"
        and (row_uses_crf(r) or r.get("effective_target_kbps"))
    ]
    total_jobs = len(jobs)
    if total_jobs == 0:
        return 0

    emit_lock = Lock()
    done_lock = Lock()
    proc_lock = Lock()
    active_procs: Set[subprocess.Popen] = set()
    done = 0
    cancelled = False

    def safe_emit(msg: str) -> None:
        with emit_lock:
            emit_fn(msg)

    def kill_all_ffmpeg() -> None:
        with proc_lock:
            procs = list(active_procs)
            active_procs.clear()
        for proc in procs:
            _kill_process_tree(proc)

    def convert_one(row: Dict[str, Any]) -> bool:
        nonlocal cancelled
        if stop.is_set():
            cancelled = True
            return False

        src = Path(row["path"])
        rel = src.relative_to(input_root) if src.is_relative_to(input_root) else Path(src.name)
        out_file = output_root / rel
        out_file.parent.mkdir(parents=True, exist_ok=True)
        planned_ext = ".mp4" if output_mp4 else rel.suffix
        planned_out = (out_file.parent / rel.name).with_suffix(planned_ext)
        eff_suffix = effective_suffix(input_root, output_root, src, planned_out, suffix)
        final_out = out_file.parent / f"{rel.stem}{eff_suffix}{planned_ext}"
        work_out = final_out
        if _paths_conflict(src, final_out):
            work_out = final_out.with_name(f"{final_out.stem}.partial{final_out.suffix}")

        use_crf = row_uses_crf(row)
        kbps = int(row["effective_target_kbps"]) if row.get("effective_target_kbps") else 0
        if use_crf:
            safe_emit(f"Konvertiere: {src.name} -> CRF {crf_value}")
            cmd = build_ffmpeg_cmd(
                src, work_out, kbps or None, codec, audio_mode,
                use_crf=True, crf=crf_value,
                threads=ffmpeg_threads, x264_preset=x264_preset,
                target_fps=target_fps,
            )
        else:
            if kbps <= 0:
                safe_emit(f"FEHLER: {src.name} (keine Zielbitrate)")
                return False
            safe_emit(f"Konvertiere: {src.name} -> {kbps} kbps")
            cmd = build_ffmpeg_cmd(
                src, work_out, kbps, codec, audio_mode, use_crf=False,
                threads=ffmpeg_threads, x264_preset=x264_preset,
                target_fps=target_fps,
            )

        proc: Optional[subprocess.Popen] = None
        try:
            proc = subprocess.Popen(
                cmd,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.PIPE,
                text=True,
                encoding="utf-8",
                errors="replace",
                creationflags=creationflags,
            )
            with proc_lock:
                active_procs.add(proc)

            while proc.poll() is None:
                if stop.is_set():
                    cancelled = True
                    _kill_process_tree(proc)
                    _safe_unlink(work_out)
                    if not _paths_conflict(work_out, final_out):
                        _safe_unlink(final_out)
                    return False
                time.sleep(0.2)

            stderr_text = ""
            try:
                if proc.stderr is not None:
                    stderr_text = proc.stderr.read() or ""
            except Exception:
                pass

            if proc.returncode != 0 or not is_valid_output(work_out):
                if stop.is_set():
                    cancelled = True
                    _safe_unlink(work_out)
                    return False
                safe_emit(f"FEHLER: {src.name}")
                if stderr_text.strip():
                    safe_emit(stderr_text.strip()[-400:])
                _safe_unlink(work_out)
                return False
        finally:
            if proc is not None:
                with proc_lock:
                    active_procs.discard(proc)

        if stop.is_set():
            cancelled = True
            _safe_unlink(work_out)
            return False

        if not _paths_conflict(work_out, final_out):
            if final_out.exists():
                final_out.unlink()
            shutil.move(str(work_out), str(final_out))
        if strip_suffix and (not suffix or suffix == "_bitrate"):
            final_out = _try_strip_autobitrate_suffix(final_out, src, safe_emit)
        if post_action == "move_to_backup":
            backup_root = output_root / "_original_backup"
            brel = src.relative_to(input_root) if src.is_relative_to(input_root) else Path(src.name)
            btarget = backup_root / brel
            btarget.parent.mkdir(parents=True, exist_ok=True)
            if btarget.exists():
                btarget = btarget.with_name(f"{btarget.stem}_dup{btarget.suffix}")
            shutil.move(str(src), str(btarget))
        elif post_action == "delete_original":
            _try_delete_source(src, final_out, safe_emit)
        safe_emit(f"Fertig: {final_out}")
        return True

    safe_emit(
        f"Encode: {total_jobs} Dateien, parallel={parallel_jobs}, "
        f"threads={ffmpeg_threads or 'auto'}, priority={'low' if low_priority else 'normal'}, "
        f"preset={x264_preset}"
    )

    workers = min(parallel_jobs, total_jobs)
    finished = 0
    try:
        with ThreadPoolExecutor(max_workers=workers) as pool:
            futures = {pool.submit(convert_one, row): row for row in jobs}
            pending = set(futures.keys())
            while pending:
                if stop.is_set():
                    cancelled = True
                    kill_all_ffmpeg()
                    for fut in pending:
                        fut.cancel()
                    break
                done_set, pending = wait(pending, timeout=0.25, return_when=FIRST_COMPLETED)
                if not done_set:
                    continue
                for future in done_set:
                    ok = False
                    try:
                        ok = bool(future.result())
                    except Exception as exc:
                        if not stop.is_set():
                            safe_emit(f"FEHLER: {exc}")
                    with done_lock:
                        if ok:
                            done += 1
                        finished += 1
                        current = finished
                    if progress_cb and total_jobs > 0:
                        progress_cb(current, total_jobs)
    finally:
        if stop.is_set() or cancelled:
            kill_all_ffmpeg()
            cancelled = True

    if cancelled:
        safe_emit("ABGEBROCHEN: Encoding gestoppt, unfertige Ausgaben entfernt.")
        return -1
    return done


def iter_rename_roots(config: Dict[str, Any]) -> List[Path]:
    roots: List[Path] = []
    seen: set[str] = set()
    for key in ("input_folder", "output_folder"):
        raw = str(config.get(key) or "").strip()
        if not raw:
            continue
        folder = Path(raw).expanduser()
        if not folder.is_dir():
            continue
        try:
            norm = str(folder.resolve()).lower()
        except Exception:
            norm = str(folder).lower()
        if norm in seen:
            continue
        seen.add(norm)
        roots.append(folder)
    return roots


def collect_rename_pairs(root: Path, recursive: bool, video_only: bool) -> Tuple[List[Tuple[str, str]], int, int]:
    pattern = "**/*" if recursive else "*"
    pairs: List[Tuple[str, str]] = []
    conflicts = 0
    scanned = 0
    for p in root.glob(pattern):
        if not p.is_file():
            continue
        if video_only and p.suffix.lower() not in VIDEO_EXTENSIONS:
            continue
        scanned += 1
        if not p.stem.endswith("_bitrate"):
            continue
        new_stem = p.stem[: -len("_bitrate")]
        if not new_stem:
            conflicts += 1
            continue
        target = p.with_name(f"{new_stem}{p.suffix}")
        if _paths_conflict(p, target):
            conflicts += 1
            continue
        pairs.append((str(p), str(target)))
    return pairs, conflicts, scanned


def collect_rename_pairs_all(config: Dict[str, Any]) -> Tuple[List[Tuple[str, str]], int, int]:
    recursive = bool(config.get("recursive", True))
    video_only = bool(config.get("rename_only_video", True))
    all_pairs: List[Tuple[str, str]] = []
    total_conflicts = 0
    total_scanned = 0
    seen_old: set[str] = set()
    for root in iter_rename_roots(config):
        pairs, conflicts, scanned = collect_rename_pairs(root, recursive, video_only)
        total_conflicts += conflicts
        total_scanned += scanned
        for old, new in pairs:
            key = str(Path(old).resolve()).lower()
            if key in seen_old:
                continue
            seen_old.add(key)
            all_pairs.append((old, new))
    return all_pairs, total_conflicts, total_scanned


def apply_renames(pairs: List[Tuple[str, str]]) -> Tuple[int, int]:
    ok = err = 0
    for old, new in pairs:
        old_p = Path(old)
        new_p = Path(new)
        try:
            if new_p.exists() and not _paths_conflict(old_p, new_p):
                new_p.unlink()
            old_p.rename(new_p)
            ok += 1
        except Exception:
            try:
                if new_p.exists() and not _paths_conflict(old_p, new_p):
                    new_p.unlink()
                shutil.move(old, new)
                ok += 1
            except Exception:
                err += 1
    return ok, err


def rows_to_dicts(rows: List[VideoInfo]) -> List[Dict[str, Any]]:
    return [asdict(r) for r in rows]
