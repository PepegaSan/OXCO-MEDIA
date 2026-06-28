"""Zeiten/FPS aus Stash-Zeilen → Frames für Resolve-AppendToTimeline."""

from __future__ import annotations

import os
import re
import unicodedata
from pathlib import Path
from typing import Any, Dict, List, Optional, Sequence, Tuple

TimelineSegment = Tuple[str, float, Optional[float]]
# (absoluter_dateipfad, start_sec, exclusive_end_sec_oder_none)


def parse_filter_terms(raw: str) -> List[str]:
    """Kommagetrennte Suchbegriffe (Leerzeichen um Kommas werden ignoriert)."""
    return [p.strip() for p in (raw or "").split(",") if p.strip()]

_INVISIBLE_PATH_CHARS = ("\ufeff", "\u200b", "\u200c", "\u200d", "\u2060")
_OUTER_QUOTE_CHARS = ('"', "'", "\u201c", "\u201d", "\u2018", "\u2019", "\u00ab", "\u00bb")


def normalize_windows_media_path(path_str: str) -> str:
    """Stash liefert oft fehlerhafte Windows-Pfade (z. B. ``D:Ordner`` ohne \\ nach dem Laufwerk)."""
    s = (path_str or "").strip()
    for ch in _INVISIBLE_PATH_CHARS:
        s = s.replace(ch, "")
    for _ in range(8):
        stripped = False
        for q in _OUTER_QUOTE_CHARS:
            if len(s) >= 2 and s.startswith(q) and s.endswith(q):
                s = s[len(q) : -len(q)].strip()
                for ch in _INVISIBLE_PATH_CHARS:
                    s = s.replace(ch, "")
                stripped = True
                break
        if not stripped:
            break
    s = s.strip().strip('"').strip("'")
    if not s:
        return s
    low = s.lower()
    if low.startswith("file:///"):
        s = s[8:]
    elif low.startswith("file://"):
        s = s[7:].lstrip("/")
    if len(s) >= 3 and s[1] == ":" and s[0].isalpha() and s[2] not in ("/", "\\"):
        s = s[:2] + "\\" + s[2:]
    if os.name == "nt":
        s = os.path.normpath(s)
    return s


def _unicode_path_forms(s: str) -> List[str]:
    out: List[str] = [s]
    seen = {s}
    try:
        for form in ("NFC", "NFD"):
            t = unicodedata.normalize(form, s)
            if t not in seen:
                seen.add(t)
                out.append(t)
    except Exception:
        pass
    return out


def path_variants_mojibake_utf8(path_str: str) -> List[str]:
    s = (path_str or "").strip()
    if not s:
        return []
    seen: set[str] = set()
    out: List[str] = []

    def add(x: str) -> None:
        t = x.strip()
        if t and t not in seen:
            seen.add(t)
            out.append(t)

    add(s)
    for enc in ("cp1252", "latin-1"):
        try:
            fixed = s.encode(enc).decode("utf-8")
            if fixed != s:
                add(fixed)
        except (UnicodeDecodeError, UnicodeEncodeError):
            continue
    return out


def media_path_candidates(path_str: str) -> List[str]:
    out: List[str] = []
    seen: set[str] = set()
    for raw in path_variants_mojibake_utf8(path_str):
        for uform in _unicode_path_forms(raw):
            n = normalize_windows_media_path(uform)
            if n and n not in seen:
                seen.add(n)
                out.append(n)
    return out


def backup_mapping_available(
    docker_path_prefix: Optional[str],
    backup_path_prefix: Optional[str],
) -> bool:
    """Backup-Umschaltung wie Stash Cutter: Remote-Präfix + Backup-Ordner gesetzt."""
    return bool((docker_path_prefix or "").strip() and (backup_path_prefix or "").strip())


def remap_docker_path_to_windows(
    path_str: str,
    remote_prefix: str,
    windows_root: str,
) -> Optional[str]:
    """
    Ersetzt den Anfang eines von Stash gelieferten Pfads durch den gleichen Ordner auf
    diesem Rechner (NAS, Docker/Linux, anderer Laufwerksbuchstabe, UNC …).

    Entspricht der Idee „Pfadpräfix wie von Stash“ / „auf diesem PC ersetzen durch“
    wie beim Marker Updater: Nur wenn ``path_str`` mit ``remote_prefix`` beginnt und
    danach ein Pfadtrenner folgt (oder der Pfad genau dem Präfix entspricht), wird
    umgebogen — damit fällt z. B. ``/database`` nicht unter ``/data``.
    """
    pre = (remote_prefix or "").strip()
    loc = (windows_root or "").strip()
    if not pre or not loc:
        return None
    raw = (path_str or "").strip()
    if not raw.startswith(pre):
        return None
    pre_core = pre.rstrip("/\\")
    try:
        root = Path(loc).expanduser()
    except OSError:
        return None
    if raw == pre or raw == pre_core:
        return os.path.normpath(str(root))
    if len(raw) > len(pre_core):
        boundary = raw[len(pre_core)]
        if boundary not in "/\\":
            return None
    rel = raw[len(pre) :].lstrip("/\\")
    if not rel:
        return os.path.normpath(str(root))
    try:
        parts = [p for p in rel.replace("\\", "/").split("/") if p]
        out = root.joinpath(*parts) if parts else root
        return os.path.normpath(str(out))
    except OSError:
        return None


def resolve_existing_media_path(
    path_str: str,
    extra_bases: Optional[Sequence[Path]] = None,
    *,
    docker_path_prefix: Optional[str] = None,
    windows_path_root: Optional[str] = None,
    backup_path_prefix: Optional[str] = None,
    use_backup: bool = False,
) -> Tuple[Optional[Path], List[str]]:
    """
    Erster existierender Dateipfad unter Kandidaten (Stash-Windows-Fix, Unicode, Mojibake).
    ``extra_bases``: z. B. Medien-Stammordner, wenn Pfade in Stash relativ sind.
    ``docker_path_prefix`` + ``windows_path_root``: Stash-Pfadpräfix (z. B. ``/data/``) → NAS/Windows.
    ``backup_path_prefix`` + ``use_backup``: dasselbe Suffix, aber Ziel = Backup statt NAS
    (wie Stash Cutter — entlastet die NAS beim Rendern, nicht „wenn Datei fehlt“).
    """
    tried: List[str] = []
    seen: set[str] = set()
    bases: List[Path] = []
    if extra_bases:
        for b in extra_bases:
            if not b or not str(b).strip():
                continue
            try:
                bases.append(Path(b).expanduser().resolve())
            except OSError:
                bases.append(Path(b).expanduser())

    def try_one(s: str) -> Optional[Path]:
        if not s or s in seen:
            return None
        seen.add(s)
        tried.append(s)
        try:
            p = Path(s)
            if p.is_file():
                return p
        except OSError:
            pass
        return None

    def try_mapped_string(mapped: str) -> Optional[Path]:
        for cand in media_path_candidates(mapped):
            pth = Path(cand)
            if pth.is_absolute():
                hit = try_one(cand)
                if hit is not None:
                    return hit
            else:
                for base in bases:
                    try:
                        joined = str((base / cand).resolve())
                    except OSError:
                        continue
                    hit = try_one(joined)
                    if hit is not None:
                        return hit
        return None

    docker = (docker_path_prefix or "").strip()
    win = (windows_path_root or "").strip()
    bak = (backup_path_prefix or "").strip()

    # Stash-Präfix → Zielordner (Backup oder NAS), optional danach NAS als Fallback
    map_targets: List[str] = []
    if docker:
        if use_backup and bak:
            map_targets.append(bak)
            if win:
                map_targets.append(win)
        elif win:
            map_targets.append(win)

    for target in map_targets:
        mapped = remap_docker_path_to_windows(path_str, docker, target)
        if mapped:
            hit = try_mapped_string(mapped)
            if hit is not None:
                return hit, tried

    # Pfad liegt schon unter NAS-Root → gleiches Suffix unter Backup
    if use_backup and win and bak:
        for cand in media_path_candidates(path_str):
            swapped = remap_docker_path_to_windows(cand, win, bak)
            if swapped:
                hit = try_mapped_string(swapped)
                if hit is not None:
                    return hit, tried

    for cand in media_path_candidates(path_str):
        p = Path(cand)
        if p.is_absolute():
            hit = try_one(cand)
            if hit is not None:
                return hit, tried
        else:
            for base in bases:
                try:
                    joined = str((base / cand).resolve())
                except OSError:
                    continue
                hit = try_one(joined)
                if hit is not None:
                    return hit, tried
            hit = try_one(cand)
            if hit is not None:
                return hit, tried
    return None, tried


def parse_float(cell: str) -> Optional[float]:
    if not cell:
        return None
    try:
        return float(str(cell).replace(",", "."))
    except ValueError:
        return None


def parse_fps_text(text: str) -> Optional[float]:
    s = (text or "").strip()
    if not s:
        return None
    if "/" in s:
        parts = s.split("/", 1)
        try:
            a = float(parts[0].strip())
            b = float(parts[1].strip())
            if b != 0:
                return a / b
        except (ValueError, ZeroDivisionError):
            return None
        return None
    try:
        return float(s.replace(",", "."))
    except ValueError:
        return None


def row_file_fps(row: Dict[str, str], fallback: float) -> float:
    for key in ("file_frame_rate",):
        f = parse_fps_text(row.get(key, "") or "")
        if f is not None and f > 0:
            return float(f)
    return max(1e-3, float(fallback))


def row_to_adjusted_end(
    row: Dict[str, str],
    file_fps: float,
    inclusive_end: bool,
) -> Tuple[Optional[float], Optional[float]]:
    """Liefert (start_sec, end_sec_fuer_dauer) — zweiter Wert wie in stash_csv (optional +1 Frame)."""
    s0 = parse_float(row.get("start_seconds", "") or "")
    s1 = parse_float(row.get("end_seconds", "") or "")
    if s0 is None:
        return None, None
    if inclusive_end and s1 is not None and s1 > s0:
        s1_adj = s1 + 1.0 / max(1e-6, file_fps)
    else:
        s1_adj = s1
    return s0, s1_adj


def clamp_marker_range_to_duration(
    start_sec: float,
    end_sec: Optional[float],
    duration_sec: float,
    *,
    eps: float = 1e-4,
) -> Tuple[float, Optional[float], bool]:
    """Begrenzt Marker-Start/Ende auf [0, Dateidauer]. Rückgabe: (start, end, wurde_gekürzt)."""
    dur = max(0.0, float(duration_sec))
    if dur <= 0:
        return max(0.0, float(start_sec)), end_sec, False

    s0 = float(start_sec)
    s1 = end_sec
    clamped = False

    if s0 < 0:
        s0 = 0.0
        clamped = True
    if s0 > dur:
        s0 = dur
        clamped = True

    if s1 is None:
        return s0, None, clamped

    s1f = float(s1)
    if s1f < 0:
        s1f = 0.0
        clamped = True
    if s1f > dur:
        s1f = dur
        clamped = True
    if s1f <= s0 + eps:
        if s0 < dur - eps:
            return s0, dur, True
        return s0, None, True
    if s1f != float(s1) or s0 != float(start_sec):
        clamped = True
    return s0, s1f, clamped


def duration_seconds(s0: float, s1_adj: Optional[float], file_fps: float, min_segment_seconds: float) -> float:
    fps = max(1e-6, file_fps)
    min_dur = max(1.0 / fps, float(min_segment_seconds))
    if s1_adj is not None and s1_adj > s0:
        return max(1.0 / fps, s1_adj - s0)
    return min_dur


def segment_to_inclusive_frames(
    s0: float,
    s1_adj: Optional[float],
    fps: float,
    min_segment_seconds: float,
    max_frame: Optional[int],
) -> Tuple[int, int]:
    """Inclusive startFrame/endFrame im Quellclip."""
    fps_f = max(1e-6, float(fps))
    dur = duration_seconds(s0, s1_adj, fps_f, min_segment_seconds)
    start_f = max(0, int(round(s0 * fps_f)))
    nframes = max(1, int(round(dur * fps_f)))
    end_f = start_f + nframes - 1
    if max_frame is not None and max_frame >= 0:
        end_f = min(end_f, max_frame)
        if end_f < start_f:
            end_f = start_f
    return start_f, end_f


def rows_to_timeline_segments(
    rows: List[Dict[str, str]],
    *,
    min_segment_seconds: float = 1.0,
    inclusive_end: bool = True,
    default_fps: float = 25.0,
    path_extra_bases: Optional[Sequence[Path]] = None,
    docker_path_prefix: Optional[str] = None,
    windows_path_root: Optional[str] = None,
    backup_path_prefix: Optional[str] = None,
    use_backup: bool = False,
) -> Tuple[List[TimelineSegment], List[str]]:
    """Konvertiert ausgewählte Zeilen in Segmente. Nur Pfade, die auf der Platte gefunden werden.

    Rückgabe: (segmente, liste_der_stash-pfade_die_nicht_aufgelöst_wurden).
    """
    out: List[TimelineSegment] = []
    unresolved: List[str] = []

    for row in rows:
        fp = (row.get("file_path") or "").strip()
        if not fp:
            continue
        hit, _tried = resolve_existing_media_path(
            fp,
            path_extra_bases,
            docker_path_prefix=docker_path_prefix,
            windows_path_root=windows_path_root,
            backup_path_prefix=backup_path_prefix,
            use_backup=use_backup,
        )
        if hit is None:
            unresolved.append(fp)
            continue
        try:
            resolved = str(hit.resolve())
        except OSError:
            resolved = str(hit)
        fps = row_file_fps(row, default_fps)
        s0, s1_adj = row_to_adjusted_end(row, fps, inclusive_end)
        if s0 is None:
            continue
        out.append((resolved, s0, s1_adj))
    return out, unresolved


def resolve_media_path(
    path_str: str,
    *,
    path_extra_bases: Optional[Sequence[Path]] = None,
    docker_path_prefix: Optional[str] = None,
    windows_path_root: Optional[str] = None,
    backup_path_prefix: Optional[str] = None,
    use_backup: bool = False,
) -> str:
    """Kompatibilität: auflösen oder normalisierten Rohpfad."""
    hit, _ = resolve_existing_media_path(
        path_str,
        path_extra_bases,
        docker_path_prefix=docker_path_prefix,
        windows_path_root=windows_path_root,
        backup_path_prefix=backup_path_prefix,
        use_backup=use_backup,
    )
    if hit is not None:
        try:
            return str(hit.resolve())
        except OSError:
            return str(hit)
    return normalize_windows_media_path(path_str.strip())


def fps_from_rows_for_path(
    rows: List[Dict[str, str]],
    resolved_path: str,
    default_fps: float,
    path_extra_bases: Optional[Sequence[Path]] = None,
    docker_path_prefix: Optional[str] = None,
    windows_path_root: Optional[str] = None,
    backup_path_prefix: Optional[str] = None,
    use_backup: bool = False,
) -> float:
    """Zeile per aufgelöstem absoluten Pfad finden."""
    want = os.path.normcase(os.path.normpath(resolved_path.strip()))
    for r in rows:
        fp = (r.get("file_path") or "").strip()
        if not fp:
            continue
        hit, _ = resolve_existing_media_path(
            fp,
            path_extra_bases,
            docker_path_prefix=docker_path_prefix,
            windows_path_root=windows_path_root,
            backup_path_prefix=backup_path_prefix,
            use_backup=use_backup,
        )
        if hit is None:
            continue
        try:
            got = os.path.normcase(os.path.normpath(str(hit.resolve())))
        except OSError:
            got = os.path.normcase(str(hit))
        if got == want:
            return row_file_fps(r, default_fps)
    return default_fps


def safe_filename_stem(path_str: str, fallback: str = "clip") -> str:
    s = (path_str or "").strip()
    if not s:
        return fallback
    stem = Path(s).stem or fallback
    stem = re.sub(r'[<>:"/\\|?*\s]+', "_", stem).strip("._")
    return stem[:80] if stem else fallback


def clip_max_frame_index(clip: Any) -> Optional[int]:
    try:
        raw = clip.GetClipProperty("Frames")
        if raw is None or raw == "":
            return None
        n = int(float(str(raw).strip()))
        return max(0, n - 1)
    except Exception:
        return None
