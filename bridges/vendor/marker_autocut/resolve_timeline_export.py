"""
Stash-Marker → DaVinci Resolve: Timelines mit AppendToTimeline (Subclips).
Nutzt davinci_api.connect_resolve, scripting_thread, apply_project_timeline_settings.
"""

from __future__ import annotations

import os
import re
import time
from collections import defaultdict
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional, Sequence, Tuple

from davinci_api import (
    APPEND_SUBCLIP_SLEEP_AFTER_IMPORT_SEC,
    ResolveError,
    append_dict_for_subclip,
    apply_project_timeline_settings,
    cleanup_timelines,
    connect_resolve,
    render_with_preset,
    scripting_thread,
    to_forward,
)

from marker_math import (
    clip_max_frame_index,
    duration_seconds,
    fps_from_rows_for_path,
    parse_fps_text,
    resolve_media_path,
    rows_to_timeline_segments,
    safe_filename_stem,
    segment_to_inclusive_frames,
)


def _extra_bases_from_options(options: Dict[str, Any]) -> Optional[List[Path]]:
    raw = options.get("path_extra_bases")
    if raw is None:
        return None
    if isinstance(raw, (list, tuple)):
        parts = [str(x).strip() for x in raw if str(x).strip()]
    else:
        parts = [x.strip() for x in re.split(r"[;\n\r]+", str(raw)) if x.strip()]
    if not parts:
        return None
    out: List[Path] = []
    for p in parts:
        try:
            out.append(Path(p).expanduser())
        except Exception:
            continue
    return out or None


def _remap_from_options(
    options: Dict[str, Any],
) -> Tuple[Optional[str], Optional[str]]:
    """Pfadanfang wie Stash → gleicher Ordner auf diesem PC (NAS, Docker, UNC …)."""
    d = (options.get("path_docker_prefix") or "").strip()
    w = (options.get("path_windows_root") or "").strip()
    if d and w:
        return d, w
    return None, None


def _backup_prefix_from_options(options: Dict[str, Any]) -> Optional[str]:
    raw = (options.get("backup_path_prefix") or "").strip()
    return raw if raw else None


def _use_backup_from_options(options: Dict[str, Any]) -> bool:
    v = options.get("use_backup")
    if isinstance(v, bool):
        return v
    return str(v or "").strip().lower() in ("1", "true", "yes", "on")


def _log_path_issues(status_callback: LogFn, not_found: List[str]) -> None:
    if not not_found:
        return
    uniq = list(dict.fromkeys(not_found))
    sample = uniq[:5]
    more = len(uniq) - len(sample)
    tail = f" … (+{more} weitere)" if more > 0 else ""
    _log(
        status_callback,
        f"Hinweis: {len(not_found)} Marker mit nicht auffindbarem Pfad "
        f"(Stash-Datenbank vs. dieser PC). Beispiele: {sample}{tail}",
    )

LogFn = Optional[Callable[[str], None]]

TIMELINE_PREFIX = "StashMarkerCut_"
# Resolve wird bei sehr großen einzelnen AppendToTimeline-Listen oft instabil.
APPEND_TO_TIMELINE_BATCH = 48


def _log(cb: LogFn, msg: str) -> None:
    if cb:
        cb(msg)


def _parse_clip_fps_raw(clip: Any) -> Optional[str]:
    try:
        raw = clip.GetClipProperty("FPS")
        if raw is None or str(raw).strip() == "":
            return None
        return str(raw).strip()
    except Exception:
        return None


def _parse_clip_resolution(clip: Any) -> str:
    try:
        r = clip.GetClipProperty("Resolution")
        if r and str(r).strip():
            return str(r).strip()
    except Exception:
        pass
    return "1920x1080"


def _clip_resolution_dims(clip: Any) -> Tuple[int, int]:
    """Breite×Höhe aus Resolve-Clip; bei Fehler 1920×1080."""
    s = _parse_clip_resolution(clip)
    try:
        w_str, h_str = str(s).lower().replace(" ", "").split("x", 1)
        w, h = int(w_str), int(h_str)
        if w > 0 and h > 0:
            return w, h
    except (ValueError, AttributeError):
        pass
    return 1920, 1080


def _max_resolution_string_across_clips(clips: Sequence[Any]) -> str:
    """Maximale Breite und maximale Höhe über alle Clips (Compilation — schwächste Quelle zwingt nicht die Timeline-Kachelgröße)."""
    max_w, max_h = 0, 0
    for c in clips:
        w, h = _clip_resolution_dims(c)
        max_w = max(max_w, w)
        max_h = max(max_h, h)
    if max_w <= 0 or max_h <= 0:
        return "1920x1080"
    return f"{max_w}x{max_h}"


def _append_to_timeline_batched(
    media_pool: Any,
    project: Any,
    timeline: Any,
    clips_to_append: List[Dict[str, Any]],
) -> int:
    """Append in kleinen Batches — stabiler bei hunderten/tausenden Markern."""
    n = len(clips_to_append)
    if n == 0:
        return 0
    batches = 0
    for start in range(0, n, APPEND_TO_TIMELINE_BATCH):
        batch = clips_to_append[start : start + APPEND_TO_TIMELINE_BATCH]
        project.SetCurrentTimeline(timeline)
        try:
            media_pool.AppendToTimeline(batch)
        except Exception as e:
            raise ResolveError(
                f"AppendToTimeline fehlgeschlagen (Batch {batches + 1}, "
                f"Segmente {start + 1}–{start + len(batch)} von {n}): {e}"
            ) from e
        batches += 1
        if start + APPEND_TO_TIMELINE_BATCH < n:
            time.sleep(0.12)
    return batches


def import_clip(media_pool: Any, path: str) -> Any:
    paths = [to_forward(resolve_media_path(path))]
    clips = media_pool.ImportMedia(paths)
    if not clips:
        raise ResolveError(f"Import fehlgeschlagen: {path}")
    # Kurz warten — Resolve verarbeitet Metadaten nach Import oft verzögert.
    time.sleep(APPEND_SUBCLIP_SLEEP_AFTER_IMPORT_SEC)
    return clips[0]


def append_segments_on_timeline(
    media_pool: Any,
    project: Any,
    *,
    clip: Any,
    source_path: str,
    segments_sec: Sequence[Tuple[float, Optional[float]]],
    status_callback: LogFn,
    min_segment_seconds: float,
    fps_fallback: float,
) -> None:
    """Eine Quelle, eine Timeline — Segmente in der **übergebenen** Reihenfolge (Export-Warteschlange)."""
    raw_fps = _parse_clip_fps_raw(clip) or str(fps_fallback)
    res = _parse_clip_resolution(clip)
    w, h, applied = apply_project_timeline_settings(project, raw_fps, res)
    _log(
        status_callback,
        f"Projekt-Timeline: {w}×{h} @ {applied!r} (Vorgabe aus Quellclip).",
    )

    max_f = clip_max_frame_index(clip)
    try:
        fps_for_math = float(parse_fps_text(raw_fps) or fps_fallback)
    except (TypeError, ValueError):
        fps_for_math = fps_fallback
    if fps_for_math <= 0:
        fps_for_math = fps_fallback

    timeline_name = f"{TIMELINE_PREFIX}{safe_filename_stem(source_path, 'clip')}_{int(time.time())}"
    timeline = media_pool.CreateEmptyTimeline(timeline_name)
    if not timeline:
        raise ResolveError("Timeline konnte nicht erstellt werden.")
    project.SetCurrentTimeline(timeline)

    clips_to_append: List[Dict[str, Any]] = []
    for s0, s1_adj in segments_sec:
        s0f, s1f = segment_to_inclusive_frames(
            s0, s1_adj, fps_for_math, min_segment_seconds, max_f
        )
        d = append_dict_for_subclip(clip, s0f, s1f)
        if d:
            clips_to_append.append(d)

    if not clips_to_append:
        raise ResolveError(
            "Keine gültigen Frame-Bereiche für die Timeline (Dauer 0?). "
            "Mindestlänge Segment oder Marker-Zeiten prüfen."
        )
    batch_count = _append_to_timeline_batched(
        media_pool, project, timeline, clips_to_append
    )
    _log(
        status_callback,
        f"Timeline „{timeline_name}“: {len(clips_to_append)} Marker-Schnitt(e) angehängt "
        f"({batch_count} Batch(es) à max. {APPEND_TO_TIMELINE_BATCH}).",
    )


def build_timelines_per_file(
    rows: List[Dict[str, str]],
    *,
    min_segment_seconds: float,
    inclusive_end: bool,
    default_fps: float,
    status_callback: LogFn,
    do_render: bool,
    output_dir: str,
    output_name_prefix: str,
    render_preset: Optional[str],
    extra_bases: Optional[Sequence[Path]] = None,
    docker_path_prefix: Optional[str] = None,
    windows_path_root: Optional[str] = None,
    backup_path_prefix: Optional[str] = None,
    use_backup: bool = False,
) -> None:
    """Eine Resolve-Timeline pro Quelldatei (nur zutreffende Marker).

    Hinweis: Unterschiedliche Bildraten pro Datei können an Resolve-Grenzen
    stoßen (Projekt-Timeline-FPS). Dann ggf. getrennt exportieren oder eine
    einheitliche Quell-FPS verwenden.
    """
    segs, not_found = rows_to_timeline_segments(
        rows,
        min_segment_seconds=min_segment_seconds,
        inclusive_end=inclusive_end,
        default_fps=default_fps,
        path_extra_bases=extra_bases,
        docker_path_prefix=docker_path_prefix,
        windows_path_root=windows_path_root,
        backup_path_prefix=backup_path_prefix,
        use_backup=use_backup,
    )
    _log_path_issues(status_callback, not_found)
    if not segs:
        raise ResolveError(
            "Keine Mediendatei gefunden. Wenn Stash andere Pfade zeigt als dieser PC "
            "(NAS/Linux/Docker): unter „NAS / … → Windows“ Präfix und Windows-Ordner eintragen "
            "oder Backup-Pfad mit „Backup statt NAS“ aktivieren."
        )

    by_file: Dict[str, List[Tuple[float, Optional[float]]]] = defaultdict(list)
    for fp, s0, s1 in segs:
        by_file[fp].append((s0, s1))

    path_order: List[str] = []
    seen_paths: set[str] = set()
    for fp, *_rest in segs:
        if fp not in seen_paths:
            seen_paths.add(fp)
            path_order.append(fp)

    _, project, media_pool, _root = connect_resolve(
        status_callback=status_callback,
        auto_launch=True,
        create_scratch_project_name="StashMarkerAutocut",
    )

    cleanup_timelines(project, media_pool, name_prefix=TIMELINE_PREFIX)

    for path in path_order:
        list_sec = sorted(by_file[path], key=lambda seg: seg[0])
        clip = import_clip(media_pool, path)
        fps_fb = fps_from_rows_for_path(
            rows,
            path,
            default_fps,
            extra_bases,
            docker_path_prefix=docker_path_prefix,
            windows_path_root=windows_path_root,
            backup_path_prefix=backup_path_prefix,
            use_backup=use_backup,
        )
        append_segments_on_timeline(
            media_pool,
            project,
            clip=clip,
            source_path=path,
            segments_sec=list_sec,
            status_callback=status_callback,
            min_segment_seconds=min_segment_seconds,
            fps_fallback=fps_fb,
        )
        if do_render:
            stem = safe_filename_stem(path, "export")
            out_dir = output_dir.strip() or os.path.expanduser("~")
            os.makedirs(out_dir, exist_ok=True)
            render_with_preset(
                project,
                output_dir=out_dir,
                output_name=f"{output_name_prefix}_{stem}_{int(time.time())}",
                preset_name=render_preset,
                status_callback=status_callback,
            )


def build_timeline_compilation(
    rows: List[Dict[str, str]],
    *,
    min_segment_seconds: float,
    inclusive_end: bool,
    default_fps: float,
    status_callback: LogFn,
    do_render: bool,
    output_dir: str,
    output_name_prefix: str,
    render_preset: Optional[str],
    extra_bases: Optional[Sequence[Path]] = None,
    docker_path_prefix: Optional[str] = None,
    windows_path_root: Optional[str] = None,
    backup_path_prefix: Optional[str] = None,
    use_backup: bool = False,
) -> None:
    """Eine Timeline: alle Segmente nacheinander (verschiedene Quellen möglich)."""
    segs, not_found = rows_to_timeline_segments(
        rows,
        min_segment_seconds=min_segment_seconds,
        inclusive_end=inclusive_end,
        default_fps=default_fps,
        path_extra_bases=extra_bases,
        docker_path_prefix=docker_path_prefix,
        windows_path_root=windows_path_root,
        backup_path_prefix=backup_path_prefix,
        use_backup=use_backup,
    )
    _log_path_issues(status_callback, not_found)
    if not segs:
        raise ResolveError(
            "Keine Mediendatei gefunden. NAS-/Server-Pfad → Windows-Mapping setzen "
            "oder „Medien-Stammordner“ ergänzen."
        )

    unique_paths = sorted({p for p, *_ in segs})

    resolve, project, media_pool, _root = connect_resolve(
        status_callback=status_callback,
        auto_launch=True,
        create_scratch_project_name="StashMarkerAutocut",
    )
    cleanup_timelines(project, media_pool, name_prefix=TIMELINE_PREFIX)

    clips_by_path: Dict[str, Any] = {}
    fps_raw_by_path: Dict[str, str] = {}
    max_f_by_path: Dict[str, Optional[int]] = {}

    for p in unique_paths:
        c = import_clip(media_pool, p)
        clips_by_path[p] = c
        fps_raw_by_path[p] = _parse_clip_fps_raw(c) or str(default_fps)
        max_f_by_path[p] = clip_max_frame_index(c)

    first_path = segs[0][0]
    raw_fps0 = fps_raw_by_path.get(first_path) or str(default_fps)
    res_max = _max_resolution_string_across_clips([clips_by_path[p] for p in unique_paths])
    w, h, applied = apply_project_timeline_settings(project, raw_fps0, res_max)
    _log(
        status_callback,
        f"Projekt-Timeline: {w}×{h} @ {applied!r} "
        f"(max. Breite/Höhe über {len(unique_paths)} Videodatei(en); FPS = erstes Segment).",
    )

    timeline_name = f"{TIMELINE_PREFIX}Compilation_{int(time.time())}"
    timeline = media_pool.CreateEmptyTimeline(timeline_name)
    if not timeline:
        raise ResolveError("Timeline konnte nicht erstellt werden.")
    project.SetCurrentTimeline(timeline)
    # Nach vielen ImportMedia kurz stabilisieren (Compilation lädt N Dateien).
    time.sleep(0.25)

    # Nur mediaPoolItem + start/end (end exklusiv),
    # **ohne** recordFrame — mehrere Quellen in **einem** AppendToTimeline-Aufruf hintereinander.
    # recordFrame + Subclips ist in Resolve oft unzuverlässig (Medienpool ok, Timeline leer).
    clips_to_append: List[Dict[str, Any]] = []
    for fp, s0, s1_adj in segs:
        clip = clips_by_path.get(fp)
        if clip is None:
            continue
        raw_fps = fps_raw_by_path.get(fp) or str(default_fps)
        try:
            fps_math = float(parse_fps_text(raw_fps) or default_fps)
        except (TypeError, ValueError):
            fps_math = default_fps
        if fps_math <= 0:
            fps_math = default_fps
        max_f = max_f_by_path.get(fp)

        s0f, s1f = segment_to_inclusive_frames(
            s0,
            s1_adj,
            fps_math,
            min_segment_seconds,
            max_f,
        )
        d = append_dict_for_subclip(clip, s0f, s1f)
        if d:
            clips_to_append.append(d)

    if not clips_to_append:
        raise ResolveError(
            "Compilation: keine gültigen Segmente für die Timeline (Frame-Bereiche leer?)."
        )

    n_seg = len(clips_to_append)
    n_files = len(unique_paths)
    batch_count = _append_to_timeline_batched(
        media_pool, project, timeline, clips_to_append
    )
    _log(
        status_callback,
        f"Timeline „{timeline_name}“: {n_seg} Marker-Schnitt(e) aus {n_files} Videodatei(en) "
        f"({batch_count} Append-Batch(es) à max. {APPEND_TO_TIMELINE_BATCH}, ohne recordFrame).",
    )

    if do_render:
        out_dir = output_dir.strip() or os.path.expanduser("~")
        os.makedirs(out_dir, exist_ok=True)
        render_with_preset(
            project,
            output_dir=out_dir,
            output_name=f"{output_name_prefix}_compilation_{int(time.time())}",
            preset_name=render_preset,
            status_callback=status_callback,
        )


def run_export_in_scripting_thread(
    mode: str,
    rows: List[Dict[str, str]],
    options: Dict[str, Any],
    log: LogFn,
) -> None:
    """mode: 'per_file' | 'compilation'"""
    min_seg = float(options.get("min_segment_seconds", 1.0))
    inclusive = bool(options.get("inclusive_end", True))
    default_fps = float(options.get("default_fps", 25.0))
    do_render = bool(options.get("do_render", False))
    output_dir = str(options.get("output_dir", ""))
    name_prefix = str(options.get("output_name_prefix", "StashMarker"))
    preset = options.get("render_preset") or None
    extra_bases = _extra_bases_from_options(options)
    d_prefix, w_root = _remap_from_options(options)
    backup_prefix = _backup_prefix_from_options(options)
    use_backup = _use_backup_from_options(options)

    with scripting_thread():
        if mode == "per_file":
            build_timelines_per_file(
                rows,
                min_segment_seconds=min_seg,
                inclusive_end=inclusive,
                default_fps=default_fps,
                status_callback=log,
                do_render=do_render,
                output_dir=output_dir,
                output_name_prefix=name_prefix,
                render_preset=preset,
                extra_bases=extra_bases,
                docker_path_prefix=d_prefix,
                windows_path_root=w_root,
                backup_path_prefix=backup_prefix,
                use_backup=use_backup,
            )
        elif mode == "compilation":
            build_timeline_compilation(
                rows,
                min_segment_seconds=min_seg,
                inclusive_end=inclusive,
                default_fps=default_fps,
                status_callback=log,
                do_render=do_render,
                output_dir=output_dir,
                output_name_prefix=name_prefix,
                render_preset=preset,
                extra_bases=extra_bases,
                docker_path_prefix=d_prefix,
                windows_path_root=w_root,
                backup_path_prefix=backup_prefix,
                use_backup=use_backup,
            )
        else:
            raise ValueError(f"Unbekannter Modus: {mode}")
