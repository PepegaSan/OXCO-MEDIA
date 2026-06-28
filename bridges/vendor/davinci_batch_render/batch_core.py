"""Headless DaVinci batch render core (extracted from batch_render.py)."""
from __future__ import annotations

import time
from pathlib import Path
from typing import Any, Callable, Optional, Sequence

from batch_i18n import tr

VIDEO_SUFFIXES = {
    ".mp4",
    ".mov",
    ".mkv",
    ".avi",
    ".webm",
    ".m4v",
    ".wmv",
    ".mts",
    ".m2ts",
    ".flv",
    ".mpg",
    ".mpeg",
}


def is_video_file(path: Path) -> bool:
    return path.is_file() and path.suffix.lower() in VIDEO_SUFFIXES


def unique_davinci_output_basename(stem: str, out_dir: Path, safe: bool) -> str:
    base = stem
    if not safe:
        return base
    exts = (".mp4", ".mov", ".mxf", ".avi", ".mkv", ".m4v", ".mpg", ".mpeg", ".webm", ".wmv")

    def taken(name: str) -> bool:
        for ext in exts:
            try:
                if (out_dir / f"{name}{ext}").is_file():
                    return True
            except OSError:
                continue
        try:
            for candidate in out_dir.glob(f"{name}.*"):
                if candidate.is_file() and candidate.suffix.lower() in exts:
                    return True
        except OSError:
            pass
        return False

    if not taken(base):
        return base
    for n in range(2, 10000):
        candidate = f"{base}_{n}"
        if not taken(candidate):
            return candidate
    return f"{base}_{int(time.time_ns())}"


def _clip_resolution(clip: Any) -> tuple[int, int]:
    raw = clip.GetClipProperty("Resolution") or ""
    if isinstance(raw, str) and "x" in raw.lower():
        parts = raw.lower().split("x", 1)
        try:
            w, h = int(parts[0]), int(parts[1])
            if w > 0 and h > 0:
                return w, h
        except (TypeError, ValueError):
            pass
    return 1920, 1080


def _clip_fps(clip: Any) -> float:
    from davinci_api import snap_standard_fps

    raw = clip.GetClipProperty("FPS")
    try:
        fps = float(str(raw).strip())
        if fps > 0:
            return snap_standard_fps(fps)
    except (TypeError, ValueError):
        pass
    return 30.0


def render_full_video_in_resolve(
    *,
    resolve: Any,
    project: Any,
    media_pool: Any,
    video_path: Path,
    render_basename: str,
    out_dir: Path,
    preset: Optional[str],
    log_status: Callable[[str], None],
    lang: str,
) -> None:
    from davinci_api import (
        ResolveError,
        delete_all_timelines,
        format_timeline_framerate_for_resolve,
        open_deliver_page,
        override_clip_fps,
        read_timeline_fps_settings,
        reconcile_export_fps,
        render_with_preset,
        set_project_master_before_import,
        sync_active_timeline_fps,
        to_forward,
    )

    delete_all_timelines(project, media_pool)

    path_str = str(video_path)
    width, height = 1920, 1080
    analysis_fps = 30.0

    time.sleep(2.0)
    clips = media_pool.ImportMedia([to_forward(path_str)])
    if not clips:
        raise ResolveError("ImportMedia lieferte keine Clips.")
    clip = clips[0]
    time.sleep(0.25)

    width, height = _clip_resolution(clip)
    analysis_fps = _clip_fps(clip)
    timeline_rate = format_timeline_framerate_for_resolve(analysis_fps)

    log_status(
        tr(
            lang,
            "davinci_fps_status",
            fps=f"{analysis_fps:.3g}",
            rate=timeline_rate,
        )
    )

    set_project_master_before_import(project, timeline_rate, analysis_fps, width, height)
    override_clip_fps(clip, analysis_fps, timeline_rate)

    tl_name = f"BatchRender_{int(time.time())}"
    timeline = media_pool.CreateEmptyTimeline(tl_name)
    if timeline is None:
        raise ResolveError("Leere Timeline konnte nicht erstellt werden.")
    project.SetCurrentTimeline(timeline)
    try:
        timeline.SetStartTimecode("00:00:00:00")
    except Exception:
        pass
    sync_active_timeline_fps(project, timeline, timeline_rate, analysis_fps)

    proj_fps, tl_fps = read_timeline_fps_settings(project, timeline)
    export_fps, fps_mode = reconcile_export_fps(analysis_fps, proj_fps, tl_fps)
    if fps_mode == "locked_wrong":
        raise ResolveError(
            tr(
                lang,
                "davinci_fps_locked",
                read=tl_fps,
                proj=proj_fps,
                want=format_timeline_framerate_for_resolve(analysis_fps),
            )
        )
    if fps_mode == "use_timeline":
        log_status(
            tr(
                lang,
                "davinci_fps_use_timeline",
                timeline=f"{export_fps:.3g}",
                probe=f"{analysis_fps:.3g}",
            )
        )
        analysis_fps = export_fps
        timeline_rate = format_timeline_framerate_for_resolve(analysis_fps)

    items = media_pool.AppendToTimeline([{"mediaPoolItem": clip}])
    if not items:
        raise ResolveError("AppendToTimeline fehlgeschlagen — Clip konnte nicht zur Timeline hinzugefügt werden.")

    project.SetCurrentTimeline(timeline)
    sync_active_timeline_fps(project, timeline, timeline_rate, analysis_fps, skip_if_close=True)

    open_deliver_page(resolve)
    render_with_preset(
        project,
        output_dir=str(out_dir),
        output_name=render_basename,
        preset_name=preset,
        status_callback=log_status,
        frame_rate=analysis_fps,
        width=width,
        height=height,
    )


def run_batch_render(
    video_paths: Sequence[str],
    *,
    davinci_output_dir: str,
    davinci_preset: str,
    resolve_exe: str = "",
    resolve_modules: str = "",
    resolve_dll: str = "",
    safe_output: bool = True,
    lang: str = "de",
    log: Optional[Callable[[str], None]] = None,
    item_status: Optional[Callable[[str, str], None]] = None,
) -> dict[str, Any]:
    from davinci_api import (
        ResolveError,
        connect_resolve,
        restore_resolve_project,
        scripting_thread,
        set_resolve_path_overrides,
        try_create_export_project,
    )

    def _log(msg: str) -> None:
        if log:
            log(msg)

    def _item_status(path: Path, status: str) -> None:
        if item_status:
            item_status(str(path), status)

    paths = [Path(p).expanduser().resolve() for p in video_paths if p]
    paths = [p for p in paths if is_video_file(p)]
    if not paths:
        raise ValueError("Keine gültigen Videodateien in der Warteschlange.")

    set_resolve_path_overrides(
        resolve_exe=resolve_exe or None,
        modules_dir=resolve_modules or None,
        fusionscript_dll=resolve_dll or None,
    )

    out_dir_str = (davinci_output_dir or "").strip()
    out_dir = Path(out_dir_str).expanduser() if out_dir_str else Path.home() / "Videos"
    out_dir.mkdir(parents=True, exist_ok=True)
    preset = (davinci_preset or "").strip() or None

    results: list[dict[str, str]] = []
    ok_count = 0
    fail_count = 0
    total = len(paths)

    with scripting_thread():
        resolve, project, media_pool, _root = connect_resolve(status_callback=_log, auto_launch=True)
        project_manager = resolve.GetProjectManager()
        try:
            orig_project_name = project.GetName()
        except Exception:
            orig_project_name = None

        export_project, export_project_name = try_create_export_project(project_manager)
        switched_export_project = export_project is not None
        if switched_export_project:
            project = export_project
            media_pool = project.GetMediaPool()
            _log(
                tr(
                    lang,
                    "davinci_temp_project",
                    temp=export_project_name or "?",
                    orig=orig_project_name or "?",
                )
            )
        else:
            _log(tr(lang, "davinci_temp_project_failed"))

        for step, video_path in enumerate(paths, start=1):
            _log(tr(lang, "status_batch_working", cur=step, total=total, name=video_path.name))
            _item_status(video_path, "running")
            render_name = unique_davinci_output_basename(video_path.stem, out_dir, safe_output)
            try:
                render_full_video_in_resolve(
                    resolve=resolve,
                    project=project,
                    media_pool=media_pool,
                    video_path=video_path,
                    render_basename=render_name,
                    out_dir=out_dir,
                    preset=preset,
                    log_status=_log,
                    lang=lang,
                )
                results.append({"path": str(video_path), "status": "done", "output_name": render_name})
                ok_count += 1
                _item_status(video_path, "done")
                _log(tr(lang, "status_item_done", name=video_path.name))
            except ResolveError as exc:
                results.append({"path": str(video_path), "status": "failed", "error": str(exc)})
                fail_count += 1
                _item_status(video_path, "failed")
                _log(f"ERROR: {video_path.name}: {exc}")
            except Exception as exc:
                results.append({"path": str(video_path), "status": "failed", "error": str(exc)})
                fail_count += 1
                _item_status(video_path, "failed")
                _log(f"ERROR: {video_path.name}: {exc}")

        restore_resolve_project(project_manager, orig_project_name, switched_export_project)

    _log(tr(lang, "status_batch_done", ok=ok_count, fail=fail_count))
    return {"ok": ok_count, "fail": fail_count, "items": results, "output_dir": str(out_dir)}
