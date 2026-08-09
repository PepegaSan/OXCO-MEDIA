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

    bak = src.with_name(src.name + ".hm_precut_bak")
    try:
        if bak.exists():
            bak.unlink()
        src.rename(bak)
        try:
            shutil.move(str(cut_path), str(src))
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
        log(f"Original ersetzt durch Cut: {src}")
        return src
    except Exception as exc:
        raise RuntimeError(f"Original ersetzen fehlgeschlagen: {exc}") from exc


def export_scenes(
    input_path: Path,
    scenes: list[tuple[float, float]],
    codec_key: str,
    output_dir: Path | None,
    log=print,
    safe_output: bool = True,
    replace_original: bool = False,
    target_fps: float | None = None,
) -> Path:
    if not scenes:
        raise ValueError("Keine Szenen zum Export.")
    if codec_key not in FFMPEG_CODECS:
        raise ValueError(f"Unbekannter Codec: {codec_key}")

    # Local import keeps vendor module usable standalone
    try:
        from ffmpeg_fps import apply_fps_to_vchains, fps_output_args, normalize_target_fps
    except ImportError:
        import sys
        from pathlib import Path as _P
        sys.path.insert(0, str(_P(__file__).resolve().parents[2]))
        from ffmpeg_fps import apply_fps_to_vchains, fps_output_args, normalize_target_fps

    fps = normalize_target_fps(target_fps)
    src = input_path.resolve()
    if not src.is_file():
        raise FileNotFoundError(f"Quelle nicht gefunden: {src}")

    v_extra = list(FFMPEG_CODECS[codec_key])
    r_args = fps_output_args(fps)
    out_dir = output_dir if output_dir else src.parent
    out_dir.mkdir(parents=True, exist_ok=True)
    base_out = out_dir / f"{src.stem}_cut{src.suffix or '.mp4'}"
    out_path = _unique_output(base_out) if safe_output else base_out

    log(f"Export -> {out_path}")
    log(f"Szenen: {len(scenes)}, Codec: {codec_key}" + (f", FPS: {fps:g}" if fps else ""))

    if len(scenes) == 1:
        st, en = scenes[0]
        command = [
            "ffmpeg",
            "-y",
            "-i",
            str(src),
            "-ss",
            str(st),
            "-to",
            str(en),
            *v_extra,
            *r_args,
            "-c:a",
            "aac",
            "-b:a",
            "320k",
            str(out_path),
        ]
        _run_tool(command, log=log)
    else:
        vchains: list[str] = []
        achains: list[str] = []
        for i, (st, en) in enumerate(scenes):
            stf = f"{st:.6f}"
            enf = f"{en:.6f}"
            vchains.append(f"[0:v]trim=start={stf}:end={enf},setpts=PTS-STARTPTS[v{i}]")
            achains.append(f"[0:a]atrim=start={stf}:end={enf},asetpts=PTS-STARTPTS[a{i}]")
        vchains = apply_fps_to_vchains(vchains, fps)
        n = len(scenes)
        concat_in = "".join(f"[v{i}][a{i}]" for i in range(n))
        has_audio = _ffprobe_has_audio_stream(src)
        if has_audio:
            fc = ";".join(vchains + achains) + f";{concat_in}concat=n={n}:v=1:a=1[outv][outa]"
            command = [
                "ffmpeg",
                "-y",
                "-i",
                str(src),
                "-filter_complex",
                fc,
                "-map",
                "[outv]",
                "-map",
                "[outa]",
                *v_extra,
                *r_args,
                "-c:a",
                "aac",
                "-b:a",
                "320k",
                str(out_path),
            ]
        else:
            concat_v = "".join(f"[v{i}]" for i in range(n))
            fc = ";".join(vchains) + f";{concat_v}concat=n={n}:v=1:a=0[outv]"
            command = [
                "ffmpeg",
                "-y",
                "-i",
                str(src),
                "-filter_complex",
                fc,
                "-map",
                "[outv]",
                *v_extra,
                *r_args,
                "-an",
                str(out_path),
            ]
        _run_tool(command, log=log)

    if not out_path.is_file():
        raise RuntimeError(f"Export-Datei wurde nicht erstellt: {out_path}")

    if replace_original:
        out_path = _replace_original_with_cut(src, out_path, log=log)

    return out_path


