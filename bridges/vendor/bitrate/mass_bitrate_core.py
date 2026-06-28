"""Kernlogik aus Videobitratechanger/mass_bitrate_gui.py (Kopie, Original unveraendert)."""

from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional, Tuple

VIDEO_EXTENSIONS = {
    ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".ts", ".flv",
}

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


def run_ffprobe(path: Path) -> Tuple[Optional[int], Optional[int], Optional[int]]:
    cmd = [
        "ffprobe", "-v", "error", "-print_format", "json",
        "-show_entries", "stream=width,height,bit_rate", "-select_streams", "v:0",
        "-show_entries", "format=bit_rate", str(path),
    ]
    completed = subprocess.run(
        cmd, capture_output=True, text=True, encoding="utf-8", errors="replace",
        check=False, creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
    )
    if completed.returncode != 0:
        return None, None, None
    try:
        payload = json.loads(completed.stdout or "{}")
    except json.JSONDecodeError:
        return None, None, None
    streams = payload.get("streams") or []
    video_stream = streams[0] if streams else None
    if not video_stream:
        return None, None, None
    width = int(video_stream["width"]) if video_stream.get("width") is not None else None
    height = int(video_stream["height"]) if video_stream.get("height") is not None else None
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
    return width, height, kbps


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


def analyze_file(file_path: Path, rules: Dict[int, int], only_lower: bool) -> VideoInfo:
    width, height, source_kbps = run_ffprobe(file_path)
    if not width or not height:
        return VideoInfo(
            path=str(file_path), width=0, height=0,
            source_size_bytes=file_path.stat().st_size if file_path.exists() else None,
            source_kbps=None, target_rule_kbps=None, effective_target_kbps=None,
            estimated_output_bytes=None, estimated_saved_bytes=None, estimated_saved_pct=None,
            action="skip", reason="Aufloesung unbekannt",
        )
    source_size = file_path.stat().st_size if file_path.exists() else None
    rule = pick_rule_for_short_side(width, height, rules)
    if source_kbps is None:
        return VideoInfo(
            path=str(file_path), width=width, height=height, source_size_bytes=source_size,
            source_kbps=None, target_rule_kbps=rule, effective_target_kbps=None,
            estimated_output_bytes=None, estimated_saved_bytes=None, estimated_saved_pct=None,
            action="skip", reason="Bitrate unbekannt",
        )
    effective_target = min(source_kbps, rule)
    est_out = est_save = est_save_pct = None
    if source_size is not None:
        est_out, est_save, est_save_pct = estimate_sizes(source_size, source_kbps, effective_target)
    if only_lower and effective_target >= source_kbps:
        action, reason = "skip", "Keine Einsparung"
    else:
        action, reason = "convert", "Reduzieren"
    return VideoInfo(
        path=str(file_path), width=width, height=height, source_size_bytes=source_size,
        source_kbps=source_kbps, target_rule_kbps=rule, effective_target_kbps=effective_target,
        estimated_output_bytes=est_out, estimated_saved_bytes=est_save, estimated_saved_pct=est_save_pct,
        action=action, reason=reason,
    )


def scan_folder(
    input_folder: str,
    recursive: bool,
    only_lower: bool,
    rule_values: Dict[str, str],
    progress_cb: Optional[Callable[[int, int], None]] = None,
) -> List[VideoInfo]:
    in_path = Path(input_folder)
    rules = parse_rules(rule_values)
    files = iter_video_files(in_path, recursive)
    total = len(files)
    if total == 0:
        return []
    workers = min(8, max(2, (os.cpu_count() or 4)))
    rows_map: Dict[Path, VideoInfo] = {}
    processed = 0
    with ThreadPoolExecutor(max_workers=workers) as pool:
        future_map = {pool.submit(analyze_file, fp, rules, only_lower): fp for fp in files}
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
                    action="skip", reason="Scan-Fehler",
                )
            processed += 1
            if progress_cb and (processed == total or processed % max(1, total // 40) == 0):
                progress_cb(processed, total)
    return [rows_map[p] for p in files]


def build_ffmpeg_cmd(src: Path, dst: Path, target_kbps: int, codec: str, audio_mode: str) -> List[str]:
    cmd = [
        "ffmpeg", "-y", "-hide_banner", "-loglevel", "warning", "-i", str(src),
        "-c:v", codec, "-b:v", f"{target_kbps}k", "-maxrate", f"{target_kbps}k",
        "-bufsize", f"{target_kbps * 2}k",
    ]
    if codec in {"h264_nvenc", "hevc_nvenc"}:
        cmd.extend(["-rc:v", "vbr", "-cq:v", "23", "-preset", "p5",
                    "-profile:v", "high" if codec == "h264_nvenc" else "main"])
    if audio_mode == "aac_128k":
        cmd.extend(["-c:a", "aac", "-b:a", "128k"])
    else:
        cmd.extend(["-c:a", "copy"])
    if dst.suffix.lower() in {".mp4", ".m4v", ".mov"}:
        cmd.extend(["-movflags", "+faststart"])
    cmd.append(str(dst))
    return cmd


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
    w, h, _ = run_ffprobe(path)
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


def convert_rows(
    config: Dict[str, Any],
    rows: List[Dict[str, Any]],
    emit_fn,
    progress_cb: Optional[Callable[[int, int], None]] = None,
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

    jobs = [r for r in rows if r.get("action") == "convert" and r.get("effective_target_kbps")]
    total_jobs = len(jobs)
    done = 0
    for row in jobs:
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

        kbps = int(row["effective_target_kbps"])
        emit_fn(f"Konvertiere: {src.name} -> {kbps} kbps")
        cmd = build_ffmpeg_cmd(src, work_out, kbps, codec, audio_mode)
        completed = subprocess.run(
            cmd, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, text=True,
            encoding="utf-8", errors="replace", check=False,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
        )
        if completed.returncode != 0 or not is_valid_output(work_out):
            emit_fn(f"FEHLER: {src.name}")
            continue
        if not _paths_conflict(work_out, final_out):
            if final_out.exists():
                final_out.unlink()
            shutil.move(str(work_out), str(final_out))
        if strip_suffix and (not suffix or suffix == "_bitrate"):
            final_out = _try_strip_autobitrate_suffix(final_out, src, emit_fn)
        if post_action == "move_to_backup":
            backup_root = output_root / "_original_backup"
            brel = src.relative_to(input_root) if src.is_relative_to(input_root) else Path(src.name)
            btarget = backup_root / brel
            btarget.parent.mkdir(parents=True, exist_ok=True)
            if btarget.exists():
                btarget = btarget.with_name(f"{btarget.stem}_dup{btarget.suffix}")
            shutil.move(str(src), str(btarget))
        elif post_action == "delete_original":
            _try_delete_source(src, final_out, emit_fn)
        done += 1
        emit_fn(f"Fertig: {final_out}")
        if progress_cb and total_jobs > 0:
            progress_cb(done, total_jobs)
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
