"""Vendored DaVinci-Export aus Cutter/cutter.py — Original-Repo unverändert."""
from __future__ import annotations

import time
from pathlib import Path

from davinci_api import (
    ResolveError,
    append_scenes_sequential,
    connect_resolve,
    delete_all_timelines,
    ffprobe_executable,
    format_timeline_framerate_for_resolve,
    open_deliver_page,
    override_clip_fps,
    probe_source_fps,
    probe_video_wh,
    read_timeline_fps_settings,
    reconcile_export_fps,
    render_with_preset,
    restore_resolve_project,
    scripting_thread,
    set_project_master_before_import,
    set_resolve_path_overrides,
    sync_active_timeline_fps,
    try_create_export_project,
    to_forward,
)


def unique_output_basename(stem: str, out_dir: Path) -> str:
    base = f"{stem}_cut"
    exts = (".mp4", ".mov", ".mxf", ".avi", ".mkv", ".m4v", ".mpg", ".mpeg", ".webm", ".wmv")

    def taken(name: str) -> bool:
        for ext in exts:
            try:
                if (out_dir / f"{name}{ext}").is_file():
                    return True
            except OSError:
                continue
        try:
            for path in out_dir.glob(f"{name}.*"):
                if path.is_file() and path.suffix.lower() in exts:
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
    return f"{base}_out"


def export_scenes_to_resolve(
    input_path: Path,
    scenes: list[tuple[float, float]],
    *,
    preset: str | None,
    output_dir: Path,
    player_fps: float = 25.0,
    resolve_exe: str | None = None,
    resolve_modules: str | None = None,
    resolve_dll: str | None = None,
    log=print,
) -> tuple[Path, str]:
    if not scenes:
        raise ValueError("Keine Szenen zum Export.")

    src = input_path.resolve()
    if not src.is_file():
        raise FileNotFoundError(f"Quelle nicht gefunden: {src}")

    output_dir.mkdir(parents=True, exist_ok=True)
    render_basename = unique_output_basename(src.stem, output_dir)
    preset_name = (preset or "").strip() or None

    set_resolve_path_overrides(
        resolve_exe=resolve_exe or None,
        modules_dir=resolve_modules or None,
        fusionscript_dll=resolve_dll or None,
    )

    with scripting_thread():
        resolve, project, media_pool, _root = connect_resolve(
            status_callback=log,
            auto_launch=True,
        )
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
            log(
                f"Temporäres Resolve-Projekt: {export_project_name or '?'} "
                f"(Original: {orig_project_name or '?'})"
            )
        else:
            log("Hinweis: Temporäres Projekt konnte nicht erstellt werden — nutze aktuelles Projekt.")

        delete_all_timelines(project, media_pool)

        try:
            video_path = str(src)
            if not ffprobe_executable():
                log("Warnung: ffprobe nicht gefunden — FPS-Erkennung kann ungenau sein.")

            analysis_fps = probe_source_fps(video_path, player_fps)
            timeline_rate = format_timeline_framerate_for_resolve(analysis_fps)
            width, height = probe_video_wh(video_path)
            if not width or not height:
                width, height = 1920, 1080

            log(f"FPS: {analysis_fps:.3g} → Timeline {timeline_rate}, {width}×{height}")

            set_project_master_before_import(project, timeline_rate, analysis_fps, width, height)

            time.sleep(2.0)
            clips = media_pool.ImportMedia([to_forward(video_path)])
            if not clips:
                raise ResolveError("ImportMedia lieferte keine Clips.")
            clip = clips[0]
            time.sleep(0.25)
            override_clip_fps(clip, analysis_fps, timeline_rate)

            tl_name = f"HailMary_{int(time.time())}"
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
                    f"Timeline-FPS gesperrt ({tl_fps}, Projekt {proj_fps}) — "
                    f"erwartet ~{format_timeline_framerate_for_resolve(analysis_fps)}."
                )
            if fps_mode == "use_timeline":
                log(f"Nutze Timeline-FPS {export_fps:.3g} (Quelle {analysis_fps:.3g})")
                analysis_fps = export_fps
                timeline_rate = format_timeline_framerate_for_resolve(analysis_fps)

            try:
                clip_frames = int(clip.GetClipProperty("Frames") or 0)
            except Exception:
                clip_frames = 0

            n = append_scenes_sequential(
                media_pool,
                timeline,
                clip,
                scenes,
                analysis_fps,
                clip_frames,
            )
            if n <= 0:
                raise ResolveError("Keine gültigen Szenen für die Timeline.")

            project.SetCurrentTimeline(timeline)
            sync_active_timeline_fps(
                project, timeline, timeline_rate, analysis_fps, skip_if_close=True
            )

            open_deliver_page(resolve)
            render_with_preset(
                project,
                output_dir=str(output_dir),
                output_name=render_basename,
                preset_name=preset_name,
                status_callback=log,
                frame_rate=analysis_fps,
                width=width,
                height=height,
            )
        finally:
            restore_resolve_project(project_manager, orig_project_name, switched_export_project)

    log(f"DaVinci-Export gestartet → {output_dir} / {render_basename}")
    return output_dir, render_basename
