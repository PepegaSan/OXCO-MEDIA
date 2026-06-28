# -*- coding: utf-8 -*-
"""Gemeinsame Logik: Video/Audio einlesen, Ton säubern (FFmpeg-Filter), optional trimmen."""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import tempfile
from dataclasses import dataclass
from typing import Any, Callable


def which_ffmpeg() -> str | None:
    return shutil.which("ffmpeg")


def which_ffprobe() -> str | None:
    return shutil.which("ffprobe")


def which_ffplay() -> str | None:
    """Liegt typischerweise im selben Ordner wie ``ffmpeg`` (FFmpeg-Install)."""
    return shutil.which("ffplay")


def probe_json(ffprobe: str, path: str) -> dict[str, Any]:
    r = subprocess.run(
        [
            ffprobe,
            "-v",
            "error",
            "-print_format",
            "json",
            "-show_format",
            "-show_streams",
            path,
        ],
        capture_output=True,
        text=True,
        timeout=120,
        creationflags=_nowin(),
    )
    r.check_returncode()
    return json.loads(r.stdout)


def probe_duration(ffprobe: str, path: str) -> float:
    data = probe_json(ffprobe, path)
    d = (data.get("format") or {}).get("duration")
    if d is None:
        return 0.0
    try:
        return max(0.0, float(d))
    except (TypeError, ValueError):
        return 0.0


def has_video_stream(ffprobe: str, path: str) -> bool:
    data = probe_json(ffprobe, path)
    for s in data.get("streams") or []:
        if s.get("codec_type") == "video":
            return True
    return False


def has_audio_stream(ffprobe: str, path: str) -> bool:
    data = probe_json(ffprobe, path)
    for s in data.get("streams") or []:
        if s.get("codec_type") == "audio":
            return True
    return False


def probe_video_bitrate_kbps(ffprobe: str, path: str) -> int | None:
    """Schätzt die Video-Bitrate der Quelle in kb/s (für UI-Vorschlag).

    Nutzt zuerst ``bit_rate`` des Videostreams, sonst Container-Bitrate minus
    erster Audiostream, sonst Tags wie ``BPS``.
    """
    try:
        data = probe_json(ffprobe, path)
    except (OSError, subprocess.CalledProcessError, json.JSONDecodeError, ValueError, TypeError):
        return None

    def _kbps(bits: Any) -> int | None:
        if bits is None:
            return None
        try:
            b = int(str(bits).strip())
            if b <= 0:
                return None
            return int(round(b / 1000.0))
        except ValueError:
            return None

    for st in data.get("streams") or []:
        if st.get("codec_type") != "video":
            continue
        k = _kbps(st.get("bit_rate"))
        if k is not None and k >= 200:
            return max(200, min(100_000, k))
        tags = st.get("tags") or {}
        for key in ("BPS", "BPS-eng"):
            if key in tags:
                k = _kbps(tags.get(key))
                if k is not None and k >= 200:
                    return max(200, min(100_000, k))
        break

    fmt = data.get("format") or {}
    total_k = _kbps(fmt.get("bit_rate"))
    if total_k is None or total_k < 300:
        return None
    audio_bits = 0
    for st in data.get("streams") or []:
        if st.get("codec_type") == "audio":
            ab = st.get("bit_rate")
            if ab:
                try:
                    audio_bits = int(str(ab).strip())
                except ValueError:
                    audio_bits = 0
            break
    audio_k = max(64, int(round(audio_bits / 1000.0))) if audio_bits else 192
    est = total_k - audio_k
    if est < 200:
        return None
    return max(200, min(100_000, est))


# (id, Kurzbeschreibung für UI — ohne Fachjargon wie CRF/libx264)
VIDEO_CODEC_ITEMS: list[tuple[str, str]] = [
    ("copy", "Bild unverändert übernehmen (am schnellsten)"),
    ("h264", "H.264 — am Computer berechnen"),
    ("h265", "H.265 — am Computer berechnen"),
    ("vp9", "VP9 — am Computer berechnen"),
    ("av1", "AV1 — am Computer berechnen (kann lange dauern)"),
    ("h264_nvenc", "H.264 — NVIDIA-Grafikkarte"),
    ("hevc_nvenc", "H.265 — NVIDIA-Grafikkarte"),
    ("h264_qsv", "H.264 — Intel (Quick Sync)"),
    ("hevc_qsv", "H.265 — Intel (Quick Sync)"),
    ("h264_amf", "H.264 — AMD-Grafikkarte"),
    ("hevc_amf", "H.265 — AMD-Grafikkarte"),
    ("prores", "Apple ProRes (für Schnittprogramme, große Dateien)"),
    ("mjpeg", "Motion JPEG (einfach, ältere Geräte)"),
    ("mpeg4", "MPEG-4 (älteres Format)"),
    ("ffv1", "Archiv, verlustfrei (sehr große Dateien)"),
]

HELP_VIDEO_CODEC = """
Wählen Sie, wie das Bild in der Ausgabedatei gespeichert wird.

• „Bild unverändert übernehmen“: Das Video wird nicht neu berechnet — am schnellsten, Bild bleibt wie in der Quelle. Wenn Sie Anfang oder Ende abschneiden, kann eine Neuberechnung nötig sein; die App wechselt dann automatisch auf H.264.

• H.264 / H.265 usw.: Das Bild wird neu berechnet. Dafür legen Sie die Ziel-Bitrate fest (höher = meist besseres Bild, größere Datei).

• NVIDIA / Intel / AMD: Nur sinnvoll, wenn Ihre Grafikkarte und Ihr FFmpeg-Build das unterstützen. Sonst eine Option „am Computer berechnen“ wählen.

• ProRes und „Archiv, verlustfrei“: Spezialfälle; die Bitrate-Einstellung entfällt dabei.
""".strip()

HELP_VIDEO_BITRATE = """
Ziel-Bitrate für das Video in Kilobit pro Sekunde (kb/s).

Das ist die Datenmenge fürs Bild pro Sekunde — vergleichbar mit Angaben bei Streaming-Diensten.

Bei einer Video-Eingabe wird der Wert, sobald ffprobe eine Bitrate erkennt, als Vorschlag aus der Quelldatei gesetzt (auf 200 kb/s gerundet); Sie können ihn jederzeit ändern.

Orientierung (nur grob): HD oft 2500–5000, Full HD 4000–8000, 4K deutlich mehr.

Höher = meist schärferes Bild, aber größere Datei. Wenn Sie unsicher sind: 4000 kb/s ist ein guter Startwert für normales HD/Full-HD-Material.
""".strip()


def video_codec_needs_bitrate(codec_id: str) -> bool:
    """True, wenn die Bitrate-Spinbox für diesen Codec verwendet wird."""
    cid = (codec_id or "").strip().lower()
    return cid not in ("copy", "prores", "ffv1")


def _clamp_video_bitrate_kbps(kbps: int) -> int:
    return max(200, min(100_000, int(kbps)))


def _vbr_br_args(kbps: int) -> tuple[str, str, str]:
    """b:v, maxrate, bufsize als FFmpeg-Strings (k-Suffix)."""
    k = _clamp_video_bitrate_kbps(kbps)
    b = f"{k}k"
    mx = f"{max(k + 200, int(k * 1.12))}k"
    buf = f"{max(k * 2, k + 800)}k"
    return b, mx, buf


def effective_video_codec(codec_id: str, use_trim: bool) -> tuple[str, bool]:
    """Gibt den tatsächlichen Codec und zurück, ob von „copy“ gewechselt wurde.

    Bei zeitlichem Schnitt ist ``-c:v copy`` nicht zuverlässig — dann H.264.
    """
    cid = (codec_id or "copy").strip().lower()
    if use_trim and cid == "copy":
        return "h264", True
    valid = {x[0] for x in VIDEO_CODEC_ITEMS}
    if cid not in valid:
        return "h264", False
    return cid, False


def _export_trim_window(
    ffprobe: str,
    inp: str,
    *,
    trim_start: float,
    trim_end: float | None,
) -> tuple[float, float | None, bool]:
    start = max(0.0, float(trim_start))
    end = trim_end
    dur_full = probe_duration(ffprobe, inp)
    if end is not None and dur_full > 0:
        end = min(float(end), dur_full)
    use_trim = (start > 0.001) or (end is not None)
    return start, end, use_trim


def nvenc_windows_two_step_active(video_codec: str, *, use_trim: bool) -> bool:
    """Unter Windows: NVENC getrennt vom schweren Audio-FFmpeg-Lauf (stabiler)."""
    if os.name != "nt":
        return False
    eff_v, _ = effective_video_codec(video_codec, use_trim)
    return eff_v in ("h264_nvenc", "hevc_nvenc")


def build_ffmpeg_thread_opts_for_video_codec(codec_id: str, *, use_trim: bool) -> list[str]:
    """Vor ``-i``: bei NVENC weniger Decoder-/Filter-Thread-Parallelität (Windows)."""
    eff_v, _ = effective_video_codec(codec_id, use_trim)
    if eff_v in ("h264_nvenc", "hevc_nvenc"):
        return ["-threads", "1", "-filter_threads", "1"]
    return []


def build_nvenc_mux_queue_opts(codec_id: str, *, use_trim: bool) -> list[str]:
    """Nach ``-map``: Mux-Warteschlange nur in der Ausgabephase (nicht vor ``-i``!)."""
    eff_v, _ = effective_video_codec(codec_id, use_trim)
    if eff_v in ("h264_nvenc", "hevc_nvenc"):
        return ["-max_muxing_queue_size", "1024"]
    return []


def build_video_encoder_args(codec_id: str, *, video_kbps: int) -> list[str]:
    """Argumente für ``-c:v`` … — Ziel-Bitrate *video_kbps* in kb/s.

    Software-Codecs und QSV/AMF nutzen typisch ``-b:v``/``-maxrate``/``-bufsize``.
    NVIDIA-NVENC: schlanke RC-Optionen; dazu ``-bf 0`` (Workaround zu älteren/
    neueren NVENC-Defaults, s. u. a. FFmpeg-Trac #9351 — „bitstream buffer“ /
    Abstürze unter Windows).
    """
    b, mx, buf = _vbr_br_args(video_kbps)
    if codec_id == "copy":
        return ["-c:v", "copy"]
    if codec_id == "h264":
        return [
            "-c:v",
            "libx264",
            "-preset",
            "medium",
            "-b:v",
            b,
            "-maxrate",
            mx,
            "-bufsize",
            buf,
            "-pix_fmt",
            "yuv420p",
        ]
    if codec_id == "h265":
        return [
            "-c:v",
            "libx265",
            "-preset",
            "medium",
            "-b:v",
            b,
            "-maxrate",
            mx,
            "-bufsize",
            buf,
            "-tag:v",
            "hvc1",
            "-pix_fmt",
            "yuv420p",
        ]
    if codec_id == "vp9":
        return ["-c:v", "libvpx-vp9", "-row-mt", "1", "-b:v", b, "-maxrate", mx, "-bufsize", buf]
    if codec_id == "av1":
        return [
            "-c:v",
            "libsvtav1",
            "-preset",
            "8",
            "-b:v",
            b,
            "-maxrate",
            mx,
            "-bufsize",
            buf,
            "-pix_fmt",
            "yuv420p",
        ]
    if codec_id == "prores":
        return ["-c:v", "prores_ks", "-profile:v", "3"]
    if codec_id == "h264_nvenc":
        # Ohne maxrate/bufsize/rc:v — bewährt gegen instabile Builds.
        # -bf 0: NVENC mit B-Frames kann „buffer too small“ / Heap-Crash auslösen
        # (Default je nach FFmpeg-Version), siehe FFmpeg-Trac #9351 u. ä.
        # -delay 0 / -surfaces / -zerolatency: weniger Reorder-Puffer (Teardown unter Windows).
        # Preset p3 etwas leichter als p4 — oft weniger interne Kantenfälle.
        return [
            "-c:v",
            "h264_nvenc",
            "-preset",
            "p3",
            "-b:v",
            b,
            "-pix_fmt",
            "yuv420p",
            "-bf",
            "0",
            "-rc-lookahead",
            "0",
            "-delay",
            "0",
            "-surfaces",
            "32",
            "-zerolatency",
            "1",
        ]
    if codec_id == "hevc_nvenc":
        return [
            "-c:v",
            "hevc_nvenc",
            "-preset",
            "p3",
            "-b:v",
            b,
            "-pix_fmt",
            "yuv420p",
            "-tag:v",
            "hvc1",
            "-bf",
            "0",
            "-rc-lookahead",
            "0",
            "-delay",
            "0",
            "-surfaces",
            "32",
            "-zerolatency",
            "1",
        ]
    if codec_id == "h264_qsv":
        return [
            "-c:v",
            "h264_qsv",
            "-preset",
            "medium",
            "-b:v",
            b,
            "-maxrate",
            mx,
            "-bufsize",
            buf,
            "-look_ahead",
            "1",
        ]
    if codec_id == "hevc_qsv":
        return [
            "-c:v",
            "hevc_qsv",
            "-preset",
            "medium",
            "-b:v",
            b,
            "-maxrate",
            mx,
            "-bufsize",
            buf,
            "-look_ahead",
            "1",
        ]
    if codec_id == "h264_amf":
        return [
            "-c:v",
            "h264_amf",
            "-quality",
            "quality",
            "-rc",
            "vbr_peak",
            "-b:v",
            b,
            "-maxrate",
            mx,
            "-bufsize",
            buf,
            "-pix_fmt",
            "yuv420p",
        ]
    if codec_id == "hevc_amf":
        return [
            "-c:v",
            "hevc_amf",
            "-quality",
            "quality",
            "-rc",
            "vbr_peak",
            "-b:v",
            b,
            "-maxrate",
            mx,
            "-bufsize",
            buf,
            "-pix_fmt",
            "yuv420p",
        ]
    if codec_id == "mjpeg":
        return ["-c:v", "mjpeg", "-b:v", b, "-maxrate", mx, "-bufsize", buf]
    if codec_id == "mpeg4":
        return ["-c:v", "mpeg4", "-b:v", b, "-maxrate", mx, "-bufsize", buf]
    if codec_id == "ffv1":
        return [
            "-c:v",
            "ffv1",
            "-level",
            "3",
            "-coder",
            "1",
            "-context",
            "1",
            "-slicecrc",
            "1",
            "-slices",
            "16",
        ]
    return build_video_encoder_args("h264", video_kbps=video_kbps)


def _nowin() -> int:
    if os.name == "nt":
        return getattr(subprocess, "CREATE_NO_WINDOW", 0)
    return 0


def boost_db_from_percent(pct: float) -> float:
    """Rechnet Lautstärke-Slider (0–200 %) in dB um: 100 %% = 0 dB, 0 %% ≈ −18 dB, 200 %% ≈ +18 dB."""
    p = max(0.0, min(200.0, float(pct)))
    return (p - 100.0) * 0.18


@dataclass
class CleanParams:
    """Regler für die Audio-Kette (FFmpeg-only, keine ML-Modelle)."""

    wind_highpass_hz: int = 120  # 60–320, höher = aggressiver gegen Wind/Wummer
    denoise: int = 55  # 0–100 → anlmdn Stärke
    fft_denoise_mix: int = 40  # 0–100, 0 = aus; leichte Mischung afftdn gegen Hall-Klingel
    deesser: int = 35  # 0–100, 0 = aus
    boost_db: float = 0.0  # dB, z. B. 0–12
    loudnorm_soft: bool = True  # sanfte Lautheits-Ausrichtung vor dem Limiter


def clean_params_from_simple(
    preset: str,
    boost_db: float,
    *,
    focus_echo: bool,
    focus_noise: bool,
) -> CleanParams:
    """Mappt Presets + Fokus-Checkboxen auf :class:`CleanParams` (ohne Einzelschieber)."""
    p = (preset or "mid").strip().lower()
    if p == "low":
        wind, denoise, fft, deesser = 95, 26, 6, 16
    elif p == "high":
        # Etwas zurückhaltender als früher: sehr aggressive Werte + afftdn/anlmdn
        # lösten unter Windows gelegentlich ffmpeg-Abstürze (Exit z. B. 3221226356).
        wind, denoise, fft, deesser = 165, 78, 52, 50
    else:
        wind, denoise, fft, deesser = 120, 52, 30, 30

    if focus_echo and focus_noise:
        # Beide gleichzeitig: die Einzel-Regeln voll zu kombinieren erzeugte extreme
        # denoise/Deesser-Werte und brachte ffmpeg unter Windows zum Absturz (z. B. 3221226356).
        wind = min(255, wind + 42)
        denoise = min(76, denoise + 15)
        fft = min(72, fft + 16)
        deesser = min(90, deesser + 18)
        denoise = max(10, denoise - 10)
        fft = max(8, fft - 6)
    elif focus_echo:
        fft = min(100, fft + 26)
        deesser = min(100, deesser + 22)
        denoise = max(5, denoise - 14)

    elif focus_noise:
        wind = min(280, wind + 55)
        denoise = min(100, denoise + 24)
        fft = max(0, fft - 12)

    wind = max(60, min(280, int(wind)))
    denoise = max(0, min(100, int(denoise)))
    fft = max(0, min(100, int(fft)))
    deesser = max(0, min(100, int(deesser)))

    return CleanParams(
        wind_highpass_hz=wind,
        denoise=denoise,
        fft_denoise_mix=fft,
        deesser=deesser,
        boost_db=float(boost_db),
        loudnorm_soft=True,
    )


def build_audio_filter(p: CleanParams) -> str:
    """Baut eine kompakte -af Kette (Mono/Stereo-tauglich)."""
    parts: list[str] = []

    # Gleichmäßiges Fltp reduziert Kantenfälle in anlmdn/afftdn (stabiler unter Windows).
    parts.append("aformat=sample_fmts=fltp")

    hp = max(60, min(320, int(p.wind_highpass_hz)))
    parts.append(f"highpass=f={hp}")

    # Breitband-Rauschen / Raumhall (kein echtes AEC ohne Referenzspur)
    smin, smax = 0.00008, 0.010
    t = max(0, min(100, int(p.denoise))) / 100.0
    s = smin + t * (smax - smin)
    # Sehr hohe anlmdn-Stärke kann FFmpeg auf manchen Builds zum Absturz bringen.
    s = min(float(s), 0.0072)
    parts.append(f"anlmdn=s={s:.6f}")

    mix = max(0, min(100, int(p.fft_denoise_mix)))
    if mix > 0:
        # Zusätzlicher FFT-Denoiser (mild gegen „Halle“/Klingeln; kein echtes AEC)
        nr = min(12.0, 6.0 + (mix / 100.0) * 18.0)
        # tn=0: Rauschboden-Verfolgung aus — tn=1 verursachte mit hohem nr unter Windows Abstürze.
        parts.append(f"afftdn=nf=-28:tn=0:nr={nr:.2f}")

    ds = max(0, min(100, int(p.deesser)))
    if ds > 0:
        f = 0.2 + (ds / 100.0) * 0.55
        parts.append(f"deesser=i={f:.2f}")

    if p.loudnorm_soft:
        parts.append("dynaudnorm=f=200:g=15")

    b = float(p.boost_db)
    if abs(b) > 0.01:
        parts.append(f"volume={b:.2f}dB")

    parts.append("alimiter=level_in=1:level_out=0.97:limit=0.97:attack=5:release=50")
    return ",".join(parts)


def build_export_command(
    ffmpeg: str,
    ffprobe: str,
    inp: str,
    out: str,
    clean: CleanParams,
    *,
    trim_start: float = 0.0,
    trim_end: float | None = None,
    video_codec: str = "copy",
    video_bitrate_kbps: int = 4000,
) -> list[str]:
    """Erzeugt ffmpeg-Kommando: neuer Ton wird an Video gehängt (``-map [aout]`` + ``-map 0:v``).

    Bei Videodateien bleibt das Bildstrom-Paar erhalten; der Audiostream ist
    das Ergebnis der Filterkette. Video-Encoding per ``video_codec`` und Ziel-Bitrate.
    """
    if not has_audio_stream(ffprobe, inp):
        raise ValueError("Kein Audiostream in der Datei.")

    vids = has_video_stream(ffprobe, inp)
    af = build_audio_filter(clean)

    start = max(0.0, float(trim_start))
    end = trim_end
    dur_full = probe_duration(ffprobe, inp)
    if end is not None and dur_full > 0:
        end = min(float(end), dur_full)
    use_trim = (start > 0.001) or (end is not None)

    cmd: list[str] = [ffmpeg, "-hide_banner", "-nostdin", "-y"]
    cmd += build_ffmpeg_thread_opts_for_video_codec(video_codec, use_trim=use_trim)
    cmd += ["-i", inp]

    if start > 0:
        cmd += ["-ss", f"{start:.3f}"]
    if end is not None:
        cmd += ["-to", f"{float(end):.3f}"]

    fc = f"[0:a:0]{af}[aout]"
    cmd += ["-filter_complex", fc]
    if vids:
        eff_v, _ = effective_video_codec(video_codec, use_trim)
        cmd += ["-map", "0:v:0", "-map", "[aout]"]
        cmd += build_nvenc_mux_queue_opts(video_codec, use_trim=use_trim)
        cmd += build_video_encoder_args(eff_v, video_kbps=video_bitrate_kbps)
    else:
        cmd += ["-map", "[aout]", "-vn"]

    cmd += ["-c:a", "aac", "-b:a", "192k", out]
    return cmd


def build_export_nvenc_phase1_audio_m4a(
    ffmpeg: str,
    ffprobe: str,
    inp: str,
    audio_m4a: str,
    clean: CleanParams,
    *,
    trim_start: float = 0.0,
    trim_end: float | None = None,
) -> list[str]:
    """Phase 1 (NVENC-Split): nur gefilterter Ton → AAC in *audio_m4a*."""
    if not has_audio_stream(ffprobe, inp):
        raise ValueError("Kein Audiostream in der Datei.")
    if not has_video_stream(ffprobe, inp):
        raise ValueError("Für den NVENC-Zweischritt wird ein Videostream benötigt.")
    start, end, _ = _export_trim_window(ffprobe, inp, trim_start=trim_start, trim_end=trim_end)
    af = build_audio_filter(clean)
    cmd: list[str] = [
        ffmpeg,
        "-hide_banner",
        "-nostdin",
        "-y",
        "-threads",
        "1",
        "-filter_threads",
        "1",
        "-i",
        inp,
    ]
    if start > 0:
        cmd += ["-ss", f"{start:.3f}"]
    if end is not None:
        cmd += ["-to", f"{float(end):.3f}"]
    fc = f"[0:a:0]{af}[aout]"
    cmd += ["-filter_complex", fc, "-map", "[aout]", "-vn", "-c:a", "aac", "-b:a", "192k", audio_m4a]
    return cmd


def build_nvenc_phase2_combine_video_copy_aac(
    ffmpeg: str,
    ffprobe: str,
    video_src: str,
    audio_aac_src: str,
    out: str,
    *,
    trim_start: float = 0.0,
    trim_end: float | None = None,
    video_codec: str = "copy",
    video_bitrate_kbps: int = 4000,
) -> list[str]:
    """Phase 2 (NVENC-Split): Video aus *video_src* (Schnitt), Ton aus fertigem AAC kopieren."""
    if not has_video_stream(ffprobe, video_src):
        raise ValueError("Kein Videostream in der Eingabe.")
    start, end, use_trim = _export_trim_window(ffprobe, video_src, trim_start=trim_start, trim_end=trim_end)
    eff_v, _ = effective_video_codec(video_codec, use_trim)
    cmd: list[str] = [ffmpeg, "-hide_banner", "-nostdin", "-y"]
    cmd += build_ffmpeg_thread_opts_for_video_codec(video_codec, use_trim=use_trim)
    cmd += ["-i", video_src]
    if start > 0:
        cmd += ["-ss", f"{start:.3f}"]
    if end is not None:
        cmd += ["-to", f"{float(end):.3f}"]
    cmd += ["-i", audio_aac_src, "-map", "0:v:0", "-map", "1:a:0"]
    cmd += build_nvenc_mux_queue_opts(video_codec, use_trim=use_trim)
    cmd += build_video_encoder_args(eff_v, video_kbps=video_bitrate_kbps)
    cmd += ["-c:a", "copy", out]
    return cmd


def build_mux_nvenc_phase1_audio_m4a(
    ffmpeg: str,
    ffprobe: str,
    media_in: str,
    cleaned_wav: str,
    audio_m4a: str,
    clean: CleanParams,
    *,
    trim_start: float = 0.0,
    trim_end: float | None = None,
    apply_ffmpeg_audio_clean: bool = True,
) -> list[str]:
    """Phase 1 für DeepFilter-Mux: AAC in *audio_m4a* (optional mit FFmpeg-Preset auf DF-WAV)."""
    if not has_audio_stream(ffprobe, media_in):
        raise ValueError("Kein Audiostream in der Quelldatei.")
    if not has_video_stream(ffprobe, media_in):
        raise ValueError("Für den NVENC-Zweischritt wird ein Videostream benötigt.")
    start, end, _ = _export_trim_window(ffprobe, media_in, trim_start=trim_start, trim_end=trim_end)
    cmd: list[str] = [ffmpeg, "-hide_banner", "-nostdin", "-y", "-threads", "1", "-filter_threads", "1"]
    if apply_ffmpeg_audio_clean:
        af = build_audio_filter(clean)
        cmd += ["-i", media_in]
        if start > 0:
            cmd += ["-ss", f"{start:.3f}"]
        if end is not None:
            cmd += ["-to", f"{float(end):.3f}"]
        cmd += ["-i", cleaned_wav]
        fc = f"[1:a]{af}[aout]"
        cmd += ["-filter_complex", fc, "-map", "[aout]", "-vn", "-c:a", "aac", "-b:a", "192k", audio_m4a]
    else:
        cmd += ["-i", cleaned_wav, "-map", "0:a:0", "-vn", "-c:a", "aac", "-b:a", "192k", audio_m4a]
    return cmd


def run_export_nvenc_two_step_windows(
    ffmpeg: str,
    ffprobe: str,
    inp: str,
    out: str,
    clean: CleanParams,
    *,
    trim_start: float,
    trim_end: float | None,
    video_codec: str,
    video_bitrate_kbps: int,
    log_line: Callable[[str], None],
    cancel_event: Any | None = None,
) -> int:
    """Windows + NVENC + Video: Ton und Bild in zwei FFmpeg-Prozessen (stabiler)."""
    _, _, use_trim = _export_trim_window(ffprobe, inp, trim_start=trim_start, trim_end=trim_end)
    if not nvenc_windows_two_step_active(video_codec, use_trim=use_trim) or not has_video_stream(
        ffprobe, inp
    ):
        cmd = build_export_command(
            ffmpeg,
            ffprobe,
            inp,
            out,
            clean,
            trim_start=trim_start,
            trim_end=trim_end,
            video_codec=video_codec,
            video_bitrate_kbps=video_bitrate_kbps,
        )
        return run_ffmpeg(cmd, log_line, cancel_event)

    fd, tmp = tempfile.mkstemp(suffix=".m4a", prefix="acc_nv1_")
    os.close(fd)
    try:
        log_line("[Hinweis] Windows/NVENC: zweistufiger Export — Phase 1: AAC-Ton, Phase 2: Bild+Mux.\n")
        cmd1 = build_export_nvenc_phase1_audio_m4a(
            ffmpeg, ffprobe, inp, tmp, clean, trim_start=trim_start, trim_end=trim_end
        )
        log_line("[Befehl] " + " ".join(cmd1) + "\n")
        r1 = run_ffmpeg(cmd1, log_line, cancel_event)
        if r1 != 0:
            return r1
        if cancel_event is not None and getattr(cancel_event, "is_set", lambda: False)():
            return -1
        cmd2 = build_nvenc_phase2_combine_video_copy_aac(
            ffmpeg,
            ffprobe,
            inp,
            tmp,
            out,
            trim_start=trim_start,
            trim_end=trim_end,
            video_codec=video_codec,
            video_bitrate_kbps=video_bitrate_kbps,
        )
        log_line("[Befehl] " + " ".join(cmd2) + "\n")
        return run_ffmpeg(cmd2, log_line, cancel_event)
    finally:
        try:
            os.unlink(tmp)
        except OSError:
            pass


def run_mux_nvenc_two_step_windows(
    ffmpeg: str,
    ffprobe: str,
    media_in: str,
    cleaned_wav: str,
    out: str,
    clean: CleanParams,
    *,
    trim_start: float,
    trim_end: float | None,
    video_codec: str,
    video_bitrate_kbps: int,
    apply_ffmpeg_audio_clean: bool,
    log_line: Callable[[str], None],
    cancel_event: Any | None = None,
) -> int:
    """Windows + NVENC + Video: DeepFilter-Mux in zwei FFmpeg-Prozessen."""
    _, _, use_trim = _export_trim_window(ffprobe, media_in, trim_start=trim_start, trim_end=trim_end)
    if not nvenc_windows_two_step_active(video_codec, use_trim=use_trim) or not has_video_stream(
        ffprobe, media_in
    ):
        cmd = build_mux_external_wav_command(
            ffmpeg,
            ffprobe,
            media_in,
            cleaned_wav,
            out,
            clean,
            trim_start=trim_start,
            trim_end=trim_end,
            video_codec=video_codec,
            video_bitrate_kbps=video_bitrate_kbps,
            apply_ffmpeg_audio_clean=apply_ffmpeg_audio_clean,
        )
        return run_ffmpeg(cmd, log_line, cancel_event)

    fd, tmp = tempfile.mkstemp(suffix=".m4a", prefix="acc_nv1_")
    os.close(fd)
    try:
        log_line("[Hinweis] Windows/NVENC: zweistufiger Mux — Phase 1: AAC-Ton, Phase 2: Bild+Mux.\n")
        cmd1 = build_mux_nvenc_phase1_audio_m4a(
            ffmpeg,
            ffprobe,
            media_in,
            cleaned_wav,
            tmp,
            clean,
            trim_start=trim_start,
            trim_end=trim_end,
            apply_ffmpeg_audio_clean=apply_ffmpeg_audio_clean,
        )
        log_line("[Befehl] " + " ".join(cmd1) + "\n")
        r1 = run_ffmpeg(cmd1, log_line, cancel_event)
        if r1 != 0:
            return r1
        if cancel_event is not None and getattr(cancel_event, "is_set", lambda: False)():
            return -1
        cmd2 = build_nvenc_phase2_combine_video_copy_aac(
            ffmpeg,
            ffprobe,
            media_in,
            tmp,
            out,
            trim_start=trim_start,
            trim_end=trim_end,
            video_codec=video_codec,
            video_bitrate_kbps=video_bitrate_kbps,
        )
        log_line("[Befehl] " + " ".join(cmd2) + "\n")
        return run_ffmpeg(cmd2, log_line, cancel_event)
    finally:
        try:
            os.unlink(tmp)
        except OSError:
            pass


def build_mux_external_wav_command(
    ffmpeg: str,
    ffprobe: str,
    media_in: str,
    cleaned_wav: str,
    out: str,
    clean: CleanParams,
    *,
    trim_start: float = 0.0,
    trim_end: float | None = None,
    video_codec: str = "copy",
    video_bitrate_kbps: int = 4000,
    apply_ffmpeg_audio_clean: bool = True,
) -> list[str]:
    """Mux: Video (optional, mit Schnitt) von ``media_in`` + bereits gesäubertes Audio aus ``cleaned_wav``.

    ``cleaned_wav`` muss exakt zum gewählten Segment passen (wird vorher per
    DeepFilter-Pipeline erzeugt). Wenn *apply_ffmpeg_audio_clean* True ist,
    laufen zusätzlich die FFmpeg-Filter aus ``CleanParams`` auf diesem Ton;
    bei False wird nur noch nach AAC kodiert (reiner DeepFilter-Ton).
    """
    if not has_audio_stream(ffprobe, media_in):
        raise ValueError("Kein Audiostream in der Quelldatei.")

    vids = has_video_stream(ffprobe, media_in)
    af = build_audio_filter(clean) if apply_ffmpeg_audio_clean else ""
    start = max(0.0, float(trim_start))
    end = trim_end
    dur_full = probe_duration(ffprobe, media_in)
    if end is not None and dur_full > 0:
        end = min(float(end), dur_full)
    use_trim = (start > 0.001) or (end is not None)

    if not vids:
        if apply_ffmpeg_audio_clean:
            fc = f"[0:a]{af}[aout]"
            return [
                ffmpeg,
                "-hide_banner",
                "-nostdin",
                "-y",
                "-i",
                cleaned_wav,
                "-filter_complex",
                fc,
                "-map",
                "[aout]",
                "-c:a",
                "aac",
                "-b:a",
                "192k",
                out,
            ]
        return [
            ffmpeg,
            "-hide_banner",
            "-nostdin",
            "-y",
            "-i",
            cleaned_wav,
            "-map",
            "0:a:0",
            "-c:a",
            "aac",
            "-b:a",
            "192k",
            out,
        ]

    cmd: list[str] = [ffmpeg, "-hide_banner", "-nostdin", "-y"]
    cmd += build_ffmpeg_thread_opts_for_video_codec(video_codec, use_trim=use_trim)
    cmd += ["-i", media_in]

    if start > 0:
        cmd += ["-ss", f"{start:.3f}"]
    if end is not None:
        cmd += ["-to", f"{float(end):.3f}"]

    cmd += ["-i", cleaned_wav]

    eff_v, _ = effective_video_codec(video_codec, use_trim)
    if apply_ffmpeg_audio_clean:
        fc = f"[1:a]{af}[aout]"
        cmd += ["-filter_complex", fc]
        cmd += ["-map", "0:v:0", "-map", "[aout]"]
    else:
        cmd += ["-map", "0:v:0", "-map", "1:a:0"]
    cmd += build_nvenc_mux_queue_opts(video_codec, use_trim=use_trim)
    cmd += build_video_encoder_args(eff_v, video_kbps=video_bitrate_kbps)

    cmd += ["-c:a", "aac", "-b:a", "192k", out]
    return cmd


def windows_ffmpeg_abnormal_exit_code(code: int) -> bool:
    """Typische Windows-Exitwerte, wenn natives Code (ffmpeg/NVENC) abstürzt."""
    if code in (0, -1):
        return False
    return code in (
        3221225477,  # STATUS_ACCESS_VIOLATION (oft beim Beenden / DLL-Unload)
        3221226356,  # STATUS_HEAP_CORRUPTION
    )


def expected_mux_duration_seconds(
    ffprobe: str,
    inp: str,
    *,
    trim_start: float = 0.0,
    trim_end: float | None = None,
) -> float:
    """Erwartete Spieldauer der Ausgabe nach Schnitt (Sekunden), für Plausibilitätsprüfung."""
    dur = probe_duration(ffprobe, inp)
    start = max(0.0, float(trim_start))
    end = trim_end
    if end is not None and dur > 0:
        end = min(float(end), dur)
    if end is None:
        if dur <= 0:
            return 0.0
        return max(0.1, dur - start)
    return max(0.1, float(end) - start)


def adjust_export_returncode_if_output_ok(
    rc: int,
    ffprobe: str,
    out_path: str,
    expected_duration_sec: float,
) -> tuple[int, str | None]:
    """Setzt Exitcode 0, wenn ein Windows-Absturzexit vorliegt, die MP4 aber plausibel fertig ist."""
    if rc == 0:
        return 0, None
    if not windows_ffmpeg_abnormal_exit_code(rc):
        return rc, None
    path = (out_path or "").strip()
    if not path:
        return rc, None
    try:
        if not os.path.isfile(path) or os.path.getsize(path) < 4096:
            return rc, None
    except OSError:
        return rc, None
    try:
        got = probe_duration(ffprobe, path)
    except (OSError, subprocess.CalledProcessError, json.JSONDecodeError, ValueError, TypeError):
        return rc, None
    exp = max(0.5, float(expected_duration_sec))
    if got <= 0 or exp <= 0:
        return rc, None
    # Nach NVENC-Teardown kann die Container-Dauer knapp unter der Zielzeit liegen (GOP/MDAT).
    slack_abs = 3.5 if exp > 45.0 else 2.5
    plausible = got >= exp * 0.76 or got >= exp - slack_abs
    if plausible:
        return 0, (
            f"[Hinweis] FFmpeg endete mit Windows-Code {rc} (typisch NVENC/Treiber beim Aufräumen), "
            f"ffprobe meldet aber eine nahezu vollständige Länge (~{got:.1f}s von ~{exp:.1f}s) — bitte die Ausgabe kurz abspielen."
        )
    return rc, None


def run_ffmpeg(
    cmd: list[str],
    log_line: Callable[[str], None],
    cancel_event: Any | None = None,
) -> int:
    """Streamt stdout/stderr Zeilenweise an log_line. Rückgabe: Exit-Code."""
    creationflags = _nowin()
    proc = subprocess.Popen(
        cmd,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        creationflags=creationflags,
    )
    assert proc.stdout
    for line in proc.stdout:
        if cancel_event is not None and getattr(cancel_event, "is_set", lambda: False)():
            proc.terminate()
            try:
                proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                proc.kill()
            return -1
        log_line(line)
    return int(proc.wait() if proc.returncode is None else proc.returncode)
