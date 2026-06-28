"""DaVinci Resolve: Clips importieren, Timeline bauen, rendern (Clip Joiner)."""

from __future__ import annotations

import configparser
import datetime
import os
import random
import shutil
import sys
import time
from pathlib import Path
from typing import Any, Callable, List, Optional, Sequence

from media_probe import (
    pick_timeline_fps_from_clips,
    probe_format_duration,
    probe_media,
    summed_source_duration,
    total_duration_sec,
)
from utf8_io import (
    canonical_media_path,
    ensure_utf8_stdio,
    format_wh,
    to_resolve_import_path,
)

LogFn = Optional[Callable[[str], None]]


class ResolvePipelineError(RuntimeError):
    pass


def default_davinci_api_path() -> str:
    if sys.platform == "win32":
        pd = os.environ.get("ProgramData", r"C:\ProgramData")
        return os.path.join(
            pd,
            "Blackmagic Design",
            "DaVinci Resolve",
            "Support",
            "Developer",
            "Scripting",
            "Modules",
        )
    return ""


def resolve_davinci_api_path(configured: str = "") -> str:
    """Konfiguriert → Oxco settings.ini → Standard."""
    cfg = (configured or "").strip()
    if cfg:
        return cfg
    oxco_ini = Path.home() / "Projects" / "Oxco" / "settings.ini"
    if oxco_ini.is_file():
        cp = configparser.ConfigParser()
        cp.read(oxco_ini, encoding="utf-8")
        if cp.has_option("PATHS", "davinci_api_path"):
            v = cp.get("PATHS", "davinci_api_path").strip()
            if v:
                return v.replace("\\", "/")
    return default_davinci_api_path()


def _log(log: LogFn, msg: str) -> None:
    if log:
        log(msg)
    else:
        print(msg, flush=True)


def normalize_media_path(path: str) -> str:
    try:
        return canonical_media_path(path)
    except FileNotFoundError as ex:
        raise ResolvePipelineError(f"Datei nicht gefunden: {path}") from ex


def to_resolve_path(path: str) -> str:
    try:
        return to_resolve_import_path(path)
    except FileNotFoundError as ex:
        raise ResolvePipelineError(f"Datei nicht gefunden: {path}") from ex


def format_timeline_framerate_for_resolve(fps: float) -> str:
    try:
        fps = float(fps)
    except (TypeError, ValueError):
        return "30"
    if fps <= 0:
        return "30"
    common = [
        (23.976023976023978, "23.976"),
        (24.0, "24"),
        (25.0, "25"),
        (29.97002997002997, "29.97"),
        (30.0, "30"),
        (48.0, "48"),
        (50.0, "50"),
        (59.94005994005994, "59.94"),
        (60.0, "60"),
    ]
    for val, label in common:
        if abs(fps - val) < 0.04:
            return label
    if abs(fps - round(fps)) < 0.001:
        return str(int(round(fps)))
    out = f"{fps:.6f}".rstrip("0").rstrip(".")
    return out or "30"


def _timeline_fps_candidates(timeline_rate: str, fps_float: float) -> List[str]:
    out: List[str] = []
    seen: set[str] = set()

    def add(s: str) -> None:
        if s and s not in seen:
            seen.add(s)
            out.append(str(s))

    add(timeline_rate)
    add(format_timeline_framerate_for_resolve(fps_float))
    if fps_float > 0:
        raw = f"{fps_float:.6f}".rstrip("0").rstrip(".")
        add(raw)
        if abs(fps_float - round(fps_float)) < 0.06:
            ri = int(round(fps_float))
            add(str(ri))
            add(f"{ri}.0")
    return out


def _set_project_master_before_import(
    project: Any,
    timeline_rate: str,
    analysis_fps: float,
    width: int,
    height: int,
    log: LogFn = None,
) -> None:
    for cand in _timeline_fps_candidates(timeline_rate, analysis_fps):
        try:
            if project.SetSetting("timelineFrameRate", cand) is not False:
                break
        except Exception:
            continue
    try:
        project.SetSetting("timelineResolutionWidth", str(width))
        project.SetSetting("timelineResolutionHeight", str(height))
    except Exception:
        pass
    time.sleep(0.3)
    _log(log, f"Projekt-Master: {format_wh(width, height)} @ {timeline_rate}")


def _delete_all_timelines(project: Any, media_pool: Any) -> None:
    try:
        n = int(project.GetTimelineCount())
    except Exception:
        return
    if n <= 0:
        return
    timelines = []
    for idx in range(1, n + 1):
        try:
            tl = project.GetTimelineByIndex(idx)
            if tl:
                timelines.append(tl)
        except Exception:
            continue
    if timelines:
        try:
            media_pool.DeleteTimelines(timelines)
        except Exception:
            pass


def _create_export_project(project_manager: Any) -> tuple[Any, Optional[str]]:
    for _ in range(12):
        name = (
            f"ClipJoin_Export_{datetime.datetime.now().strftime('%Y%m%d_%H%M%S')}_"
            f"{random.randint(100000, 999999)}"
        )
        try:
            p = project_manager.CreateProject(name)
        except Exception:
            p = None
        if p:
            return p, name
    return None, None


def _restore_project(project_manager: Any, orig_name: Optional[str], switched: bool, log: LogFn = None) -> None:
    if not switched or not orig_name:
        return
    try:
        project_manager.LoadProject(orig_name)
        _log(log, f"Resolve-Projekt wiederhergestellt: {orig_name}")
    except Exception as ex:
        _log(log, f"Projekt-Wiederherstellung fehlgeschlagen: {ex}")


def _wait_for_render_idle(project: Any, timeout_s: float, log: LogFn = None) -> bool:
    rendering_seen = False
    start = time.time()
    no_start_deadline = start + 120.0
    while True:
        try:
            busy = project.IsRenderingInProgress()
        except Exception as ex:
            _log(log, f"Render-Abfrage unterbrochen: {ex}")
            return False
        now = time.time()
        if busy:
            rendering_seen = True
        if not busy and rendering_seen:
            time.sleep(2.0)
            try:
                if project.IsRenderingInProgress():
                    continue
            except Exception:
                pass
            return True
        if not busy and not rendering_seen:
            try:
                jobs = project.GetRenderJobList() or []
                if jobs:
                    job_id = jobs[-1].get("JobId")
                    if job_id:
                        st = (project.GetRenderJobStatus(job_id) or {}).get("JobStatus", "")
                        if st in ("Complete", "Abgeschlossen"):
                            time.sleep(2.0)
                            return True
                        s = str(st).lower()
                        if "fail" in s or "cancel" in s or "abort" in s:
                            _log(log, f"Render fehlgeschlagen: {st}")
                            return False
            except Exception:
                pass
            if now > no_start_deadline:
                _log(log, "Render startete nicht innerhalb von 120s.")
                return False
        if timeout_s > 0 and (now - start) > timeout_s:
            try:
                project.StopRendering()
            except Exception:
                pass
            _log(log, f"Render-Timeout ({timeout_s:.0f}s).")
            return False
        time.sleep(0.4 if not rendering_seen else 5.0)


def _load_dvr_script(davinci_api_path: str) -> Any:
    for key in ("RESOLVE_SCRIPT_API", "RESOLVE_SCRIPT_LIB"):
        os.environ.pop(key, None)

    api_path = resolve_davinci_api_path(davinci_api_path)
    if not api_path or not os.path.isdir(api_path):
        raise ResolvePipelineError(f"DaVinci API-Ordner nicht gefunden: {api_path!r}")
    if api_path not in sys.path:
        sys.path.append(api_path)
    try:
        import DaVinciResolveScript as dvr_script  # type: ignore
    except ImportError as ex:
        raise ResolvePipelineError(f"DaVinciResolveScript Import fehlgeschlagen: {ex}") from ex
    return dvr_script


def connect_resolve(
    davinci_api_path: str,
    *,
    log: LogFn = None,
    retry_attempts: int = 60,
    retry_delay: float = 3.0,
) -> tuple[Any, Any, Any]:
    dvr_script = _load_dvr_script(davinci_api_path)

    resolve = None
    for attempt in range(1, max(1, retry_attempts) + 1):
        resolve = dvr_script.scriptapp("Resolve")
        if resolve:
            break
        if attempt == 1:
            _log(log, "Warte auf Resolve scriptapp (Studio, External Scripting = Local)…")
        if attempt < retry_attempts:
            time.sleep(retry_delay)
    if not resolve:
        raise ResolvePipelineError("Resolve scriptapp nicht erreichbar.")

    pm = resolve.GetProjectManager()
    project = None
    for attempt in range(1, 41):
        project = pm.GetCurrentProject()
        if project:
            break
        if attempt < 40:
            time.sleep(3.0)
    if not project:
        raise ResolvePipelineError("Kein Resolve-Projekt geöffnet.")

    media_pool = project.GetMediaPool()
    if not media_pool:
        raise ResolvePipelineError("Media Pool nicht verfügbar.")
    return resolve, project, media_pool


def import_clip(
    media_pool: Any,
    path: str,
    *,
    log: LogFn = None,
    retries: int = 5,
) -> Any:
    abs_path = to_resolve_path(path)
    size = os.path.getsize(abs_path.replace("/", os.sep))
    _log(log, f"ImportMedia: {os.path.basename(abs_path)} ({size // 1024} KiB)")

    last: Any = None
    for attempt in range(1, retries + 1):
        try:
            last = media_pool.ImportMedia([abs_path])
        except Exception as ex:
            last = ex
            _log(log, f"  Versuch {attempt}/{retries}: {ex!r}")
        if isinstance(last, list) and last and last[0]:
            time.sleep(0.35)
            return last[0]
        if attempt < retries:
            time.sleep(0.6 * attempt)

    raise ResolvePipelineError(f"ImportMedia fehlgeschlagen: {abs_path}")


def format_clip_fps_property(fps: float) -> str:
    try:
        f = float(fps)
    except (TypeError, ValueError):
        return "30.0"
    if f <= 0:
        return "30.0"
    if abs(f - round(f)) < 0.02:
        return f"{float(int(round(f))):.1f}"
    return format_timeline_framerate_for_resolve(f)


APPEND_TO_TIMELINE_BATCH = 48
IMPORT_SETTLE_SEC = 0.35


def _parse_fps_setting(value: str) -> Optional[float]:
    try:
        f = float(str(value).strip().replace(",", "."))
    except (TypeError, ValueError):
        return None
    return f if f > 0 else None


def _read_timeline_fps_settings(project: Any, timeline: Any) -> tuple[str, str]:
    try:
        gp = str(project.GetSetting("timelineFrameRate") or "").strip() or "?"
    except Exception:
        gp = "?"
    try:
        gt = str(timeline.GetSetting("timelineFrameRate") or "").strip() or "?"
    except Exception:
        gt = "?"
    return gp, gt


def _reconcile_export_fps(
    probe_fps: float,
    project_setting: str,
    timeline_setting: str,
) -> tuple[float, str]:
    """Wie Cutter: Resolve-Timeline-FPS vs ffprobe abstimmen."""
    from media_probe import snap_standard_fps

    probe = float(probe_fps)
    tl = _parse_fps_setting(timeline_setting)
    proj = _parse_fps_setting(project_setting)
    if tl is None:
        return probe, "ok"
    if proj is not None and abs(tl - proj) > 0.05:
        return probe, "locked_wrong"
    if abs(tl - probe) <= 0.06:
        return probe, "ok"
    if abs(tl - probe) >= 1.0:
        return probe, "locked_wrong"
    if proj is not None and abs(tl - proj) <= 0.02:
        return snap_standard_fps(tl), "use_timeline"
    return probe, "ok"


def _sync_active_timeline_fps(
    project: Any,
    timeline: Any,
    timeline_rate: str,
    analysis_fps: float,
    *,
    skip_if_close: bool = False,
) -> None:
    if skip_if_close:
        try:
            gt = timeline.GetSetting("timelineFrameRate")
            g = float(str(gt).replace(",", "."))
            if abs(g - float(analysis_fps)) < 0.06:
                return
        except Exception:
            pass
    for cand in _timeline_fps_candidates(timeline_rate, analysis_fps):
        try:
            if timeline.SetSetting("timelineFrameRate", cand) is not False:
                break
        except Exception:
            continue
    _ = project


def _apply_clip_fps(pool_item: Any, timeline_rate: str, fps: float, log: LogFn = None) -> None:
    """Wie Cutter override_clip_fps — mehr Kandidaten für SetClipProperty."""
    clip_pv = format_clip_fps_property(fps)
    raw = f"{float(fps):.6f}".rstrip("0").rstrip(".")
    for val in (timeline_rate, clip_pv, raw, f"{float(fps):.3f}"):
        if not val:
            continue
        try:
            if pool_item.SetClipProperty("FPS", val) is not False:
                _log(log, f"Clip-FPS gesetzt: {val}")
                return
        except Exception:
            continue


def _clip_end_frame_exclusive(
    path: str,
    pool_item: Any,
    clip_fps: float,
    log: LogFn = None,
) -> int:
    """Halb-offenes endFrame in Quell-Frames (native Clip-FPS, nicht Timeline-FPS)."""
    probe_frames = 0
    dur = probe_format_duration(path)
    if dur and dur > 0 and clip_fps > 0:
        probe_frames = max(1, int(round(dur * clip_fps)))

    resolve_frames = 0
    try:
        resolve_frames = int(float(pool_item.GetClipProperty("Frames") or 0))
    except (TypeError, ValueError):
        resolve_frames = 0

    if probe_frames > 0 and resolve_frames > 0:
        end = min(probe_frames, resolve_frames)
        if abs(probe_frames - resolve_frames) > 2 and log:
            _log(
                log,
                f"  {os.path.basename(path)}: Frames probe={probe_frames} resolve={resolve_frames} → {end}",
            )
        return max(1, end)
    if resolve_frames > 0:
        return max(1, resolve_frames)
    if probe_frames > 0:
        return probe_frames
    return 1


def _clip_timeline_frames(source_frames: int, clip_fps: float, timeline_fps: float) -> int:
    """Quell-Frames → Timeline-Frames (60fps-Clip auf 30fps-Timeline = halbe Frame-Anzahl)."""
    if source_frames <= 0 or clip_fps <= 0 or timeline_fps <= 0:
        return max(1, source_frames)
    if abs(clip_fps - timeline_fps) < 0.06:
        return max(1, source_frames)
    return max(1, int(round(source_frames * timeline_fps / clip_fps)))


def _append_dict_full_clip(pool_item: Any, end_frame_exclusive: int) -> dict[str, Any]:
    end = max(1, int(end_frame_exclusive))
    return {
        "mediaPoolItem": pool_item,
        "startFrame": 0,
        "endFrame": end,
    }


def _append_ok(res: Any) -> bool:
    return isinstance(res, list) and bool(res) and any(x is not None for x in res)


def append_clips_on_timeline(
    project: Any,
    media_pool: Any,
    clip_specs: Sequence[tuple[Any, int, float]],
    *,
    timeline_fps: float,
    timeline_name: str,
    log: LogFn = None,
) -> Any:
    """
    Cutter/Marker-autocut: einzelne Clips, ohne recordFrame, batched AppendToTimeline.
    clip_specs: (pool_item, end_frame_exclusive, clip_native_fps)
    """
    timeline = media_pool.CreateEmptyTimeline(timeline_name)
    if not timeline:
        raise ResolvePipelineError("CreateEmptyTimeline fehlgeschlagen.")
    project.SetCurrentTimeline(timeline)
    try:
        timeline.SetStartTimecode("00:00:00:00")
    except Exception:
        pass

    to_append: list[dict[str, Any]] = []
    timeline_frames = 0
    for pool_item, end_f, clip_fps in clip_specs:
        to_append.append(_append_dict_full_clip(pool_item, end_f))
        timeline_frames += _clip_timeline_frames(end_f, clip_fps, timeline_fps)

    if not to_append:
        raise ResolvePipelineError("Keine Clips für Timeline.")

    n = len(to_append)
    batches = 0
    for start in range(0, n, APPEND_TO_TIMELINE_BATCH):
        batch = to_append[start : start + APPEND_TO_TIMELINE_BATCH]
        project.SetCurrentTimeline(timeline)
        try:
            res = media_pool.AppendToTimeline(batch)
        except Exception as ex:
            raise ResolvePipelineError(
                f"AppendToTimeline fehlgeschlagen (Batch {batches + 1}, "
                f"Clips {start + 1}–{start + len(batch)} von {n}): {ex!r}"
            ) from ex
        if not _append_ok(res):
            _log(log, f"Batch {batches + 1} leer — Einzel-Append …")
            for pl in batch:
                if not _append_ok(media_pool.AppendToTimeline([pl])):
                    raise ResolvePipelineError("AppendToTimeline (Einzel) fehlgeschlagen.")
        batches += 1
        if start + APPEND_TO_TIMELINE_BATCH < n:
            time.sleep(0.12)

    dur = timeline_frames / timeline_fps if timeline_fps > 0 else 0
    _log(
        log,
        f"Timeline {timeline_name!r}: {n} Clip(s), {dur:.1f}s ({timeline_frames} Timeline-Frames @ {timeline_fps:.3g} fps)",
    )
    return timeline


def join_and_render(
    clips: Sequence[str],
    output_dir: str,
    output_name: str,
    *,
    davinci_api_path: str,
    preset_name: str,
    width: int,
    height: int,
    analysis_fps: float,
    timeout_s: float = 3600.0,
    log: LogFn = None,
) -> None:
    """Resolve-Pipeline — im Subprocess aufrufen (Hauptthread), nicht aus GUI-Worker-Thread."""
    if not clips:
        raise ResolvePipelineError("Keine Clips.")

    ensure_utf8_stdio()
    os.makedirs(output_dir, exist_ok=True)

    src_dur = summed_source_duration(list(clips))
    _log(log, f"Quellclips gesamt: {src_dur:.1f}s ({len(clips)} Datei(en))")

    timeline_fps = pick_timeline_fps_from_clips(list(clips), log=log)
    if timeline_fps <= 0:
        timeline_fps = float(analysis_fps) if analysis_fps > 0 else 30.0

    timeline_rate = format_timeline_framerate_for_resolve(timeline_fps)
    probe0 = probe_media(clips[0])
    width = probe0.width or width
    height = probe0.height or height
    for p in clips[1:]:
        pi = probe_media(p)
        width = max(width, pi.width or 0)
        height = max(height, pi.height or 0)

    expected_frames = max(1, int(round(src_dur * timeline_fps)))
    _log(log, f"Timeline-Ziel: {src_dur:.1f}s ≈ {expected_frames} Frames @ {timeline_fps:.6g} fps ({timeline_rate})")

    time.sleep(2.0)

    resolve, project, media_pool = connect_resolve(davinci_api_path, log=log)
    project_manager = resolve.GetProjectManager()

    try:
        orig_name = project.GetName()
    except Exception:
        orig_name = None

    export_proj, export_name = _create_export_project(project_manager)
    switched = False
    if export_proj is not None:
        project = export_proj
        switched = True
        media_pool = project.GetMediaPool()
        _delete_all_timelines(project, media_pool)
        _log(log, f"Temp-Exportprojekt: {export_name}")
        time.sleep(1.0)

    try:
        _set_project_master_before_import(
            project, timeline_rate, timeline_fps, width, height, log=log
        )

        clip_specs: list[tuple[Any, int, float]] = []
        for i, path in enumerate(clips):
            norm = normalize_media_path(path)
            meta = probe_media(norm, log=log)
            clip_fps = meta.fps if meta.fps > 0 else timeline_fps
            clip_rate = format_timeline_framerate_for_resolve(clip_fps)

            _log(log, f"Import {i + 1}/{len(clips)}: {os.path.basename(norm)}")
            item = import_clip(media_pool, norm, log=log)
            _apply_clip_fps(item, clip_rate, clip_fps, log=log)
            if abs(clip_fps - timeline_fps) > 0.5:
                _log(
                    log,
                    f"  Clip native {clip_fps:.6g} fps ({clip_rate}) — Timeline bleibt @ {timeline_fps:.6g} fps",
                )
            end_f = _clip_end_frame_exclusive(norm, item, clip_fps, log=log)
            clip_specs.append((item, end_f, clip_fps))
            time.sleep(IMPORT_SETTLE_SEC)

        tl_name = f"ClipJoin_{output_name}_{int(time.time())}"
        timeline = append_clips_on_timeline(
            project,
            media_pool,
            clip_specs,
            timeline_fps=timeline_fps,
            timeline_name=tl_name,
            log=log,
        )

        _sync_active_timeline_fps(project, timeline, timeline_rate, timeline_fps)
        proj_fps, tl_fps = _read_timeline_fps_settings(project, timeline)
        export_fps, fps_mode = _reconcile_export_fps(timeline_fps, proj_fps, tl_fps)
        if fps_mode == "locked_wrong":
            raise ResolvePipelineError(
                f"Timeline-FPS {tl_fps!r} (Projekt {proj_fps!r}) passt nicht zu Quelle (~{timeline_rate})."
            )
        if fps_mode == "use_timeline":
            _log(log, f"Resolve-Timeline-FPS {export_fps:.3g} statt ffprobe {timeline_fps:.3g}")
            timeline_fps = export_fps
            timeline_rate = format_timeline_framerate_for_resolve(timeline_fps)

        project.SetCurrentTimeline(timeline)
        _sync_active_timeline_fps(
            project, timeline, timeline_rate, timeline_fps, skip_if_close=True
        )

        project.DeleteAllRenderJobs()
        preset = (preset_name or "YouTube - 1080p").strip()
        if not project.LoadRenderPreset(preset):
            _log(log, f"Preset {preset!r} nicht geladen — Resolve-Standard.")

        project.SetRenderSettings(
            {
                "SelectAllFrames": True,
                "TargetDir": output_dir.replace("\\", "/"),
                "CustomName": output_name,
                "ResolutionWidth": width,
                "ResolutionHeight": height,
                "FrameRate": float(timeline_fps),
            }
        )
        project.AddRenderJob()
        _log(log, f"Render: {output_name} → {output_dir}")
        project.StartRendering()

        if not _wait_for_render_idle(project, timeout_s, log=log):
            raise ResolvePipelineError("Render nicht erfolgreich abgeschlossen.")

        jobs = project.GetRenderJobList() or []
        if jobs:
            job_id = jobs[-1].get("JobId")
            if job_id:
                status = (project.GetRenderJobStatus(job_id) or {}).get("JobStatus", "")
                if status not in ("Complete", "Abgeschlossen"):
                    raise ResolvePipelineError(f"Render-Status: {status}")
        _log(log, "Render abgeschlossen.")
    finally:
        _restore_project(project_manager, orig_name, switched, log=log)
