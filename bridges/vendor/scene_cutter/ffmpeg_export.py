"""Vendored FFmpeg-Export aus Cutter/cutter.py — Original-Repo unverändert."""

from __future__ import annotations



import shutil

import subprocess

import sys

from pathlib import Path



FFMPEG_CODECS: dict[str, tuple[str, ...]] = {

    "h264_nvenc": ("-c:v", "h264_nvenc", "-preset", "p6", "-cq", "18"),

    "hevc_nvenc": ("-c:v", "hevc_nvenc", "-preset", "p6", "-cq", "22"),

    "libx264": ("-c:v", "libx264", "-preset", "medium", "-crf", "18"),

    "libx265": ("-c:v", "libx265", "-preset", "medium", "-crf", "22"),

}





def _resolve_tool(name: str) -> str:

    path = shutil.which(name)

    if path:

        return path



    local = Path.home() / "AppData" / "Local" / "Microsoft" / "WinGet" / "Links" / f"{name}.exe"

    if local.is_file():

        return str(local)



    raise FileNotFoundError(

        f"{name} nicht gefunden. FFmpeg installieren und PATH prüfen "

        "(Hail Mary nach Installation neu starten)."

    )





def _subprocess_kw() -> dict:

    kw: dict = dict(

        check=True,

        stdout=subprocess.PIPE,

        stderr=subprocess.STDOUT,

        text=True,

        encoding="utf-8",

        errors="replace",

    )

    if sys.platform == "win32":

        kw["creationflags"] = getattr(subprocess, "CREATE_NO_WINDOW", 0)

    return kw





def _run_tool(command: list[str], log=print) -> None:

    tool = _resolve_tool(command[0])

    command[0] = tool

    log(f"$ {' '.join(command)}")

    try:

        result = subprocess.run(command, **_subprocess_kw())

    except FileNotFoundError:

        raise

    except subprocess.CalledProcessError as exc:

        tail = (exc.stdout or "")[-1500:]

        raise RuntimeError(

            f"{Path(tool).name} fehlgeschlagen (Code {exc.returncode}):\n{tail}"

        ) from exc



    if result.stdout:

        for line in result.stdout.splitlines():

            log(line)





def _ffprobe_has_audio_stream(path: Path) -> bool:

    command = [

        "ffprobe",

        "-v",

        "error",

        "-select_streams",

        "a:0",

        "-show_entries",

        "stream=index",

        "-of",

        "csv=p=0",

        str(path),

    ]

    try:

        tool = _resolve_tool("ffprobe")

        command[0] = tool

        kw = _subprocess_kw()

        result = subprocess.run(command, **kw)

        return result.returncode == 0 and bool(result.stdout and result.stdout.strip())

    except (OSError, subprocess.TimeoutExpired, FileNotFoundError, RuntimeError):

        return False





def _unique_output(base: Path) -> Path:

    if not base.exists():

        return base

    stem, suffix = base.stem, base.suffix

    for n in range(2, 10000):

        candidate = base.with_name(f"{stem}_{n}{suffix}")

        if not candidate.exists():

            return candidate

    return base.with_name(f"{stem}_out{suffix}")






def _replace_original_with_cut(src: Path, cut_path: Path, log=print) -> Path:
    """Ersetzt die Quelldatei durch den Cut; gibt den finalen Pfad (Originalname) zurueck."""
    src = src.resolve()
    cut_path = cut_path.resolve()
    if not cut_path.is_file():
        raise RuntimeError(f"Cut-Datei fehlt fuer Replace: {cut_path}")
    if cut_path.stat().st_size < 1024:
        raise RuntimeError(f"Cut-Datei zu klein / ungueltig: {cut_path}")
    if src == cut_path:
        log(f"Replace unnoetig (Cut == Quelle): {src}")
        return src

    # Same name as the original; only the extension follows the cut (webm source → mp4 cut).
    target = src.with_suffix(cut_path.suffix) if cut_path.suffix.lower() != src.suffix.lower() else src
    bak = src.with_name(src.name + ".hm_precut_bak")
    try:
        if bak.exists():
            bak.unlink()
        src.rename(bak)
        try:
            shutil.move(str(cut_path), str(target))
        except Exception:
            try:
                if not src.exists() and bak.exists():
                    bak.rename(src)
            except Exception:
                pass
            raise
        try:
            bak.unlink(missing_ok=True)
        except TypeError:
            if bak.exists():
                bak.unlink()
        except OSError as exc:
            log(f"WARN: Backup konnte nicht geloescht werden ({bak}): {exc}")
        log(f"Original ersetzt durch Cut: {target}")
        return target
    except Exception as exc:
        raise RuntimeError(f"Original ersetzen fehlgeschlagen: {exc}") from exc


# H.264/HEVC + AAC are not allowed in these containers — write .mp4 instead.
_REMUX_TO_MP4 = {".webm", ".wmv", ".flv", ".ogv", ".3gp", ".mpg", ".mpeg", ".ts", ".m2ts", ".mts", ".vob"}


def _output_suffix(src: Path) -> str:
    suffix = (src.suffix or ".mp4").lower()
    return ".mp4" if suffix in _REMUX_TO_MP4 else (src.suffix or ".mp4")


def export_scenes(
    input_path: Path,
    scenes: list[tuple[float, float]],
    codec_key: str,
    output_dir: Path | None,
    log=print,
    safe_output: bool = True,
    replace_original: bool = False,
    target_fps: float | None = None,
    progress=None,
) -> Path:
    if not scenes:
        raise ValueError("Keine Szenen zum Export.")
    if codec_key not in FFMPEG_CODECS:
        raise ValueError(f"Unbekannter Codec: {codec_key}")

    # Local import keeps vendor module usable standalone
    try:
        from ffmpeg_fps import fps_filter_clause, fps_output_args, normalize_target_fps
        from ffmpeg_progress import run_ffmpeg
    except ImportError:
        from pathlib import Path as _P
        sys.path.insert(0, str(_P(__file__).resolve().parents[2]))
        from ffmpeg_fps import fps_filter_clause, fps_output_args, normalize_target_fps
        from ffmpeg_progress import run_ffmpeg

    fps = normalize_target_fps(target_fps)
    src = input_path.resolve()
    if not src.is_file():
        raise FileNotFoundError(f"Quelle nicht gefunden: {src}")

    v_extra = list(FFMPEG_CODECS[codec_key])
    out_dir = output_dir if output_dir else src.parent
    out_dir.mkdir(parents=True, exist_ok=True)
    suffix = _output_suffix(src)
    if suffix.lower() != (src.suffix or "").lower():
        log(f"Container {src.suffix} unterstützt H.264/AAC nicht — Ausgabe als {suffix}")
    base_out = out_dir / f"{src.stem}_cut{suffix}"
    out_path = _unique_output(base_out) if safe_output else base_out

    log(f"Export -> {out_path}")
    log(f"Szenen: {len(scenes)}, Codec: {codec_key}" + (f", FPS: {fps:g}" if fps else ""))

    has_audio = _ffprobe_has_audio_stream(src)
    ffmpeg = _resolve_tool("ffmpeg")
    # Each scene is its own input with a fast seek in front of -i: FFmpeg jumps straight to the
    # scene (frame-accurate when re-encoding) instead of decoding the file from the start.
    command: list[str] = [ffmpeg, "-y", "-hide_banner"]
    for st, en in scenes:
        command += ["-ss", f"{st:.6f}", "-t", f"{en - st:.6f}", "-i", str(src)]

    n = len(scenes)
    if n == 1:
        command += ["-map", "0:v:0"]
        if has_audio:
            command += ["-map", "0:a:0"]
        if fps:
            command += ["-vf", f"fps={fps:g}"]
    else:
        clause = fps_filter_clause(fps)
        chains = [f"[{i}:v:0]setpts=PTS-STARTPTS{clause}[v{i}]" for i in range(n)]
        if has_audio:
            chains += [f"[{i}:a:0]asetpts=PTS-STARTPTS[a{i}]" for i in range(n)]
            pads = "".join(f"[v{i}][a{i}]" for i in range(n))
            chains.append(f"{pads}concat=n={n}:v=1:a=1[outv][outa]")
            command += ["-filter_complex", ";".join(chains), "-map", "[outv]", "-map", "[outa]"]
        else:
            pads = "".join(f"[v{i}]" for i in range(n))
            chains.append(f"{pads}concat=n={n}:v=1:a=0[outv]")
            command += ["-filter_complex", ";".join(chains), "-map", "[outv]"]

    command += [*v_extra, *fps_output_args(fps)]
    command += ["-c:a", "aac", "-b:a", "320k"] if has_audio else ["-an"]
    if suffix.lower() in (".mp4", ".m4v", ".mov"):
        command += ["-movflags", "+faststart"]
    command.append(str(out_path))

    total = sum(en - st for st, en in scenes)
    run_ffmpeg(command, total_seconds=total, progress=progress, label="Export", log=log)

    if not out_path.is_file():
        raise RuntimeError(f"Export-Datei wurde nicht erstellt: {out_path}")

    if replace_original:
        out_path = _replace_original_with_cut(src, out_path, log=log)

    return out_path
