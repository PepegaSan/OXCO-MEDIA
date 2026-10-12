"""
Oxco compare: analyse an original and its deepfake frame by frame, cut short swap dropouts,
export the result (EDL / FFmpeg / DaVinci Resolve).

CLI (Hail Mary): compare.py SOURCE DEEPFAKE [--auto] [--retry-export-only | --defer-davinci | --davinci-export-only]
Settings come from the ini in OXCO_SETTINGS_INI (or settings.ini next to this file).
"""

import cv2
import json
import sys
import os
import shutil
import time
import subprocess
import configparser
import re
import datetime
import random

import oxco_analysis as oa

TEXTS = {
    'en': {
        'unexpected_error': "\nAn unexpected error occurred: {error}",
        'usage': "Usage: compare.py SOURCE DEEPFAKE [--auto] [--retry-export-only|--defer-davinci|--davinci-export-only]",
        'set_mode': "- Analysis mode: {val}",
        'mode_classic': "classic (changed pixels, full resolution)",
        'mode_blob': "blob (largest changed region, {w}px analysis width, blur {blur})",
        'set_postfilter': "- Post filter: min. difference run {run} fr. | min. kept piece {keep} fr. | cut padding {pad} fr.",
        'set_sync': "- Auto sync offset: {val}",
        'sync_applied': "[INFO] Frame offset detected: deepfake frame k = source frame k{sign}{n} (score {s0:.2f} → {sb:.2f}). Skipping {n} leading frame(s) of the {which}.\n",
        'sync_none': "[INFO] No frame offset between the videos (checked ±{n} frames).\n",
        'which_source': "source",
        'which_deepfake': "deepfake",
        'reader_info': "[INFO] Decoder: {reader} | analysis size {w}x{h}\n",
        'metrics_reused': "[INFO] Reusing cached analysis ({frames} frames) — only filters are applied again, no decoding.\n",
        'metrics_saved': "[INFO] Per-frame metrics saved: {path}\n",
        'analysis_time': "[INFO] Analysis took {sec:.1f}s ({fps:.0f} frame pairs/s).\n",
        'cut_summary': "[INFO] Result: {kept:.1f}s kept, {cut:.1f}s cut in {n} piece(s).\n",
        'no_swap_warning': "[WARN] Not a single frame exceeds the threshold — is this really the matching original/deepfake pair? (Nothing is cut.)\n",
        'all_swap_warning': "[WARN] Every analysed frame differs — wrong pairing, different edit or a global colour/scale change? Check the preview; 'Pixel max' can ignore global changes.\n",
        'retry_refilter': "[INFO] Retry: applying current filters to the cached analysis ({frames} frames).\n",
        'davinci_res_fallback': "[INFO] Frame size for Resolve taken from OpenCV: {w}x{h}\n",
        'davinci_res_default': "[WARN] Could not read the deepfake frame size; using 1920x1080 for the Resolve timeline.\n",
        'ffmpeg_no_audio': "[INFO] {name} has no audio track — exporting video only.\n",
        'ffmpeg_retry_no_audio': "[INFO] FFmpeg failed with audio; retrying without audio track...\n",
        'loading_settings': "Loading settings from settings.ini:",
        'set_buffer': "- Buffer: {val} seconds",
        'set_noise': "- Noise Filter: {val}",
        'set_pixel': "- Pixel Tolerance: {val}",
        'set_pixel_max': "- Pixel ceiling (0=off): {val}",
        'set_davinci': "- DaVinci API Export: {val}",
        'set_davinci_timeout': "- DaVinci render time limit: {val}",
        'davinci_timeout_off': "none (wait until DaVinci finishes)",
        'set_ffmpeg': "- FFmpeg Export: {val}",
        'set_ffmpeg_target': "- FFmpeg renders: {val}",
        'ffmpeg_target_both': "source and deepfake",
        'ffmpeg_target_source': "source only",
        'ffmpeg_target_deepfake': "deepfake only",
        'set_fullcheck': "- FullCheck EDL: {val}",
        'set_export_unique': "- Avoid overwriting video exports (FFmpeg / DaVinci): {val}",
        'on': "On",
        'off': "Off",
        'start_analysis': "\nStarting pixel difference analysis for {frames} frames...",
        'analyzing': "\rAnalyzing: {percent:.1f}% ({current}/{total} frames)",
        'frames_processed': "\n{count} frames processed. Applying filters...",
        'no_diff': "No difference found between the videos.",
        'edl_auto_created': "\nAutoDelete EDL files created:",
        'edl_full_created': "\nFullCheck EDL files created.",
        'all_done': "\nAll operations completed.",
        'compare_exit_partial': (
            "\n[INFO] An enabled export (FFmpeg and/or DaVinci) did not complete successfully. "
            "Exit code 3 — adjust Filters in Oxco and use Retry.\n"
        ),
        'checkpoint_written': "[INFO] Export checkpoint saved (Retry runs export only, no new pixel analysis).\n",
        'checkpoint_write_failed': "[WARN] Could not write export checkpoint: {error}\n",
        'retry_export_start': "[INFO] Retry: export only (EDL / FFmpeg / DaVinci) — same cut as last analysis.\n",
        'retry_export_no_checkpoint': "[ERROR] No export checkpoint. Run a full Compare first (analysis must find differences).\n",
        'retry_export_ck_read_err': "[ERROR] Export checkpoint unreadable: {error}\n",
        'retry_export_path_mismatch': "[ERROR] Checkpoint does not match these two video paths. Run full Compare again.\n",
        'retry_export_missing_file': "[ERROR] Source or deepfake file is missing on disk.\n",
        'retry_export_auto_only': "[ERROR] --retry-export-only is only supported with two paths and --auto (Oxco Retry).\n",
        'ffmpeg_render': "Rendering {name} via FFmpeg ({codec})...",
        'ffmpeg_progress': "\rProgress: {percent:.1f}% ({frame}/{total} frames)",
        'ffmpeg_error': "\n\n--- FFmpeg Error for {name} ---",
        'ffmpeg_success': "\n-> Successfully created.",
        'davinci_api_missing': "\n[ERROR] DaVinci API (DaVinciResolveScript) not found!",
        'davinci_api_path_searched': "Python explicitly searched in this folder:\n{path}",
        'davinci_api_check_explorer': "Please check in Windows Explorer if this folder and the file 'DaVinciResolveScript.py' actually exist there.",
        'davinci_not_open': "\n[ERROR] DaVinci Resolve Studio must be open before starting this script!",
        'davinci_api_retry': "[INFO] DaVinci scripting API not ready yet; retrying ({n}/{max}, pause {delay}s between checks)...\n",
        'davinci_scriptapp_stopped': "[ERROR] DaVinci Resolve closed while waiting for the scripting API.\n",
        'davinci_scriptapp_hint': (
            "[INFO] If this repeats for a long time: use Resolve **Studio** (external scripting is not in the free "
            "version), open a project, and run Resolve + this tool as the same Windows user (not \"Run as admin\" for "
            "only one of them). You can raise SETTINGS → davinci_scriptapp_retry_attempts / "
            "davinci_scriptapp_retry_delay_seconds in settings.ini.\n"
        ),
        'davinci_fallback_skip': "\n[WARN] DaVinci Resolve is not running or the scripting API is unavailable.\nPixel analysis, EDL, and FFmpeg (if enabled) will still run; DaVinci export is skipped.\n",
        'davinci_export_exception': "\n[WARN] DaVinci export failed: {error}\n[INFO] Pixel analysis and any EDL/FFmpeg files written above are still valid.\n",
        'davinci_export_interrupted': "\n[INFO] DaVinci export interrupted or API error while talking to Resolve: {error}\n[INFO] Pixel analysis and any EDL/FFmpeg output above are still valid.\n",
        'davinci_render_no_jobs': "\n[INFO] DaVinci returned no render job info (Resolve may have closed during export).\n",
        'compare_summary_davinci_ok': "[INFO] DaVinci export: completed successfully.\n",
        'compare_summary_davinci_fail': "[INFO] DaVinci export: did not complete (Resolve closed, API error, or timeout). Analysis, EDL, and FFmpeg (if enabled) above are unchanged.\n",
        'davinci_no_project': "\n[ERROR] No open project found in DaVinci Resolve.",
        'davinci_no_project_wait': "[INFO] No open Resolve project yet (typical right after a restart). Retrying ({n}/{max})...\n",
        'davinci_no_project_hint': "[INFO] Tip: Open or create any project in Resolve, then run compare again.\n",
        'davinci_export_start_info': "[INFO] DaVinci export starting (API path: {path})\n",
        'davinci_script_missing': "[WARN] DaVinciResolveScript.py not found:\n{path}\nCheck PATHS → davinci_api_path in settings.ini (Resolve Studio scripting \"Modules\" folder).\n",
        'davinci_sending_data': "\nSending data via API to DaVinci Resolve Studio...",
        'davinci_import_error': "[ERROR] Could not import {path} into Resolve.",
        'davinci_timeline_error': "[ERROR] Could not create a timeline.",
        'davinci_no_valid_scenes': "No valid scenes found to render.",
        'davinci_preset_warning': "\n[WARNING] Could not find render preset '{preset}' in DaVinci!",
        'davinci_preset_fallback': "Rendering will proceed using the last settings used in DaVinci.",
        'davinci_job_created': "Render job for '{name}' created.",
        'davinci_start_render': "Starting hardware rendering in DaVinci Resolve...",
        'davinci_render_running': "-> Rendering in progress! (Progress is now displayed over in the DaVinci Resolve GUI)",
        'davinci_meta_no_resolution': "No resolution in clip metadata.",
        'davinci_meta_parse_error': "[Info] Could not read clip metadata completely: {error}",
        'davinci_render_timeout_exceeded': "DaVinci render exceeded {sec} seconds; export was stopped.",
        'davinci_render_status_failed': "DaVinci render job finished with status: {status}",
        'fps_fallback': "[Info] Could not read a valid FPS from the source video; using 30.",
        'fps_ffprobe': "[INFO] Using stream FPS {fps} from ffprobe for analysis and DaVinci timeline (OpenCV reported {opencv}).\n",
        'fps_ffprobe_only': "[INFO] Using stream FPS {fps} from ffprobe (OpenCV had no valid rate).\n",
        'fps_r_vs_avg': "[INFO] ffprobe r_frame_rate={r:.6g} vs avg_frame_rate={avg:.6g} — using nominal r_frame_rate for timeline (avg is often wrong on NTSC MP4).\n",
        'fps_prefer_opencv': "[INFO] ffprobe avg_frame_rate={avg:.6g} disagrees with OpenCV {ocv:.6g} — using OpenCV (typical when avg is mis-muxed).\n",
        'davinci_timeline_fps': "[INFO] DaVinci timeline frame rate: {rate} (same as compare analysis; Resolve clip metadata was {clip}).\n",
        'davinci_set_failed': "[WARN] DaVinci API refused setting {key}={val!r} on {where} (timeline may keep previous FPS until this succeeds).\n",
        'davinci_fps_verify': "[INFO] DaVinci FPS read-back — Project (Master Settings dropdown): {proj} | Active timeline object: {tl}\n",
        'davinci_master_preset': "[INFO] Resolve Master after pre-import preset: timelineFrameRate read-back={fps} | {w}x{h}\n",
        'davinci_fps_locked_hint': "[WARN] Master timeline FPS is still {read} (wanted {target}). Resolve often locks FPS once the Media Pool already has clips — use an empty pool, a new project, or a project template that matches the target rate.\n",
        'davinci_temp_project': "[INFO] Using temporary Resolve project '{temp}' for this export (Master FPS can be set with no timelines). Your project '{orig}' will be reopened when finished.\n",
        'davinci_temp_project_failed': "[WARN] Could not create a temporary Resolve project; continuing in the current project — Master FPS may stay locked if timelines or pool clips already exist.\n",
        'davinci_restored_project': "[INFO] Restored Resolve project '{name}'.\n",
        'davinci_restore_failed': "[WARN] Could not switch back to Resolve project '{name}': {error}\n",
        'davinci_render_never_started': "[WARN] DaVinci did not report an active render within {sec}s (script would otherwise exit too early and files can be truncated). Check Deliver page; retry export.\n",
        'davinci_autostart_already': "[INFO] DaVinci Resolve is already running.\n",
        'davinci_autostart_start': "[INFO] DaVinci Resolve is not running. Starting (PATHS → davinci_exe_path)...\n",
        'davinci_autostart_wait': "[INFO] Waiting {sec} seconds for Resolve (SETTINGS → davinci_startup_wait_seconds)...\n",
        'davinci_autostart_err': "[ERROR] Could not start DaVinci Resolve: {e}\n",
        'davinci_autostart_path': "[INFO] Executable path was: {path}\n",
        'davinci_autostart_no_exe': "[WARN] DaVinci export is on, but PATHS → davinci_exe_path is empty or missing — start Resolve Studio manually.\n",
        'davinci_autostart_exe_missing': "[WARN] Resolve.exe not found (check PATHS → davinci_exe_path):\n{path}\n",
        'davinci_autostart_skip_os': "[INFO] Auto-starting Resolve is only implemented on Windows (same as watcher tasklist); start Resolve manually if needed.\n",
        'sync_warn_framecount': (
            "[WARN] OpenCV frame count differs — source: {src}, deepfake: {df}. "
            "Compare always pairs by frame index (0,1,2,…); decoding stops when either file ends. "
            "Different trims, re-encodes, or duplicate frames can look like \"differences\" especially toward the end.\n"
        ),
        'sync_warn_duration': (
            "[WARN] Container duration (ffprobe) differs — source: {src:.3f}s, deepfake: {df:.3f}s (Δ{delta:.3f}s). "
            "If lengths do not match, index pairing is not guaranteed to align with wall-clock time.\n"
        ),
        'sync_warn_fps': (
            "[WARN] OpenCV FPS differs — source: {src:.6g}, deepfake: {df:.6g}. "
            "Analysis FPS is taken from the source file only; the other track may drift perceptually vs. indices.\n"
        ),
        'sync_warn_truncated': (
            "[WARN] Decoding stopped after {got} frame pairs; source container reported {expected}. "
            "Usually the shorter or harder-to-decode file ended first — check that both exports cover the same range.\n"
        ),
        'sync_info_no_ffprobe_duration': "[INFO] ffprobe not found — skipping container duration cross-check (place ffprobe.exe next to the app or on PATH).\n",
    },
    'de': {
        'unexpected_error': "\nEin unerwarteter Fehler ist aufgetreten: {error}",
        'usage': "Aufruf: compare.py ORIGINAL DEEPFAKE [--auto] [--retry-export-only|--defer-davinci|--davinci-export-only]",
        'set_mode': "- Analyse-Modus: {val}",
        'mode_classic': "klassisch (geänderte Pixel, volle Auflösung)",
        'mode_blob': "Blob (größter geänderter Bereich, Analysebreite {w}px, Blur {blur})",
        'set_postfilter': "- Nachfilter: min. Unterschied {run} Fr. | min. behaltenes Stück {keep} Fr. | Schnitt-Polster {pad} Fr.",
        'set_sync': "- Auto-Versatz-Erkennung: {val}",
        'sync_applied': "[INFO] Frame-Versatz erkannt: Deepfake-Frame k = Original-Frame k{sign}{n} (Wert {s0:.2f} → {sb:.2f}). Überspringe {n} Frame(s) am Anfang vom {which}.\n",
        'sync_none': "[INFO] Kein Frame-Versatz zwischen den Videos (geprüft ±{n} Frames).\n",
        'which_source': "Original",
        'which_deepfake': "Deepfake",
        'reader_info': "[INFO] Decoder: {reader} | Analysegröße {w}x{h}\n",
        'metrics_reused': "[INFO] Gespeicherte Analyse wird wiederverwendet ({frames} Frames) — nur die Filter werden neu angewendet, kein Dekodieren.\n",
        'metrics_saved': "[INFO] Messwerte pro Frame gespeichert: {path}\n",
        'analysis_time': "[INFO] Analyse dauerte {sec:.1f}s ({fps:.0f} Frame-Paare/s).\n",
        'cut_summary': "[INFO] Ergebnis: {kept:.1f}s behalten, {cut:.1f}s geschnitten in {n} Stück(en).\n",
        'no_swap_warning': "[WARN] Kein einziger Frame liegt über der Schwelle — ist das wirklich das passende Original/Deepfake-Paar? (Es wird nichts geschnitten.)\n",
        'all_swap_warning': "[WARN] Jeder analysierte Frame weicht ab — falsches Paar, anderer Schnitt oder globale Farb-/Größenänderung? Vorschau prüfen; „Pixel max“ kann globale Änderungen ignorieren.\n",
        'retry_refilter': "[INFO] Erneut: aktuelle Filter werden auf die gespeicherte Analyse angewendet ({frames} Frames).\n",
        'davinci_res_fallback': "[INFO] Bildgröße für Resolve von OpenCV gelesen: {w}x{h}\n",
        'davinci_res_default': "[WARN] Bildgröße des Deepfakes nicht lesbar; Resolve-Timeline nutzt 1920x1080.\n",
        'ffmpeg_no_audio': "[INFO] {name} hat keine Tonspur — es wird nur Video exportiert.\n",
        'ffmpeg_retry_no_audio': "[INFO] FFmpeg mit Ton fehlgeschlagen; neuer Versuch ohne Tonspur...\n",
        'loading_settings': "Lade Einstellungen aus settings.ini:",
        'set_buffer': "- Puffer: {val} Sekunden",
        'set_noise': "- Rausch-Filter: {val}",
        'set_pixel': "- Pixel-Toleranz: {val}",
        'set_pixel_max': "- Pixel-Obergrenze (0=aus): {val}",
        'set_davinci': "- DaVinci API Export: {val}",
        'set_davinci_timeout': "- DaVinci Render-Zeitlimit: {val}",
        'davinci_timeout_off': "keins (warten bis DaVinci fertig ist)",
        'set_ffmpeg': "- FFmpeg Export: {val}",
        'set_ffmpeg_target': "- FFmpeg rendert: {val}",
        'ffmpeg_target_both': "Original und Deepfake",
        'ffmpeg_target_source': "nur Original",
        'ffmpeg_target_deepfake': "nur Deepfake",
        'set_fullcheck': "- FullCheck EDL: {val}",
        'set_export_unique': "- Video-Exporte nicht überschreiben (FFmpeg / DaVinci): {val}",
        'on': "An",
        'off': "Aus",
        'start_analysis': "\nStarte Pixel-Differenz-Analyse für {frames} Frames...",
        'analyzing': "\rAnalysiere: {percent:.1f}% ({current}/{total} Frames)",
        'frames_processed': "\n{count} Frames verarbeitet. Wende Filter an...",
        'no_diff': "Kein Unterschied zwischen den Videos gefunden.",
        'edl_auto_created': "\nAutoDelete EDL Dateien erstellt:",
        'edl_full_created': "\nFullCheck EDL Dateien erstellt.",
        'all_done': "\nAlle Vorgänge abgeschlossen.",
        'compare_exit_partial': (
            "\n[INFO] Mindestens ein aktivierter Export (FFmpeg und/oder DaVinci) ist fehlgeschlagen. "
            "Exit-Code 3 — im Oxco-Tab „Filter“ anpassen und „Erneut“ nutzen.\n"
        ),
        'checkpoint_written': "[INFO] Export-Checkpoint gespeichert („Erneut“ = nur Export, keine neue Pixelanalyse).\n",
        'checkpoint_write_failed': "[WARN] Checkpoint konnte nicht geschrieben werden: {error}\n",
        'retry_export_start': "[INFO] Erneut: nur Export (EDL / FFmpeg / DaVinci) — gleicher Schnitt wie letzte Analyse.\n",
        'retry_export_no_checkpoint': "[FEHLER] Kein Export-Checkpoint. Zuerst vollständigen Compare starten (Analyse muss Unterschiede finden).\n",
        'retry_export_ck_read_err': "[FEHLER] Export-Checkpoint nicht lesbar: {error}\n",
        'retry_export_path_mismatch': "[FEHLER] Checkpoint passt nicht zu diesen beiden Videopfaden. Compare erneut vollständig ausführen.\n",
        'retry_export_missing_file': "[FEHLER] Original- oder Deepfake-Datei fehlt auf der Platte.\n",
        'retry_export_auto_only': "[FEHLER] --retry-export-only nur mit zwei Pfaden und --auto (Oxco „Erneut“).\n",
        'ffmpeg_render': "Rendere {name} über FFmpeg ({codec})...",
        'ffmpeg_progress': "\rFortschritt: {percent:.1f}% ({frame}/{total} Frames)",
        'ffmpeg_error': "\n\n--- FFmpeg Fehler bei {name} ---",
        'ffmpeg_success': "\n-> Erfolgreich erstellt.",
        'davinci_api_missing': "\n[FEHLER] DaVinci API (DaVinciResolveScript) nicht gefunden!",
        'davinci_api_path_searched': "Python hat explizit in diesem Ordner gesucht:\n{path}",
        'davinci_api_check_explorer': "Bitte prüfe im Windows Explorer, ob dieser Ordner und die Datei 'DaVinciResolveScript.py' dort wirklich existieren.",
        'davinci_not_open': "\n[FEHLER] DaVinci Resolve Studio muss geöffnet sein, bevor dieses Skript startet!",
        'davinci_api_retry': "[INFO] DaVinci-Scripting-API noch nicht bereit; erneuter Versuch ({n}/{max}, Pause {delay}s)...\n",
        'davinci_scriptapp_stopped': "[FEHLER] DaVinci Resolve wurde während des API-Wartens beendet.\n",
        'davinci_scriptapp_hint': (
            "[INFO] Wenn das lange so bleibt: **Resolve Studio** nutzen (kein externes Scripting in der Gratis-Version), "
            "ein Projekt oeffnen, Resolve und dieses Tool unter dem **gleichen** Windows-Benutzer starten "
            "(nicht nur eines \"als Administrator\"). In settings.ini kannst du "
            "davinci_scriptapp_retry_attempts / davinci_scriptapp_retry_delay_seconds erhoehen.\n"
        ),
        'davinci_fallback_skip': "\n[WARN] DaVinci Resolve laeuft nicht oder die Scripting-API ist nicht verfuegbar.\nPixel-Analyse, EDL und FFmpeg (falls aktiv) laufen trotzdem; DaVinci-Export wird uebersprungen.\n",
        'davinci_export_exception': "\n[WARN] DaVinci-Export fehlgeschlagen: {error}\n[INFO] Pixel-Analyse und bereits geschriebene EDL-/FFmpeg-Dateien bleiben gueltig.\n",
        'davinci_export_interrupted': "\n[INFO] DaVinci-Export unterbrochen oder API-Fehler bei Resolve: {error}\n[INFO] Pixel-Analyse und EDL-/FFmpeg-Ausgabe oben bleiben gueltig.\n",
        'davinci_render_no_jobs': "\n[INFO] DaVinci lieferte keine Render-Job-Infos (Resolve evtl. waehrend des Exports beendet).\n",
        'compare_summary_davinci_ok': "[INFO] DaVinci-Export: erfolgreich abgeschlossen.\n",
        'compare_summary_davinci_fail': "[INFO] DaVinci-Export: nicht abgeschlossen (Resolve geschlossen, API-Fehler oder Timeout). Analyse, EDL und FFmpeg (falls aktiv) oben bleiben unveraendert.\n",
        'davinci_no_project': "\n[FEHLER] Kein offenes Projekt in DaVinci Resolve gefunden.",
        'davinci_no_project_wait': "[INFO] Noch kein offenes Resolve-Projekt (häufig direkt nach Neustart). Wiederhole ({n}/{max})...\n",
        'davinci_no_project_hint': "[INFO] Tipp: In Resolve ein Projekt öffnen oder anlegen, danach Compare erneut starten.\n",
        'davinci_export_start_info': "[INFO] Starte DaVinci-Export (API-Pfad: {path})\n",
        'davinci_script_missing': "[WARN] DaVinciResolveScript.py nicht gefunden:\n{path}\nBitte PATHS → davinci_api_path in settings.ini prüfen (Studio-Ordner \"Modules\").\n",
        'davinci_sending_data': "\nSende Daten über API an DaVinci Resolve Studio...",
        'davinci_import_error': "[FEHLER] Konnte {path} nicht in Resolve importieren.",
        'davinci_timeline_error': "[FEHLER] Konnte keine Timeline erstellen.",
        'davinci_no_valid_scenes': "Keine validen Szenen zum Rendern gefunden.",
        'davinci_preset_warning': "\n[WARNUNG] Konnte Render-Preset '{preset}' in DaVinci nicht finden!",
        'davinci_preset_fallback': "Es wird mit den zuletzt in DaVinci genutzten Einstellungen gerendert.",
        'davinci_job_created': "Render-Job für '{name}' erstellt.",
        'davinci_start_render': "Starte Hardware-Rendering in DaVinci Resolve...",
        'davinci_render_running': "-> Rendering läuft! (Der Fortschritt wird dir jetzt drüben in der DaVinci Resolve GUI angezeigt)",
        'davinci_meta_no_resolution': "Keine Auflösung in den Clip-Metadaten.",
        'davinci_meta_parse_error': "[Info] Clip-Metadaten nicht vollständig lesbar: {error}",
        'davinci_render_timeout_exceeded': "DaVinci-Rendering länger als {sec} Sekunden; Export wurde gestoppt.",
        'davinci_render_status_failed': "DaVinci-Render abgeschlossen mit Status: {status}",
        'fps_fallback': "[Info] Keine gültige FPS aus dem Quellvideo lesbar; verwende 30.",
        'fps_ffprobe': "[INFO] Stream-FPS {fps} von ffprobe fuer Analyse und DaVinci-Timeline (OpenCV meldete {opencv}).\n",
        'fps_ffprobe_only': "[INFO] Stream-FPS {fps} von ffprobe (OpenCV hatte keine gueltige Rate).\n",
        'fps_r_vs_avg': "[INFO] ffprobe r_frame_rate={r:.6g} vs avg_frame_rate={avg:.6g} — nutze nominales r_frame_rate (avg oft falsch bei NTSC-MP4).\n",
        'fps_prefer_opencv': "[INFO] ffprobe avg_frame_rate={avg:.6g} passt nicht zu OpenCV {ocv:.6g} — nutze OpenCV (typisch bei falsch gemuxtem avg).\n",
        'davinci_timeline_fps': "[INFO] DaVinci-Timeline: {rate} (wie Compare-Analyse; Resolve-Clip-Metadaten: {clip}).\n",
        'davinci_set_failed': "[WARN] DaVinci-API lehnt {key}={val!r} auf {where} ab (Timeline behält evtl. alte FPS).\n",
        'davinci_fps_verify': "[INFO] DaVinci FPS zur Kontrolle — Projekt (Master/Dropdown): {proj} | Timeline-Objekt: {tl}\n",
        'davinci_master_preset': "[INFO] Resolve Master nach Vorab-Preset: timelineFrameRate (read-back)={fps} | {w}x{h}\n",
        'davinci_fps_locked_hint': "[WARN] Master-Timeline-FPS bleibt {read} (Ziel {target}). Resolve sperrt die FPS oft, sobald der Media Pool schon Clips hat — leerer Pool, neues Projekt oder passende Projektvorlage.\n",
        'davinci_temp_project': "[INFO] Temporäres Resolve-Projekt '{temp}' für diesen Export (Master-FPS ohne bestehende Timelines). Danach wird '{orig}' wieder geöffnet.\n",
        'davinci_temp_project_failed': "[WARN] Temporäres Resolve-Projekt nicht erstellbar; es wird das aktuelle Projekt genutzt — Master-FPS kann gesperrt bleiben, wenn schon Timelines/Clips existieren.\n",
        'davinci_restored_project': "[INFO] Resolve-Projekt '{name}' wieder geöffnet.\n",
        'davinci_restore_failed': "[WARN] Zurück zu Resolve-Projekt '{name}' nicht möglich: {error}\n",
        'davinci_render_never_started': "[WARN] Kein laufender Render-Meldung innerhalb {sec}s (sonst bricht das Skript zu früh ab — Datei unvollständig). Deliver prüfen; Export wiederholen.\n",
        'davinci_autostart_already': "[INFO] DaVinci Resolve läuft bereits.\n",
        'davinci_autostart_start': "[INFO] DaVinci Resolve läuft nicht. Starte Programm (PATHS → davinci_exe_path)...\n",
        'davinci_autostart_wait': "[INFO] Warte {sec} Sekunden auf Resolve (SETTINGS → davinci_startup_wait_seconds)...\n",
        'davinci_autostart_err': "[FEHLER] DaVinci Resolve konnte nicht gestartet werden: {e}\n",
        'davinci_autostart_path': "[INFO] Verwendeter Pfad: {path}\n",
        'davinci_autostart_no_exe': "[WARN] DaVinci-Export ist an, aber PATHS → davinci_exe_path fehlt — bitte Resolve Studio manuell starten.\n",
        'davinci_autostart_exe_missing': "[WARN] Resolve.exe nicht gefunden (PATHS → davinci_exe_path prüfen):\n{path}\n",
        'davinci_autostart_skip_os': "[INFO] Auto-Start von Resolve nur unter Windows (wie watcher tasklist); ggf. Resolve manuell starten.\n",
        'sync_warn_framecount': (
            "[WARN] OpenCV-Frameanzahl weicht ab — Original: {src}, Deepfake: {df}. "
            "Compare koppelt immer nach Frame-Index (0,1,2,…); das Dekodieren endet, sobald eine Datei zu Ende ist. "
            "Unterschiedliche Schnitte, Re-Encodes oder doppelte Frames wirken dann oft wie „Unterschiede“, besonders gegen Ende.\n"
        ),
        'sync_warn_duration': (
            "[WARN] Container-Laufzeit (ffprobe) weicht ab — Original: {src:.3f}s, Deepfake: {df:.3f}s (Δ{delta:.3f}s). "
            "Bei unterschiedlicher Länge stimmt die Index-Kopplung nicht zwingend mit der Echtzeit überein.\n"
        ),
        'sync_warn_fps': (
            "[WARN] OpenCV-FPS weicht ab — Original: {src:.6g}, Deepfake: {df:.6g}. "
            "Die Analyse-FPS kommt nur aus dem Original; die andere Spur kann sich zur Index-Zählung anders anfühlen.\n"
        ),
        'sync_warn_truncated': (
            "[WARN] Dekodierung endete nach {got} Frame-Paaren; der Container meldete fürs Original {expected}. "
            "Typisch: kürzeres Deepfake-Export, Lesefehler oder anderer In/Out-Point — prüfen, ob beide Clips dieselbe Spanne haben.\n"
        ),
        'sync_info_no_ffprobe_duration': "[INFO] ffprobe nicht gefunden — Container-Laufzeit wird nicht verglichen (ffprobe.exe neben die App oder im PATH).\n",
    }
}

def get_base_dir():
    if getattr(sys, 'frozen', False):
        return os.path.dirname(sys.executable)
    return os.path.dirname(os.path.abspath(__file__))


def _read_ini_file(config, path):
    """Read settings.ini as UTF-8 (matches Control Center). Fallback: default ConfigParser.read."""
    try:
        with open(path, "r", encoding="utf-8-sig") as fh:
            config.read_file(fh)
    except Exception:
        config.read(path)


def _ini_bool(config, section, option, fallback=False):
    """ConfigParser boolean, tolerant of odd editor values."""
    if not config.has_option(section, option):
        return fallback
    try:
        return config.getboolean(section, option)
    except ValueError:
        v = config.get(section, option, fallback="").strip().lower()
        return v in ("1", "yes", "true", "on", "ja")


def _ini_int_clamped(config, section, option, lo, hi, fallback):
    try:
        return max(lo, min(hi, config.getint(section, option, fallback=fallback)))
    except (ValueError, TypeError):
        return fallback


def _ini_float_clamped(config, section, option, lo, hi, fallback):
    try:
        return max(lo, min(hi, config.getfloat(section, option, fallback=fallback)))
    except (ValueError, TypeError):
        return fallback


def _is_resolve_running_win():
    """Windows: same check as Deepfake_smoother_premium watcher.py (tasklist)."""
    if sys.platform != "win32":
        return False
    try:
        kw = {}
        if hasattr(subprocess, "CREATE_NO_WINDOW"):
            kw["creationflags"] = subprocess.CREATE_NO_WINDOW
        output = subprocess.check_output(
            ["tasklist", "/FI", "IMAGENAME eq Resolve.exe", "/FO", "CSV", "/NH"],
            shell=False,
            **kw,
        ).decode("utf-8", errors="ignore")
        return "Resolve.exe" in output
    except Exception:
        return False


def ensure_resolve_running_for_compare(davinci_exe_path, startup_wait_sec, lang):
    """
    If Resolve is not running, start davinci_exe_path (Windows: os.startfile like watcher.py),
    then sleep startup_wait_sec (SETTINGS.davinci_startup_wait_seconds).
    """
    if sys.platform != "win32":
        print(TEXTS[lang]["davinci_autostart_skip_os"], flush=True)
        return
    if _is_resolve_running_win():
        print(TEXTS[lang]["davinci_autostart_already"], flush=True)
        return
    exe = (davinci_exe_path or "").strip()
    if not exe:
        print(TEXTS[lang]["davinci_autostart_no_exe"], flush=True)
        return
    if not os.path.isfile(exe):
        print(TEXTS[lang]["davinci_autostart_exe_missing"].format(path=exe), flush=True)
        return
    print(TEXTS[lang]["davinci_autostart_start"], flush=True)
    try:
        os.startfile(exe)  # type: ignore[attr-defined]
    except Exception as e:
        print(TEXTS[lang]["davinci_autostart_err"].format(e=e), flush=True)
        print(TEXTS[lang]["davinci_autostart_path"].format(path=exe), flush=True)
        return
    wait_sec = max(0, min(600, int(startup_wait_sec)))
    if wait_sec > 0:
        print(TEXTS[lang]["davinci_autostart_wait"].format(sec=wait_sec), flush=True)
        time.sleep(wait_sec)


def _eval_fraction_fps(s):
    s = str(s).strip()
    if not s or s == "0/0":
        return None
    if "/" in s:
        parts = s.split("/", 1)
        try:
            num, den = float(parts[0]), float(parts[1])
            if den == 0:
                return None
            return num / den
        except (ValueError, TypeError):
            return None
    try:
        return float(s)
    except ValueError:
        return None


_STANDARD_CFR_ANCHORS = (
    23.976023976023978,
    24.0,
    25.0,
    29.97002997002997,
    30.0,
    48.0,
    50.0,
    59.94005994005994,
    60.0,
)


def _near_standard_cfr(fps, tol=0.06):
    if fps is None or fps <= 0:
        return False
    try:
        f = float(fps)
    except (TypeError, ValueError):
        return False
    for v in _STANDARD_CFR_ANCHORS:
        if abs(f - v) < tol:
            return True
    return False


def probe_video_stream_fps_rates(ffprobe_exe, video_path):
    """
    Returns (r_frame_rate_fps, avg_frame_rate_fps) — each may be None.
    For 29.97 NTSC, r_frame_rate is usually 30000/1001; avg_frame_rate is often mis-reported as ~24.
    """
    if not ffprobe_exe or not video_path or not os.path.isfile(video_path):
        return None, None
    try:
        kw = {}
        if hasattr(subprocess, "CREATE_NO_WINDOW"):
            kw["creationflags"] = subprocess.CREATE_NO_WINDOW
        completed = subprocess.run(
            [
                ffprobe_exe,
                "-v",
                "error",
                "-select_streams",
                "v:0",
                "-show_entries",
                "stream=avg_frame_rate,r_frame_rate",
                "-of",
                "json",
                video_path,
            ],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=90,
            **kw,
        )
    except (subprocess.SubprocessError, OSError):
        return None, None
    if completed.returncode != 0:
        return None, None
    try:
        payload = json.loads(completed.stdout or "{}")
    except json.JSONDecodeError:
        return None, None
    streams = payload.get("streams") or []
    if not streams:
        return None, None
    st = streams[0]
    r_fps = _eval_fraction_fps(st.get("r_frame_rate")) if st.get("r_frame_rate") else None
    a_fps = _eval_fraction_fps(st.get("avg_frame_rate")) if st.get("avg_frame_rate") else None
    if r_fps is not None and r_fps <= 0:
        r_fps = None
    if a_fps is not None and a_fps <= 0:
        a_fps = None
    return r_fps, a_fps


def probe_video_wh_ffprobe(ffprobe_exe, video_path):
    """Returns (width, height) for first video stream, or (None, None)."""
    if not ffprobe_exe or not video_path or not os.path.isfile(video_path):
        return None, None
    try:
        kw = {}
        if hasattr(subprocess, "CREATE_NO_WINDOW"):
            kw["creationflags"] = subprocess.CREATE_NO_WINDOW
        completed = subprocess.run(
            [
                ffprobe_exe,
                "-v",
                "error",
                "-select_streams",
                "v:0",
                "-show_entries",
                "stream=width,height",
                "-of",
                "json",
                video_path,
            ],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=90,
            **kw,
        )
    except (subprocess.SubprocessError, OSError):
        return None, None
    if completed.returncode != 0:
        return None, None
    try:
        payload = json.loads(completed.stdout or "{}")
    except json.JSONDecodeError:
        return None, None
    streams = payload.get("streams") or []
    if not streams:
        return None, None
    st = streams[0]
    w, h = st.get("width"), st.get("height")
    if w is None or h is None:
        return None, None
    try:
        return int(w), int(h)
    except (TypeError, ValueError):
        return None, None


def probe_format_duration_seconds(ffprobe_exe, video_path):
    """Container duration in seconds from ffprobe (format), or None."""
    if not ffprobe_exe or not video_path or not os.path.isfile(video_path):
        return None
    try:
        kw = {}
        if hasattr(subprocess, "CREATE_NO_WINDOW"):
            kw["creationflags"] = subprocess.CREATE_NO_WINDOW
        completed = subprocess.run(
            [
                ffprobe_exe,
                "-v",
                "error",
                "-show_entries",
                "format=duration",
                "-of",
                "json",
                video_path,
            ],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=90,
            **kw,
        )
    except (subprocess.SubprocessError, OSError):
        return None
    if completed.returncode != 0:
        return None
    try:
        payload = json.loads(completed.stdout or "{}")
    except json.JSONDecodeError:
        return None
    fmt = payload.get("format") or {}
    d = fmt.get("duration")
    try:
        v = float(d)
    except (TypeError, ValueError):
        return None
    if v <= 0:
        return None
    return v


def print_stream_sync_warnings(lang, ffprobe_exe, source_video, deepfake_video, cap_src, cap_df):
    """Cheap sanity checks before index-locked frame comparison (no auto re-sync)."""
    try:
        fc_s = int(cap_src.get(cv2.CAP_PROP_FRAME_COUNT))
    except (TypeError, ValueError):
        fc_s = 0
    try:
        fc_d = int(cap_df.get(cv2.CAP_PROP_FRAME_COUNT))
    except (TypeError, ValueError):
        fc_d = 0
    if fc_s > 0 and fc_d > 0 and fc_s != fc_d:
        print(TEXTS[lang]["sync_warn_framecount"].format(src=fc_s, df=fc_d), flush=True)

    try:
        fps_s = float(cap_src.get(cv2.CAP_PROP_FPS))
    except (TypeError, ValueError):
        fps_s = 0.0
    try:
        fps_d = float(cap_df.get(cv2.CAP_PROP_FPS))
    except (TypeError, ValueError):
        fps_d = 0.0
    if fps_s > 0.01 and fps_d > 0.01 and abs(fps_s - fps_d) >= 0.2:
        print(TEXTS[lang]["sync_warn_fps"].format(src=fps_s, df=fps_d), flush=True)

    if ffprobe_exe:
        ds = probe_format_duration_seconds(ffprobe_exe, source_video)
        dd = probe_format_duration_seconds(ffprobe_exe, deepfake_video)
        if ds is not None and dd is not None:
            delta = abs(ds - dd)
            if delta >= 0.25:
                print(TEXTS[lang]["sync_warn_duration"].format(src=ds, df=dd, delta=delta), flush=True)
    else:
        print(TEXTS[lang]["sync_info_no_ffprobe_duration"], flush=True)


def pick_analysis_fps_from_probes(opencv_fps, r_fps, avg_fps, lang):
    """
    Prefer ffprobe r_frame_rate (nominal CFR). If only avg disagrees with OpenCV on a standard rate, trust OpenCV.
    """
    try:
        of = float(opencv_fps) if opencv_fps and float(opencv_fps) > 0 else None
    except (TypeError, ValueError):
        of = None
    rf = float(r_fps) if r_fps and float(r_fps) > 0 else None
    af = float(avg_fps) if avg_fps and float(avg_fps) > 0 else None

    if rf:
        if af and abs(rf - af) > 0.5:
            print(TEXTS[lang]["fps_r_vs_avg"].format(r=rf, avg=af), flush=True)
        return rf
    if af and of and abs(af - of) >= 0.5 and _near_standard_cfr(of):
        print(TEXTS[lang]["fps_prefer_opencv"].format(avg=af, ocv=of), flush=True)
        return of
    if af:
        return af
    if of:
        return of
    return None


def _davinci_set(project_or_timeline, key, val, where, lang):
    """Log if Resolve rejects a setting (returns False)."""
    try:
        ok = project_or_timeline.SetSetting(key, str(val))
    except Exception as e:
        print(f"[WARN] DaVinci SetSetting({key}) raised: {e}\n", flush=True)
        return False
    if ok is False:
        print(
            TEXTS[lang]["davinci_set_failed"].format(key=key, val=str(val), where=where),
            flush=True,
        )
    return ok is not False


def _timeline_fps_string_candidates(timeline_rate: str, fps_float: float):
    """Resolve is picky: Master dropdown vs timeline may accept '25' vs '25.0'. Try several."""
    try:
        f = float(fps_float)
    except (TypeError, ValueError):
        f = 0.0
    out = []
    seen = set()

    def add(s):
        if s and s not in seen:
            seen.add(s)
            out.append(str(s))

    add(timeline_rate)
    add(format_timeline_framerate_for_resolve(f))
    if f > 0:
        raw = f"{f:.6f}".rstrip("0").rstrip(".")
        add(raw)
        if abs(f - round(f)) < 0.06:
            ri = int(round(f))
            add(str(ri))
            add(f"{ri}.0")
    return out


def _davinci_set_project_master_before_import(project, timeline_rate, analysis_fps, width, height, lang):
    """
    Set Master timeline FPS + resolution on the Project before ImportMedia.
    Resolve often locks Master FPS to the first clip in the pool; doing this first avoids 23.976 lock on 25 fps exports.
    """
    candidates = _timeline_fps_string_candidates(timeline_rate, analysis_fps)
    chosen = None
    for cand in candidates:
        try:
            rp = project.SetSetting("timelineFrameRate", cand)
        except Exception:
            continue
        if rp is False:
            continue
        chosen = cand
        break
    if chosen is None:
        chosen = candidates[0]
        _davinci_set(project, "timelineFrameRate", chosen, "project", lang)
    _davinci_set(project, "timelineResolutionWidth", str(width), "project", lang)
    _davinci_set(project, "timelineResolutionHeight", str(height), "project", lang)
    try:
        gp = project.GetSetting("timelineFrameRate")
        print(
            TEXTS[lang]["davinci_master_preset"].format(fps=gp, w=width, h=height),
            flush=True,
        )
        try:
            gpf = float(str(gp).replace(",", "."))
            want = float(analysis_fps)
            if abs(gpf - want) > 0.5:
                print(
                    TEXTS[lang]["davinci_fps_locked_hint"].format(read=gp, target=timeline_rate),
                    flush=True,
                )
        except (TypeError, ValueError):
            pass
    except Exception:
        pass


def _davinci_sync_active_timeline_fps(project, timeline, timeline_rate, analysis_fps, lang, skip_if_close=False):
    """
    Match the active Timeline object's frame rate to analysis (Timeline:SetSetting).
    Timeline resolution is not set here — Master resolution on Project is enough for new timelines; some Resolve builds reject Timeline width/height via API.
    """
    if skip_if_close:
        try:
            gt = timeline.GetSetting("timelineFrameRate")
            g = float(str(gt).replace(",", "."))
            want = float(analysis_fps)
            if abs(g - want) < 0.06:
                gp = project.GetSetting("timelineFrameRate")
                print(TEXTS[lang]["davinci_fps_verify"].format(proj=gp, tl=gt), flush=True)
                return
        except Exception:
            pass
    candidates = _timeline_fps_string_candidates(timeline_rate, analysis_fps)
    chosen = None
    for cand in candidates:
        try:
            rt = timeline.SetSetting("timelineFrameRate", cand)
        except Exception:
            continue
        if rt is False:
            continue
        chosen = cand
        break
    if chosen is None:
        chosen = candidates[0]
        _davinci_set(timeline, "timelineFrameRate", chosen, "timeline", lang)
    try:
        gp = project.GetSetting("timelineFrameRate")
        gt = timeline.GetSetting("timelineFrameRate")
        print(TEXTS[lang]["davinci_fps_verify"].format(proj=gp, tl=gt), flush=True)
    except Exception:
        pass


def _connect_resolve_scriptapp(dvr_script, lang, scriptapp_retry_attempts, scriptapp_retry_delay_sec):
    """
    scriptapp(\"Resolve\") is often None until Resolve Studio finishes init.
    Stop early if Resolve.exe is not running (avoids minutes of pointless retries).
    """
    max_attempts = max(1, int(scriptapp_retry_attempts))
    delay = max(0.5, float(scriptapp_retry_delay_sec))
    resolve = None
    for attempt in range(1, max_attempts + 1):
        try:
            resolve = dvr_script.scriptapp("Resolve")
        except Exception:
            resolve = None
        if resolve:
            return resolve
        if attempt == 1:
            print(TEXTS[lang]["davinci_scriptapp_hint"], flush=True)
        if sys.platform == "win32" and not _is_resolve_running_win():
            print(TEXTS[lang]["davinci_scriptapp_stopped"], flush=True)
            return None
        if attempt < max_attempts:
            if attempt == 1 or attempt % 5 == 0:
                print(
                    TEXTS[lang]["davinci_api_retry"].format(
                        n=attempt,
                        max=max_attempts,
                        delay=delay,
                    ),
                    flush=True,
                )
            time.sleep(delay)
    print(TEXTS[lang]["davinci_not_open"], flush=True)
    return None


def _wait_for_resolve_project(project_manager, lang, max_project_wait=40, project_wait_delay=3.0):
    project = None
    for attempt in range(1, max_project_wait + 1):
        try:
            project = project_manager.GetCurrentProject()
        except Exception:
            project = None
        if project:
            return project
        if sys.platform == "win32" and not _is_resolve_running_win():
            print(TEXTS[lang]["davinci_scriptapp_stopped"], flush=True)
            return None
        if attempt < max_project_wait:
            if attempt == 1 or attempt % 5 == 0:
                print(
                    TEXTS[lang]["davinci_no_project_wait"].format(
                        n=attempt, max=max_project_wait
                    ),
                    flush=True,
                )
            time.sleep(project_wait_delay)
    print(TEXTS[lang]["davinci_no_project"])
    print(TEXTS[lang]["davinci_no_project_hint"])
    return None


def _davinci_delete_all_timelines(project, mediaPool):
    """Resolve makes Project timelineFrameRate read-only while timelines exist; strip defaults on a fresh project."""
    try:
        n = int(project.GetTimelineCount())
    except (TypeError, ValueError, AttributeError, Exception):
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
    if not timelines:
        return
    try:
        mediaPool.DeleteTimelines(timelines)
    except Exception:
        pass


def _davinci_try_create_export_project(project_manager, lang):
    """Returns (project, name) or (None, None)."""
    for _ in range(12):
        name = (
            f"AutoCut_Export_{datetime.datetime.now().strftime('%Y%m%d_%H%M%S')}_"
            f"{random.randint(100000, 999999)}"
        )
        try:
            p = project_manager.CreateProject(name)
        except Exception:
            p = None
        if p:
            return p, name
    print(TEXTS[lang]["davinci_temp_project_failed"], flush=True)
    return None, None


def _davinci_restore_user_project(project_manager, orig_name, did_switch, lang):
    if not did_switch or not orig_name:
        return
    try:
        project_manager.LoadProject(orig_name)
        print(TEXTS[lang]["davinci_restored_project"].format(name=orig_name), flush=True)
    except Exception as e:
        print(
            TEXTS[lang]["davinci_restore_failed"].format(name=orig_name, error=e),
            flush=True,
        )


def _davinci_wait_for_render_idle(project, lang, davinci_render_timeout_seconds, start_time):
    """
    IsRenderingInProgress() is often False for a short time right after StartRendering().
    If we treat that as 'done', the script returns while encoding is still running — output files are truncated.
    Wait until we have either seen active rendering and then idle, or job status is already Complete, or timeout.
    """
    rendering_seen = False
    no_start_deadline = start_time + 120.0
    if davinci_render_timeout_seconds > 0:
        absolute_deadline = start_time + float(davinci_render_timeout_seconds)
    else:
        # settings.ini uses 0 for "no limit" — still cap to avoid an infinite poll loop
        absolute_deadline = start_time + 7200.0
    while True:
        now = time.time()
        if now > absolute_deadline:
            try:
                project.StopRendering()
            except Exception:
                pass
            time.sleep(2)
            if davinci_render_timeout_seconds > 0:
                print(
                    "\n[INFO] "
                    + TEXTS[lang]["davinci_render_timeout_exceeded"].format(
                        sec=davinci_render_timeout_seconds
                    )
                    + "\n",
                    flush=True,
                )
            else:
                print(
                    "\n[WARN] DaVinci render wait safety limit (7200s) exceeded.\n",
                    flush=True,
                )
            return False
        try:
            busy = project.IsRenderingInProgress()
        except Exception as poll_err:
            print(TEXTS[lang]['davinci_export_interrupted'].format(error=poll_err), flush=True)
            return False
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
                render_jobs = project.GetRenderJobList()
                if render_jobs:
                    job_id = render_jobs[-1].get("JobId")
                    if job_id:
                        st = project.GetRenderJobStatus(job_id)
                        status = (st or {}).get("JobStatus", "Unknown")
                        if status in ("Complete", "Abgeschlossen"):
                            time.sleep(2.0)
                            return True
                        s = str(status).lower()
                        if "fail" in s or "cancel" in s or "abort" in s:
                            print(
                                TEXTS[lang]['davinci_render_status_failed'].format(
                                    status=status
                                ),
                                flush=True,
                            )
                            return False
            except Exception:
                pass
            if now > no_start_deadline:
                print(
                    TEXTS[lang]['davinci_render_never_started'].format(sec=120),
                    flush=True,
                )
                return False
        time.sleep(0.4 if not rendering_seen else 5.0)


def _davinci_run_export_pipeline(
    project,
    mediaPool,
    deepfake_video,
    all_seqs,
    lang,
    target_dir,
    davinci_render_timeout_seconds,
    analysis_fps,
    timeline_rate,
    width,
    height,
    abs_video_path,
    settings,
):
    """Import, build timeline, render — assumes project/mediaPool are the export target."""
    # Before ImportMedia: avoid Resolve locking Master FPS to the clip's tagged rate (e.g. 23.976 on a 25 fps cut list).
    _davinci_set_project_master_before_import(
        project, timeline_rate, analysis_fps, width, height, lang
    )

    imported_items = mediaPool.ImportMedia([abs_video_path])

    if not imported_items:
        print(TEXTS[lang]['davinci_import_error'].format(path=abs_video_path))
        return False

    df_item = imported_items[0]

    clip_props = df_item.GetClipProperty() or {}
    clip_fps_meta = clip_props.get("FPS")

    # Deepfake export files are often tagged 23.976 while source is 29.97 — override clip interpretation.
    try:
        clip_pv = format_clip_fps_property_for_resolve(analysis_fps)
        for val in (timeline_rate, clip_pv):
            try:
                if df_item.SetClipProperty("FPS", val) is not False:
                    break
            except Exception:
                continue
    except Exception:
        pass

    base_name = os.path.splitext(os.path.basename(deepfake_video))[0]
    custom_render_name = pick_unique_davinci_custom_name(base_name, target_dir, settings)
    timestamp = datetime.datetime.now().strftime("%H%M%S")
    timeline_name = f"AutoCut_{base_name}_{timestamp}"
    timeline = mediaPool.CreateEmptyTimeline(timeline_name)

    if not timeline:
        print(TEXTS[lang]['davinci_timeline_error'])
        return False

    # AppendToTimeline() attaches to the *current* timeline — switch before append.
    project.SetCurrentTimeline(timeline)
    _davinci_sync_active_timeline_fps(project, timeline, timeline_rate, analysis_fps, lang)
    print(
        TEXTS[lang]['davinci_timeline_fps'].format(
            rate=timeline_rate,
            clip=clip_fps_meta if clip_fps_meta is not None else "(none)",
        ),
        flush=True,
    )

    # all_seqs is half-open [start, end) like FFmpeg trim. DaVinci AppendToTimeline uses the same convention:
    # duration = endFrame - startFrame (exclusive end), per Resolve scripting docs / behaviour — do NOT use end-1 here.
    clips_to_append = []
    for start_frame, end_frame, is_good in all_seqs:
        if is_good:
            duration = end_frame - start_frame
            if duration > 0:
                clips_to_append.append({
                    "mediaPoolItem": df_item,
                    "startFrame": int(start_frame),
                    "endFrame": int(end_frame),
                })

    if clips_to_append:
        mediaPool.AppendToTimeline(clips_to_append)
    else:
        print(TEXTS[lang]['davinci_no_valid_scenes'])
        return False

    # Resolve may snap timeline FPS after append — re-sync only if needed (avoids duplicate API warnings).
    project.SetCurrentTimeline(timeline)
    _davinci_sync_active_timeline_fps(
        project, timeline, timeline_rate, analysis_fps, lang, skip_if_close=True
    )

    project.DeleteAllRenderJobs()

    preset_name = str(settings.get("davinci_render_preset", "AutoCutPreset") or "AutoCutPreset").strip()
    if not preset_name:
        preset_name = "AutoCutPreset"
    if not project.LoadRenderPreset(preset_name):
        print(TEXTS[lang]['davinci_preset_warning'].format(preset=preset_name))
        print(TEXTS[lang]['davinci_preset_fallback'])

    try:
        render_fps = float(analysis_fps)
    except (TypeError, ValueError):
        render_fps = 30.0
    project.SetRenderSettings({
        "SelectAllFrames": True,
        "TargetDir": target_dir,
        "CustomName": custom_render_name,
        "ResolutionWidth": width,
        "ResolutionHeight": height,
        "FrameRate": render_fps,
    })

    project.AddRenderJob()
    print(TEXTS[lang]['davinci_job_created'].format(name=custom_render_name))
    print(TEXTS[lang]['davinci_start_render'])
    project.StartRendering()
    print(TEXTS[lang]['davinci_render_running'])

    # Wait until DaVinci finishes; optional time limit (settings.ini davinci_render_timeout_seconds, 0 = no limit).
    # If Resolve is closed mid-render, the API may raise or return empty jobs — treat as soft failure (no process crash).
    start_time = time.time()
    try:
        if not _davinci_wait_for_render_idle(
            project, lang, davinci_render_timeout_seconds, start_time
        ):
            return False

        try:
            render_jobs = project.GetRenderJobList()
        except Exception as e:
            print(TEXTS[lang]['davinci_export_interrupted'].format(error=e))
            return False
        if not render_jobs:
            print(TEXTS[lang]['davinci_render_no_jobs'])
            return False

        job_id = render_jobs[-1].get("JobId")
        if not job_id:
            print(TEXTS[lang]['davinci_render_no_jobs'])
            return False

        try:
            status_dict = project.GetRenderJobStatus(job_id)
        except Exception as e:
            print(TEXTS[lang]['davinci_export_interrupted'].format(error=e))
            return False
        status = status_dict.get("JobStatus", "Unknown") if status_dict else "Unknown"

        if status not in ["Complete", "Abgeschlossen"]:
            print(TEXTS[lang]['davinci_render_status_failed'].format(status=status))
            return False

        return True
    except Exception as e:
        print(TEXTS[lang]['davinci_export_interrupted'].format(error=e))
        return False


def format_clip_fps_property_for_resolve(fps_float):
    """Clip Attributes > Video FPS — scripting examples often use '25.0' style for whole rates."""
    try:
        f = float(fps_float)
    except (TypeError, ValueError):
        return "30.0"
    if f <= 0:
        return "30.0"
    if abs(f - round(f)) < 0.02:
        return f"{float(int(round(f))):.1f}"
    return format_timeline_framerate_for_resolve(f)


def format_timeline_framerate_for_resolve(fps):
    """
    Strings for project.SetSetting('timelineFrameRate', ...).
    Match common Resolve rates so cuts align with AppendToTimeline frame numbers.
    """
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
    return out if out else "30"


def parse_ffmpeg_export_target(raw):
    v = (raw or "both").strip().lower()
    if v in ("both", "source", "deepfake"):
        return v
    return "both"


def load_config():
    base_dir = get_base_dir()
    override = (os.environ.get("OXCO_SETTINGS_INI") or "").strip()
    config_path = override if override else os.path.join(base_dir, 'settings.ini')
    config = configparser.ConfigParser()
    
    if not os.path.exists(config_path):
        # Missing ini = defaults below (Hail Mary always passes a job ini via OXCO_SETTINGS_INI).
        if override:
            print(f"[ERROR] Job settings.ini not found: {config_path}", flush=True)
            sys.exit(1)
    else:
        _read_ini_file(config, config_path)

    if not config.has_section('SETTINGS'):
        config.add_section('SETTINGS')
    
    if not config.has_section('PATHS'):
        config.add_section('PATHS')

    return {
        'language': config.get('SETTINGS', 'language', fallback='en').lower(),
        'buffer_seconds': config.getfloat('SETTINGS', 'buffer_seconds', fallback=2.0),
        'pixel_noise_threshold': config.getint('SETTINGS', 'pixel_noise_threshold', fallback=15),
        'changed_pixels_threshold': config.getint('SETTINGS', 'changed_pixels_threshold', fallback=200),
        'changed_pixels_max_threshold': max(
            0, config.getint('SETTINGS', 'changed_pixels_max_threshold', fallback=0)
        ),
        'enable_ffmpeg_export': _ini_bool(config, 'SETTINGS', 'enable_ffmpeg_export', False),
        'ffmpeg_export_target': parse_ffmpeg_export_target(
            config.get('SETTINGS', 'ffmpeg_export_target', fallback='both')
        ),
        'ffmpeg_encoder': normalize_ffmpeg_encoder(config.get('SETTINGS', 'ffmpeg_encoder', fallback='cpu')),
        'analysis_mode': parse_analysis_mode(config.get('SETTINGS', 'analysis_mode', fallback='classic')),
        'analysis_width': _ini_int_clamped(config, 'SETTINGS', 'analysis_width', 64, 3840, 640),
        'analysis_blur': _ini_int_clamped(config, 'SETTINGS', 'analysis_blur', 0, 15, 3),
        'analysis_hwaccel': _ini_bool(config, 'SETTINGS', 'analysis_hwaccel', False),
        'auto_sync_offset': _ini_bool(config, 'SETTINGS', 'auto_sync_offset', True),
        'max_sync_offset_frames': _ini_int_clamped(config, 'SETTINGS', 'max_sync_offset_frames', 0, 120, 12),
        'min_diff_run_frames': _ini_int_clamped(config, 'SETTINGS', 'min_diff_run_frames', 1, 100, 2),
        'min_keep_seconds': _ini_float_clamped(config, 'SETTINGS', 'min_keep_seconds', 0.0, 10.0, 0.25),
        'cut_padding_frames': _ini_int_clamped(config, 'SETTINGS', 'cut_padding_frames', 0, 60, 0),
        'reuse_metrics': _ini_bool(config, 'SETTINGS', 'reuse_metrics', True),
        'enable_fullcheck_edl': _ini_bool(config, 'SETTINGS', 'enable_fullcheck_edl', False),
        'enable_autodelete_edl': _ini_bool(config, 'SETTINGS', 'enable_autodelete_edl', True),
        'enable_davinci_export': _ini_bool(config, 'SETTINGS', 'enable_davinci_export', False),
        'davinci_render_timeout_seconds': max(
            0,
            config.getint('SETTINGS', 'davinci_render_timeout_seconds', fallback=1800)
        ),
        'davinci_render_preset': config.get('SETTINGS', 'davinci_render_preset', fallback='AutoCutPreset').strip() or 'AutoCutPreset',
        'davinci_api_path': (
            config.get('PATHS', 'davinci_api_path', fallback=r'C:\ProgramData\Blackmagic Design\DaVinci Resolve\Support\Developer\Scripting\Modules').strip()
            or r'C:\ProgramData\Blackmagic Design\DaVinci Resolve\Support\Developer\Scripting\Modules'
        ),
        'final_export_dir': config.get('PATHS', 'final_export_dir', fallback=''),
        'davinci_scriptapp_retry_attempts': _ini_int_clamped(
            config, 'SETTINGS', 'davinci_scriptapp_retry_attempts', 1, 120, 24
        ),
        'davinci_scriptapp_retry_delay_seconds': _ini_float_clamped(
            config, 'SETTINGS', 'davinci_scriptapp_retry_delay_seconds', 1.0, 30.0, 5.0
        ),
        'export_avoid_overwrite': _ini_bool(config, 'SETTINGS', 'export_avoid_overwrite', False),
        'davinci_exe_path': config.get('PATHS', 'davinci_exe_path', fallback='').strip(),
        'davinci_startup_wait_seconds': _ini_int_clamped(
            config, 'SETTINGS', 'davinci_startup_wait_seconds', 0, 600, 20
        ),
    }


def parse_analysis_mode(raw):
    v = (raw or "classic").strip().lower()
    return v if v in oa.ANALYSIS_MODES else "classic"


FFMPEG_ENCODER_PARAMS = {
    'cpu': ["-c:v", "libx264", "-preset", "fast", "-crf", "18"],
    'cpu_hevc': ["-c:v", "libx265", "-preset", "fast", "-crf", "22"],
    'nvidia_h264': ["-c:v", "h264_nvenc", "-preset", "p6", "-cq", "18"],
    'nvidia_hevc': ["-c:v", "hevc_nvenc", "-preset", "p6", "-cq", "18"],
    'nvidia_av1': ["-c:v", "av1_nvenc", "-preset", "p6", "-cq", "18"],
    'amd_h264': ["-c:v", "h264_amf", "-rc", "cqp", "-qp_p", "18", "-qp_i", "18"],
    'amd_hevc': ["-c:v", "hevc_amf", "-rc", "cqp", "-qp_p", "18", "-qp_i", "18"],
    'intel_h264': ["-c:v", "h264_qsv", "-global_quality", "18"],
    'intel_hevc': ["-c:v", "hevc_qsv", "-global_quality", "20"],
}

# Codec names used elsewhere in Hail Mary (Bitrate tab, settings.ini) → keys above.
_FFMPEG_ENCODER_ALIASES = {
    'libx264': 'cpu',
    'libx265': 'cpu_hevc',
    'h264_nvenc': 'nvidia_h264',
    'hevc_nvenc': 'nvidia_hevc',
    'av1_nvenc': 'nvidia_av1',
    'h264_amf': 'amd_h264',
    'hevc_amf': 'amd_hevc',
    'h264_qsv': 'intel_h264',
    'hevc_qsv': 'intel_hevc',
}


def normalize_ffmpeg_encoder(raw):
    """Accept compare keys and plain FFmpeg codec names; unknown → CPU (works on every machine)."""
    v = (raw or "").strip().lower()
    v = _FFMPEG_ENCODER_ALIASES.get(v, v)
    return v if v in FFMPEG_ENCODER_PARAMS else 'cpu'


def frame_to_tc(frame, fps_int):
    h = int(frame / (fps_int * 3600))
    m = int((frame / (fps_int * 60)) % 60)
    s = int((frame / fps_int) % 60)
    f = int(frame % fps_int)
    return f"{h:02d}:{m:02d}:{s:02d}:{f:02d}"


def compare_export_filename_tag(
    buffer_seconds,
    noise_thresh,
    pixel_thresh,
    changed_pixels_max: int = 0,
    analysis_mode: str = "classic",
):
    """Suffix from Compare filter (buffer / noise / pixel / optional max cap), filename-safe."""
    b = f"{float(buffer_seconds):g}".replace(".", "-")
    s = f"_b{b}_n{int(noise_thresh)}_p{int(pixel_thresh)}"
    mx = int(changed_pixels_max) if changed_pixels_max else 0
    if mx > 0:
        s += f"_m{mx}"
    if analysis_mode == "blob":
        s += "_blob"
    return s


def _settings_filename_tag(settings):
    return compare_export_filename_tag(
        settings["buffer_seconds"],
        settings["pixel_noise_threshold"],
        settings["changed_pixels_threshold"],
        settings.get("changed_pixels_max_threshold", 0) or 0,
        settings.get("analysis_mode", "classic"),
    )


def allocate_unique_media_output_path(base_without_ext, ext, settings):
    """
    base_without_ext: full path without extension, e.g. C:/work/clip_AutoCut
    ext: includes dot, e.g. .mp4
    If export_avoid_overwrite is false, returns base_without_ext + ext (FFmpeg -y may overwrite).
    """
    if not settings.get("export_avoid_overwrite"):
        return base_without_ext + ext
    tag = _settings_filename_tag(settings)
    candidates = [f"{base_without_ext}{tag}{ext}"] + [
        f"{base_without_ext}{tag}_{i}{ext}" for i in range(2, 21)
    ]
    for path in candidates:
        if not os.path.exists(path):
            return path
    return f"{base_without_ext}{tag}_{datetime.datetime.now().strftime('%H%M%S')}{ext}"


def pick_unique_davinci_custom_name(base_name, target_dir, settings):
    """Resolve CustomName (no extension). When avoid-overwrite, append filter tag then _2.._20 if needed."""
    stem = f"{base_name}_DaVinci_Export"
    if not settings.get("export_avoid_overwrite"):
        return stem
    tag = _settings_filename_tag(settings)
    exts = (".mp4", ".mov", ".mxf", ".mkv")

    def stem_taken(cand_stem):
        if not os.path.isdir(target_dir):
            return False
        for e in exts:
            if os.path.isfile(os.path.join(target_dir, cand_stem + e)):
                return True
        return False

    for extra in [""] + [f"_{i}" for i in range(2, 21)]:
        cand = f"{stem}{tag}{extra}"
        if not stem_taken(cand):
            return cand
    return f"{stem}{tag}_{datetime.datetime.now().strftime('%H%M%S')}"


def get_segment_style(is_good):
    if is_good:
        return {
            "clip_name": "SWAP_OK",
            "clip_color": "Green",
            "resolve_color": "ResolveColorGreen",
            "marker_text": "SWAP_OK"
        }
    else:
        return {
            "clip_name": "FLICK_ERROR",
            "clip_color": "Red",
            "resolve_color": "ResolveColorRed",
            "marker_text": "FLICK_ERROR"
        }

def write_edl(output_path, all_seqs, fps_int, video_path, auto_remove):
    clip_name = os.path.basename(video_path)
    with open(output_path, "w", encoding="utf-8") as f:
        f.write("TITLE: GPU Check EDL\n")
        f.write("FCM: NON-DROP FRAME\n\n")

        event_num = 1
        record_time_frames = 0

        for start_frame, end_frame, is_good in all_seqs:
            if auto_remove and not is_good:
                continue

            duration = end_frame - start_frame
            if duration <= 0:
                continue

            src_tc_in = frame_to_tc(start_frame, fps_int)
            src_tc_out = frame_to_tc(end_frame, fps_int)

            if auto_remove:
                rec_tc_in = frame_to_tc(record_time_frames, fps_int)
                record_time_frames += duration
                rec_tc_out = frame_to_tc(record_time_frames, fps_int)
            else:
                rec_tc_in = src_tc_in
                rec_tc_out = src_tc_out

            style = get_segment_style(is_good)

            f.write(f"{event_num:03d} AX AA/V C        {src_tc_in} {src_tc_out} {rec_tc_in} {rec_tc_out}\n")
            f.write(f"* FROM CLIP NAME: {clip_name}\n")
            f.write(f"* CLIP NAME: {style['clip_name']}\n")
            f.write(f"* CLIP COLOR: {style['clip_color']}\n")
            f.write(f"* {style['marker_text']}\n")
            f.write(f" |C:{style['resolve_color']} |M:{style['marker_text']} |D:1\n\n")

            event_num += 1

def probe_has_audio(ffprobe_exe, video_path):
    """True/False when ffprobe can tell, None when unknown (no ffprobe or probe error)."""
    if not ffprobe_exe or not video_path or not os.path.isfile(video_path):
        return None
    try:
        kw = {}
        if hasattr(subprocess, "CREATE_NO_WINDOW"):
            kw["creationflags"] = subprocess.CREATE_NO_WINDOW
        completed = subprocess.run(
            [ffprobe_exe, "-v", "error", "-select_streams", "a", "-show_entries", "stream=index", "-of", "json", video_path],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=90, **kw,
        )
    except (subprocess.SubprocessError, OSError):
        return None
    if completed.returncode != 0:
        return None
    try:
        payload = json.loads(completed.stdout or "{}")
    except json.JSONDecodeError:
        return None
    return bool(payload.get("streams"))


def build_ffmpeg_select_filter(good_seqs, fps_float, with_audio, audio_chunk_samples=128):
    """
    One-pass cut list: `select` by frame number (frame-exact), audio by time in small chunks
    (asetnsamples keeps each cut within ~3 ms instead of a whole 1024-sample audio frame).
    """
    v_terms = "+".join(f"between(n,{int(a)},{int(b) - 1})" for a, b, _ in good_seqs)
    lines = [f"[0:v:0]select='{v_terms}',setpts=N/({fps_float:.9f})/TB[outv]"]
    if with_audio:
        a_terms = "+".join(
            f"gte(t,{a / fps_float:.6f})*lt(t,{b / fps_float:.6f})" for a, b, _ in good_seqs
        )
        lines.append(
            f"[0:a:0]asetnsamples=n={int(audio_chunk_samples)}:p=0,aselect='{a_terms}',asetpts=N/SR/TB[outa]"
        )
    return ";\n".join(lines) + "\n"


def export_video_ffmpeg(video_path, output_path, all_seqs, fps_float, total_frames, ffmpeg_exe, lang, encoder_setting, ffprobe_exe=None):
    """True bei Erfolg oder nichts zu tun; False bei FFmpeg-Fehler."""
    good_seqs = [seq for seq in all_seqs if seq[2] and seq[1] > seq[0]]
    if not good_seqs:
        return True

    encoder_key = normalize_ffmpeg_encoder(encoder_setting)
    out_frames = sum(b - a for a, b, _ in good_seqs)
    has_audio = probe_has_audio(ffprobe_exe, video_path)
    if has_audio is False:
        print(TEXTS[lang]["ffmpeg_no_audio"].format(name=os.path.basename(video_path)), flush=True)

    print(TEXTS[lang]['ffmpeg_render'].format(name=os.path.basename(output_path), codec=encoder_key))
    ok = _run_ffmpeg_cut(
        video_path, output_path, good_seqs, fps_float, out_frames, ffmpeg_exe, lang, encoder_key,
        with_audio=has_audio is not False,
    )
    if not ok and has_audio is None:
        # Unknown audio layout (no ffprobe): the usual failure is a missing audio stream.
        print(TEXTS[lang]["ffmpeg_retry_no_audio"], flush=True)
        ok = _run_ffmpeg_cut(
            video_path, output_path, good_seqs, fps_float, out_frames, ffmpeg_exe, lang, encoder_key,
            with_audio=False,
        )
    if ok:
        print(TEXTS[lang]["ffmpeg_success"])
    return ok


def _run_ffmpeg_cut(video_path, output_path, good_seqs, fps_float, out_frames, ffmpeg_exe, lang, encoder_key, *, with_audio):
    filter_path = output_path + "_filter.txt"
    with open(filter_path, 'w', encoding='utf-8') as f:
        f.write(build_ffmpeg_select_filter(good_seqs, fps_float, with_audio))

    cmd = [
        ffmpeg_exe, "-y", "-hide_banner",
        "-i", video_path,
        "-filter_complex_script", filter_path,
        "-map", "[outv]",
    ]
    if with_audio:
        cmd += ["-map", "[outa]"]
    cmd += FFMPEG_ENCODER_PARAMS[encoder_key]
    cmd += ["-r", f"{fps_float:.9f}"]
    if with_audio:
        cmd += ["-c:a", "aac", "-b:a", "256k"]
    cmd += [output_path]

    try:
        kw = {}
        if hasattr(subprocess, "CREATE_NO_WINDOW"):
            kw["creationflags"] = subprocess.CREATE_NO_WINDOW
        process = subprocess.Popen(
            cmd,
            stderr=subprocess.PIPE,
            stdout=subprocess.DEVNULL,
            stdin=subprocess.DEVNULL,
            universal_newlines=True,
            encoding="utf-8",
            errors="replace",
            **kw,
        )

        frame_pattern = re.compile(r"frame=\s*(\d+)")
        last_lines = []

        for line in process.stderr:
            last_lines.append(line.strip())
            if len(last_lines) > 15:
                last_lines.pop(0)

            match = frame_pattern.search(line)
            if match and out_frames > 0:
                frame = int(match.group(1))
                percent = min(100.0, (frame / out_frames) * 100)
                sys.stdout.write(
                    TEXTS[lang]["ffmpeg_progress"].format(percent=percent, frame=frame, total=out_frames)
                )
                sys.stdout.flush()

        process.wait()

        if process.returncode != 0:
            print(TEXTS[lang]["ffmpeg_error"].format(name=os.path.basename(video_path)))
            for line in last_lines:
                print(line)
            print("---------------------------------\n")
            return False
        return True
    except Exception as ex:
        print(TEXTS[lang]["ffmpeg_error"].format(name=os.path.basename(video_path)))
        print(str(ex))
        print("---------------------------------\n")
        return False
    finally:
        if os.path.exists(filter_path):
            try:
                os.remove(filter_path)
            except Exception:
                pass


def export_via_davinci(
    deepfake_video,
    source_video,
    all_seqs,
    davinci_api_path,
    lang,
    final_export_dir,
    davinci_render_timeout_seconds,
    *,
    analysis_fps,
    scriptapp_retry_attempts=60,
    scriptapp_retry_delay_sec=3.0,
    settings=None,
):
    """Returns True if render completed successfully, False otherwise."""

    if davinci_api_path not in sys.path:
        sys.path.append(davinci_api_path)

    try:
        import DaVinciResolveScript as dvr_script
    except ImportError:
        print(TEXTS[lang]['davinci_api_missing'])
        print(TEXTS[lang]['davinci_api_path_searched'].format(path=davinci_api_path))
        print(TEXTS[lang]['davinci_api_check_explorer'])
        return False

    # scriptapp("Resolve") is often still None until Resolve Studio finishes init (or if not Studio / wrong user).
    resolve = _connect_resolve_scriptapp(
        dvr_script,
        lang,
        scriptapp_retry_attempts,
        scriptapp_retry_delay_sec,
    )
    if not resolve:
        return False

    projectManager = resolve.GetProjectManager()
    project = _wait_for_resolve_project(projectManager, lang)
    if not project:
        return False

    print(TEXTS[lang]['davinci_sending_data'])

    # Pause so Windows can release file locks from OpenCV before Resolve imports the clip
    time.sleep(2)

    # 2. Pfad für die DaVinci-API erzwingen (Forward-Slashes verhindern Lesefehler)
    abs_video_fs = os.path.abspath(deepfake_video)
    abs_video_path = abs_video_fs.replace("\\", "/")
    if final_export_dir and os.path.exists(final_export_dir):
        target_dir = final_export_dir
    else:
        target_dir = os.path.dirname(abs_video_fs)
    if settings is None:
        settings = {"export_avoid_overwrite": False}

    try:
        analysis_fps = float(analysis_fps)
    except (TypeError, ValueError):
        analysis_fps = 30.0
    if analysis_fps <= 0:
        analysis_fps = 30.0

    # Timeline FPS must match compare.py frame indices (OpenCV + ffprobe), not only Resolve metadata.
    timeline_rate = format_timeline_framerate_for_resolve(analysis_fps)

    base_dir = get_base_dir()
    ffprobe_local = os.path.join(base_dir, "ffprobe.exe")
    ffprobe_exe = ffprobe_local if os.path.isfile(ffprobe_local) else shutil.which("ffprobe")

    width = height = None
    if ffprobe_exe:
        width, height = probe_video_wh_ffprobe(ffprobe_exe, abs_video_fs)

    if not width or not height:
        # Runs as a hidden subprocess — never prompt; fall back to OpenCV, then 1080p.
        cap = cv2.VideoCapture(abs_video_fs)
        try:
            width = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH) or 0)
            height = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT) or 0)
        finally:
            cap.release()
        if width > 0 and height > 0:
            print(TEXTS[lang]["davinci_res_fallback"].format(w=width, h=height), flush=True)
        else:
            print(TEXTS[lang]["davinci_res_default"], flush=True)
            width, height = 1920, 1080

    try:
        orig_resolve_project_name = project.GetName()
    except Exception:
        orig_resolve_project_name = None

    export_proj, export_proj_name = _davinci_try_create_export_project(projectManager, lang)
    switched_to_temp_export_project = False
    if export_proj is not None:
        project = export_proj
        switched_to_temp_export_project = True
        mediaPool = project.GetMediaPool()
        _davinci_delete_all_timelines(project, mediaPool)
        if orig_resolve_project_name:
            print(
                TEXTS[lang]["davinci_temp_project"].format(
                    temp=export_proj_name,
                    orig=orig_resolve_project_name,
                ),
                flush=True,
            )
    else:
        mediaPool = project.GetMediaPool()

    try:
        return _davinci_run_export_pipeline(
            project,
            mediaPool,
            deepfake_video,
            all_seqs,
            lang,
            target_dir,
            davinci_render_timeout_seconds,
            analysis_fps,
            timeline_rate,
            width,
            height,
            abs_video_path,
            settings,
        )
    finally:
        _davinci_restore_user_project(
            projectManager,
            orig_resolve_project_name,
            switched_to_temp_export_project,
            lang,
        )


def _ensure_stdio_utf8() -> None:
    """Windows-Konsole nutzt oft cp1252; unsere Meldungen enthalten z. B. „→“ (U+2192)."""
    try:
        if hasattr(sys.stdout, "reconfigure"):
            sys.stdout.reconfigure(encoding="utf-8", errors="replace", line_buffering=True)
        if hasattr(sys.stderr, "reconfigure"):
            sys.stderr.reconfigure(encoding="utf-8", errors="replace", line_buffering=True)
    except Exception:
        pass


def _resolve_lang(settings) -> str:
    lang = settings.get("language", "en") or "en"
    if isinstance(lang, str):
        lang = lang.lower()
    return lang if lang in TEXTS else "en"


def _locate_ffmpeg_tools():
    """(ffmpeg_exe or None, ffprobe_exe or None) — app folder first, then PATH."""
    base_dir = get_base_dir()
    local_ffmpeg = os.path.join(base_dir, "ffmpeg.exe")
    ffmpeg_exe = local_ffmpeg if os.path.isfile(local_ffmpeg) else shutil.which("ffmpeg")
    local_ffprobe = os.path.join(base_dir, "ffprobe.exe")
    ffprobe_exe = local_ffprobe if os.path.isfile(local_ffprobe) else shutil.which("ffprobe")
    return ffmpeg_exe, ffprobe_exe


def _analysis_params(settings) -> "oa.AnalysisParams":
    return oa.AnalysisParams(
        mode=settings.get("analysis_mode", "classic"),
        noise=settings["pixel_noise_threshold"],
        analysis_width=settings.get("analysis_width", 640),
        blur=settings.get("analysis_blur", 3),
        hwaccel=settings.get("analysis_hwaccel", False),
        auto_sync=settings.get("auto_sync_offset", True),
        max_sync_offset=settings.get("max_sync_offset_frames", 12),
    ).normalized()


def _filter_params(settings, fps_float) -> "oa.FilterParams":
    return oa.FilterParams(
        pixel_threshold=int(settings["changed_pixels_threshold"]),
        pixel_max=int(settings.get("changed_pixels_max_threshold", 0) or 0),
        buffer_frames=int(fps_float * float(settings["buffer_seconds"])),
        min_diff_run=int(settings.get("min_diff_run_frames", 2)),
        min_keep_frames=int(round(float(settings.get("min_keep_seconds", 0.25)) * fps_float)),
        cut_padding=int(settings.get("cut_padding_frames", 0)),
    )


EXPORT_CHECKPOINT_FILENAME = "oxco_compare_export_checkpoint.json"
CHECKPOINT_VERSION = 2


def export_checkpoint_path() -> str:
    override = (os.environ.get("OXCO_CHECKPOINT_PATH") or "").strip()
    if override:
        return override
    return os.path.join(get_base_dir(), EXPORT_CHECKPOINT_FILENAME)


def _remove_export_checkpoint_silent() -> None:
    try:
        p = export_checkpoint_path()
        if os.path.isfile(p):
            os.remove(p)
    except OSError:
        pass


def _write_export_checkpoint(
    source_video: str,
    deepfake_video: str,
    lang: str,
    all_seqs: list,
    fps_float: float,
    fps_int: int,
    total_frames: int,
    *,
    src_offset: int = 0,
    df_offset: int = 0,
    metrics_path: str = "",
) -> None:
    """all_seqs are in analysis-frame indices; src_offset/df_offset map them to each file's frame numbers."""
    data = {
        "version": CHECKPOINT_VERSION,
        "source_video": os.path.normcase(os.path.abspath(source_video)),
        "deepfake_video": os.path.normcase(os.path.abspath(deepfake_video)),
        "lang": lang,
        "all_seqs": [[int(a), int(b), bool(c)] for (a, b, c) in all_seqs],
        "fps_float": float(fps_float),
        "fps_int": int(fps_int),
        "total_frames": int(total_frames),
        "src_offset": int(src_offset),
        "df_offset": int(df_offset),
        "metrics_path": metrics_path or "",
    }
    try:
        path = export_checkpoint_path()
        folder = os.path.dirname(path)
        if folder:
            os.makedirs(folder, exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)
        print(TEXTS[lang]["checkpoint_written"], flush=True)
    except OSError as e:
        print(TEXTS[lang]["checkpoint_write_failed"].format(error=e), flush=True)


def _load_export_checkpoint_data(source_video: str, deepfake_video: str, lang: str):
    """Lädt Checkpoint (v1 oder v2); Rückgabe dict oder None."""
    ck_path = export_checkpoint_path()
    if not os.path.isfile(ck_path):
        print(TEXTS[lang]["retry_export_no_checkpoint"], flush=True)
        return None
    try:
        with open(ck_path, encoding="utf-8") as f:
            data = json.load(f)
    except (OSError, json.JSONDecodeError) as e:
        print(TEXTS[lang]["retry_export_ck_read_err"].format(error=e), flush=True)
        return None

    if data.get("version") not in (1, CHECKPOINT_VERSION):
        print(TEXTS[lang]["retry_export_no_checkpoint"], flush=True)
        return None

    sa = os.path.normcase(os.path.abspath(source_video))
    sb = os.path.normcase(os.path.abspath(deepfake_video))
    if data.get("source_video") != sa or data.get("deepfake_video") != sb:
        print(TEXTS[lang]["retry_export_path_mismatch"], flush=True)
        return None
    if not os.path.isfile(source_video) or not os.path.isfile(deepfake_video):
        print(TEXTS[lang]["retry_export_missing_file"], flush=True)
        return None

    all_seqs = []
    for item in data.get("all_seqs") or []:
        if len(item) >= 3:
            all_seqs.append((int(item[0]), int(item[1]), bool(item[2])))
    if not all_seqs:
        print(TEXTS[lang]["retry_export_no_checkpoint"], flush=True)
        return None

    return {
        "all_seqs": all_seqs,
        "fps_float": float(data["fps_float"]),
        "fps_int": int(data["fps_int"]),
        "total_frames": int(data["total_frames"]),
        "src_offset": int(data.get("src_offset") or 0),
        "df_offset": int(data.get("df_offset") or 0),
        "metrics_path": str(data.get("metrics_path") or ""),
    }


def run_compare_export_pipeline(
    source_video: str,
    deepfake_video: str,
    settings: dict,
    lang: str,
    all_seqs: list,
    fps_float: float,
    fps_int: int,
    total_frames: int,
    *,
    davinci_only: bool = False,
    keep_checkpoint_on_exit: bool = False,
    src_offset: int = 0,
    df_offset: int = 0,
) -> int:
    """EDL + FFmpeg + DaVinci gemäß settings; Rückgabe 0 oder 3."""
    ffmpeg_found, ffprobe_exe = _locate_ffmpeg_tools()
    ffmpeg_exe = ffmpeg_found or "ffmpeg"

    enable_ffmpeg = False if davinci_only else settings["enable_ffmpeg_export"]
    ffmpeg_export_target = settings["ffmpeg_export_target"]
    ffmpeg_encoder = settings["ffmpeg_encoder"]
    enable_fullcheck = False if davinci_only else settings["enable_fullcheck_edl"]
    enable_autodelete_edl = False if davinci_only else settings["enable_autodelete_edl"]
    enable_davinci = True if davinci_only else settings["enable_davinci_export"]
    davinci_api_path = settings["davinci_api_path"]
    final_export_dir = settings.get("final_export_dir", "") or ""
    davinci_render_timeout_seconds = settings.get("davinci_render_timeout_seconds", 1800)
    scriptapp_retries = settings.get("davinci_scriptapp_retry_attempts", 60)
    scriptapp_delay = settings.get("davinci_scriptapp_retry_delay_seconds", 3.0)
    davinci_exe_path = settings.get("davinci_exe_path", "") or ""
    try:
        _dsw = settings.get("davinci_startup_wait_seconds", 20)
        davinci_startup_wait_seconds = max(0, min(600, int(_dsw)))
    except (TypeError, ValueError):
        davinci_startup_wait_seconds = 20

    # Analysis indices → frame numbers of each file (non-zero when a leading-frame offset was detected).
    seqs_src = oa.shift_seqs(all_seqs, src_offset)
    seqs_df = oa.shift_seqs(all_seqs, df_offset)

    ffmpeg_state = "an" if enable_ffmpeg else "aus"
    davinci_state = "an" if enable_davinci else "aus"
    print(f"[INFO] Export-Modus: FFmpeg={ffmpeg_state}, DaVinci={davinci_state}\n", flush=True)

    if enable_davinci:
        ensure_resolve_running_for_compare(davinci_exe_path, davinci_startup_wait_seconds, lang)

    base_src_full = os.path.splitext(source_video)[0]
    base_df_full = os.path.splitext(deepfake_video)[0]

    if final_export_dir and os.path.exists(final_export_dir):
        path_src_base = os.path.join(final_export_dir, os.path.basename(base_src_full))
        path_df_base = os.path.join(final_export_dir, os.path.basename(base_df_full))
    else:
        path_src_base = base_src_full
        path_df_base = base_df_full

    if enable_autodelete_edl:
        edl_src_auto = f"{path_src_base}_SOURCE_AutoDelete.edl"
        edl_df_auto = f"{path_df_base}_DEEPFAKE_AutoDelete.edl"
        write_edl(edl_src_auto, seqs_src, fps_int, source_video, auto_remove=True)
        write_edl(edl_df_auto, seqs_df, fps_int, deepfake_video, auto_remove=True)
        print(TEXTS[lang]["edl_auto_created"])
        print(f"- {os.path.basename(edl_src_auto)}")
        print(f"- {os.path.basename(edl_df_auto)}")

    if enable_fullcheck:
        edl_src_full = f"{path_src_base}_SOURCE_FullCheck.edl"
        edl_df_full = f"{path_df_base}_DEEPFAKE_FullCheck.edl"
        write_edl(edl_src_full, seqs_src, fps_int, source_video, auto_remove=False)
        write_edl(edl_df_full, seqs_df, fps_int, deepfake_video, auto_remove=False)
        print(TEXTS[lang]["edl_full_created"])

    ffmpeg_ok_all = True
    if enable_ffmpeg:
        vid_src_auto = allocate_unique_media_output_path(f"{path_src_base}_AutoCut", ".mp4", settings)
        vid_df_auto = allocate_unique_media_output_path(f"{path_df_base}_AutoCut", ".mp4", settings)
        if ffmpeg_export_target in ("both", "source"):
            if not export_video_ffmpeg(
                source_video, vid_src_auto, seqs_src, fps_float, total_frames, ffmpeg_exe, lang, ffmpeg_encoder,
                ffprobe_exe=ffprobe_exe,
            ):
                ffmpeg_ok_all = False
        if ffmpeg_export_target in ("both", "deepfake"):
            if not export_video_ffmpeg(
                deepfake_video, vid_df_auto, seqs_df, fps_float, total_frames, ffmpeg_exe, lang, ffmpeg_encoder,
                ffprobe_exe=ffprobe_exe,
            ):
                ffmpeg_ok_all = False

    davinci_ok = True
    if enable_davinci:
        api_abs = os.path.abspath(davinci_api_path)
        dvm = os.path.join(api_abs, "DaVinciResolveScript.py")
        print(TEXTS[lang]["davinci_export_start_info"].format(path=api_abs), flush=True)
        if not os.path.isfile(dvm):
            print(TEXTS[lang]["davinci_script_missing"].format(path=dvm), flush=True)
            davinci_ok = False
        else:
            print(
                f"[INFO] DaVinci scriptapp connect: up to {int(scriptapp_retries)} tries, "
                f"{float(scriptapp_delay)}s apart (settings.ini: davinci_scriptapp_retry_*).\n",
                flush=True,
            )
            try:
                davinci_ok = export_via_davinci(
                    deepfake_video,
                    source_video,
                    seqs_df,
                    davinci_api_path,
                    lang,
                    final_export_dir,
                    davinci_render_timeout_seconds,
                    analysis_fps=fps_float,
                    scriptapp_retry_attempts=scriptapp_retries,
                    scriptapp_retry_delay_sec=scriptapp_delay,
                    settings=settings,
                )
            except Exception as ex:
                print(TEXTS[lang]["davinci_export_exception"].format(error=ex))
                davinci_ok = False
        if davinci_ok:
            print(TEXTS[lang]["compare_summary_davinci_ok"])
        else:
            print(TEXTS[lang]["compare_summary_davinci_fail"])

    export_failed = (enable_ffmpeg and not ffmpeg_ok_all) or (enable_davinci and not davinci_ok)

    print(TEXTS[lang]["all_done"])
    if export_failed:
        print(TEXTS[lang]["compare_exit_partial"], flush=True)
        return 3
    if not keep_checkpoint_on_exit:
        _remove_export_checkpoint_silent()
    return 0


def _refilter_from_metrics(ck: dict, source_video: str, deepfake_video: str, settings: dict, lang: str):
    """Re-apply the current filters to cached metrics when they were measured with the current analysis params."""
    data = oa.load_metrics(ck.get("metrics_path"))
    if not data:
        return None
    if data.get("params") != _analysis_params(settings).cache_key():
        return None

    def same(a, b):
        return os.path.normcase(os.path.abspath(a or "")) == os.path.normcase(os.path.abspath(b))

    if not (same(data.get("source_video"), source_video) and same(data.get("deepfake_video"), deepfake_video)):
        return None
    metric = data.get("metric") or []
    print(TEXTS[lang]["retry_refilter"].format(frames=len(metric)), flush=True)
    seqs, _flags = oa.compute_segments(metric, _filter_params(settings, ck["fps_float"]))
    _print_cut_summary(seqs, ck["fps_float"], lang, deepfake_video, ck.get("metrics_path", ""))
    return seqs


def run_retry_export_from_checkpoint(source_video: str, deepfake_video: str, settings: dict) -> int:
    """Nur Export erneut (keine Pixelanalyse); aktuelle Filter werden auf gespeicherte Messwerte angewendet. 0/3/1."""
    lang = _resolve_lang(settings)
    ck = _load_export_checkpoint_data(source_video, deepfake_video, lang)
    if ck is None:
        return 1

    all_seqs = _refilter_from_metrics(ck, source_video, deepfake_video, settings, lang) or ck["all_seqs"]
    if not any(v for _a, _b, v in all_seqs):
        print(TEXTS[lang]['no_diff'])
        return 0
    print(TEXTS[lang]["retry_export_start"], flush=True)
    return run_compare_export_pipeline(
        source_video, deepfake_video, settings, lang, all_seqs,
        ck["fps_float"], ck["fps_int"], ck["total_frames"],
        src_offset=ck["src_offset"], df_offset=ck["df_offset"],
    )


def run_davinci_export_from_checkpoint(source_video: str, deepfake_video: str, settings: dict) -> int:
    """Nur DaVinci-Export aus Checkpoint (Pipeline-Phase nach Analyse)."""
    lang = _resolve_lang(settings)
    ck = _load_export_checkpoint_data(source_video, deepfake_video, lang)
    if ck is None:
        return 1

    print("[INFO] Pipeline: DaVinci-Export startet (Analyse war bereits fertig).\n", flush=True)
    return run_compare_export_pipeline(
        source_video,
        deepfake_video,
        settings,
        lang,
        ck["all_seqs"],
        ck["fps_float"],
        ck["fps_int"],
        ck["total_frames"],
        davinci_only=True,
        src_offset=ck["src_offset"],
        df_offset=ck["df_offset"],
    )


def _print_cut_summary(seqs, fps_float, lang, deepfake_video="", metrics_path=""):
    kept, cut, n = oa.segment_summary(seqs)
    print(TEXTS[lang]["cut_summary"].format(kept=kept / fps_float, cut=cut / fps_float, n=n), flush=True)
    # Machine-readable line for Hail Mary (result badge per deepfake, timeline reload).
    result = {
        "deepfake": os.path.abspath(deepfake_video) if deepfake_video else "",
        "kept_seconds": round(kept / fps_float, 3),
        "cut_seconds": round(cut / fps_float, 3),
        "cuts": n,
        "metrics": metrics_path or "",
    }
    print("HM_RESULT:" + json.dumps(result, ensure_ascii=False), flush=True)


def _print_settings(settings, lang, params, fp):
    on, off = TEXTS[lang]['on'], TEXTS[lang]['off']
    print(TEXTS[lang]['loading_settings'])
    if params.mode == "blob":
        mode_txt = TEXTS[lang]['mode_blob'].format(w=params.analysis_width, blur=params.blur)
    else:
        mode_txt = TEXTS[lang]['mode_classic']
    print(TEXTS[lang]['set_mode'].format(val=mode_txt))
    print(TEXTS[lang]['set_buffer'].format(val=settings['buffer_seconds']))
    print(TEXTS[lang]['set_noise'].format(val=params.noise))
    print(TEXTS[lang]['set_pixel'].format(val=fp.pixel_threshold))
    print(TEXTS[lang]['set_pixel_max'].format(val=fp.pixel_max))
    print(TEXTS[lang]['set_postfilter'].format(run=fp.min_diff_run, keep=fp.min_keep_frames, pad=fp.cut_padding))
    sync_txt = f"{on} (±{params.max_sync_offset})" if params.auto_sync and params.max_sync_offset > 0 else off
    print(TEXTS[lang]['set_sync'].format(val=sync_txt))
    print(TEXTS[lang]['set_davinci'].format(val=on if settings['enable_davinci_export'] else off))
    if settings['enable_davinci_export']:
        _dto = settings.get('davinci_render_timeout_seconds', 1800)
        _tout = TEXTS[lang]['davinci_timeout_off'] if _dto == 0 else f"{_dto}s"
        print(TEXTS[lang]['set_davinci_timeout'].format(val=_tout))
    print(TEXTS[lang]['set_ffmpeg'].format(val=on if settings['enable_ffmpeg_export'] else off))
    if settings['enable_ffmpeg_export']:
        target_key = f"ffmpeg_target_{settings['ffmpeg_export_target']}"
        print(TEXTS[lang]['set_ffmpeg_target'].format(val=TEXTS[lang][target_key]))
    print(TEXTS[lang]['set_fullcheck'].format(val=on if settings['enable_fullcheck_edl'] else off))
    print(TEXTS[lang]['set_export_unique'].format(val=on if settings.get('export_avoid_overwrite') else off))


def _probe_inputs(source_video, deepfake_video, ffprobe_exe, lang):
    """FPS, frame-count hint and source size; prints the cheap sync sanity warnings."""
    cap_src = cv2.VideoCapture(source_video)
    cap_df = cv2.VideoCapture(deepfake_video)
    try:
        try:
            opencv_fps_raw = float(cap_src.get(cv2.CAP_PROP_FPS))
        except (TypeError, ValueError):
            opencv_fps_raw = 0.0
        r_fps, avg_fps = (None, None)
        if ffprobe_exe:
            r_fps, avg_fps = probe_video_stream_fps_rates(ffprobe_exe, source_video)
        picked = pick_analysis_fps_from_probes(opencv_fps_raw, r_fps, avg_fps, lang)
        if picked is not None:
            fps_float = float(picked)
            if (not opencv_fps_raw or opencv_fps_raw <= 0) and fps_float > 0:
                print(TEXTS[lang]['fps_ffprobe_only'].format(fps=f"{fps_float:.6g}"), flush=True)
        else:
            fps_float = opencv_fps_raw
        if not fps_float or fps_float <= 0:
            print(TEXTS[lang]['fps_fallback'])
            fps_float = 30.0
        try:
            total_frames = max(0, int(cap_src.get(cv2.CAP_PROP_FRAME_COUNT)))
        except (TypeError, ValueError):
            total_frames = 0
        src_w = int(cap_src.get(cv2.CAP_PROP_FRAME_WIDTH) or 0)
        src_h = int(cap_src.get(cv2.CAP_PROP_FRAME_HEIGHT) or 0)
        print_stream_sync_warnings(lang, ffprobe_exe, source_video, deepfake_video, cap_src, cap_df)
    finally:
        cap_src.release()
        cap_df.release()
    return fps_float, total_frames, (src_w, src_h)


def _run_analysis(source_video, deepfake_video, settings, params, lang, *, ffmpeg_exe, fps_float, total_frames, src_wh):
    """Decode + measure (or reuse cached metrics). Returns (AnalysisResult, metrics_path)."""
    metrics_path = oa.metrics_cache_path(source_video, deepfake_video, params)
    cached = oa.load_metrics(metrics_path) if settings.get("reuse_metrics", True) else None
    if cached:
        result = oa.result_from_payload(cached)
        print(TEXTS[lang]['metrics_reused'].format(frames=result.frames), flush=True)
        return result, metrics_path

    print(TEXTS[lang]['start_analysis'].format(frames=total_frames))

    def progress(current, expected):
        if expected <= 0:
            return
        percent = min(100.0, current / expected * 100)
        sys.stdout.write(TEXTS[lang]['analyzing'].format(percent=percent, current=current, total=expected))
        sys.stdout.flush()
        print(f"HM_PROGRESS:{percent:.1f}:Analyse", flush=True)

    result = oa.analyze_pair(
        source_video,
        deepfake_video,
        params,
        src_wh=src_wh,
        ffmpeg_exe=ffmpeg_exe,
        total_hint=total_frames,
        progress=progress,
    )
    print(TEXTS[lang]['frames_processed'].format(count=result.frames))
    print(TEXTS[lang]['reader_info'].format(reader=result.reader, w=result.width, h=result.height), flush=True)
    if params.auto_sync and params.max_sync_offset > 0:
        shift = result.src_offset or result.df_offset
        if shift:
            print(TEXTS[lang]['sync_applied'].format(
                sign="+" if result.src_offset else "-",
                n=shift,
                s0=result.sync_scores[0],
                sb=result.sync_scores[1],
                which=TEXTS[lang]['which_source'] if result.src_offset else TEXTS[lang]['which_deepfake'],
            ), flush=True)
        else:
            print(TEXTS[lang]['sync_none'].format(n=params.max_sync_offset), flush=True)
    if total_frames > 0 and result.frames + max(result.src_offset, result.df_offset) < total_frames:
        print(TEXTS[lang]["sync_warn_truncated"].format(got=result.frames, expected=total_frames), flush=True)
    if result.seconds > 0 and result.frames > 0:
        print(TEXTS[lang]['analysis_time'].format(sec=result.seconds, fps=result.frames / result.seconds), flush=True)
    if result.frames > 0 and oa.save_metrics(
        metrics_path, oa.build_metrics_payload(source_video, deepfake_video, params, result, fps_float)
    ):
        print(TEXTS[lang]['metrics_saved'].format(path=metrics_path), flush=True)
    return result, metrics_path


def main(source_video, deepfake_video, settings, defer_davinci: bool = False):
    lang = _resolve_lang(settings)
    _remove_export_checkpoint_silent()
    _ensure_stdio_utf8()

    ffmpeg_exe, ffprobe_exe = _locate_ffmpeg_tools()
    enable_davinci = settings['enable_davinci_export']
    davinci_exe_path = settings.get("davinci_exe_path", "") or ""
    try:
        davinci_startup_wait_seconds = max(0, min(600, int(settings.get("davinci_startup_wait_seconds", 20))))
    except (TypeError, ValueError):
        davinci_startup_wait_seconds = 20

    ffmpeg_state = "an" if settings['enable_ffmpeg_export'] else "aus"
    davinci_state = "an" if enable_davinci else "aus"
    print(f"[INFO] Export-Modus: FFmpeg={ffmpeg_state}, DaVinci={davinci_state}\n", flush=True)

    if enable_davinci and not defer_davinci:
        ensure_resolve_running_for_compare(davinci_exe_path, davinci_startup_wait_seconds, lang)
    elif defer_davinci and enable_davinci:
        print(
            "[INFO] Pipeline: DaVinci-Export wird nach der Analyse ausgeführt "
            "(nächste Analyse kann parallel zum Resolve-Render laufen).\n",
            flush=True,
        )

    fps_float, total_frames, src_wh = _probe_inputs(source_video, deepfake_video, ffprobe_exe, lang)
    fps_int = max(1, int(round(fps_float)))
    params = _analysis_params(settings)
    fp = _filter_params(settings, fps_float)
    _print_settings(settings, lang, params, fp)

    result, metrics_path = _run_analysis(
        source_video, deepfake_video, settings, params, lang,
        ffmpeg_exe=ffmpeg_exe, fps_float=fps_float, total_frames=total_frames, src_wh=src_wh,
    )
    print("HM_PROGRESS:100:Analyse", flush=True)
    print("HM_PROGRESS_END", flush=True)
    if metrics_path and os.path.isfile(metrics_path):
        print(f"HM_METRICS:{metrics_path}", flush=True)

    all_seqs, flags = oa.compute_segments(result.metric, fp)
    if flags and not any(flags):
        print(TEXTS[lang]['no_swap_warning'], flush=True)
    elif flags and all(flags):
        print(TEXTS[lang]['all_swap_warning'], flush=True)
    _print_cut_summary(all_seqs, fps_float, lang, deepfake_video, metrics_path or "")

    if not any(v for _a, _b, v in all_seqs):
        print(TEXTS[lang]['no_diff'])
        return 0

    _write_export_checkpoint(
        source_video, deepfake_video, lang, all_seqs, fps_float, fps_int, total_frames,
        src_offset=result.src_offset, df_offset=result.df_offset, metrics_path=metrics_path or "",
    )
    if defer_davinci and enable_davinci:
        export_settings = dict(settings)
        export_settings["enable_davinci_export"] = False
        rc = run_compare_export_pipeline(
            source_video,
            deepfake_video,
            export_settings,
            lang,
            all_seqs,
            fps_float,
            fps_int,
            total_frames,
            keep_checkpoint_on_exit=True,
            src_offset=result.src_offset,
            df_offset=result.df_offset,
        )
        if rc in (0, 3):
            print("HM_PIPELINE_DAVINCI_PENDING", flush=True)
        return rc
    return run_compare_export_pipeline(
        source_video, deepfake_video, settings, lang, all_seqs, fps_float, fps_int, total_frames,
        src_offset=result.src_offset, df_offset=result.df_offset,
    )


def _strip_compare_script_from_argv(parts):
    """When launched as ``python compare.py A B``, argv[1] is this script — drop it."""
    if not parts:
        return parts
    if getattr(sys, "frozen", False):
        return parts
    try:
        here = os.path.abspath(__file__)
        p0 = os.path.abspath(parts[0])
        if os.path.isfile(parts[0]) and os.path.normcase(p0) == os.path.normcase(here):
            return parts[1:]
    except (OSError, ValueError, TypeError):
        pass
    return parts


def parse_compare_cli_args(argv=None):
    """Returns (source, deepfake, retry_export_only, defer_davinci, davinci_export_only) or None."""
    parts = list(sys.argv[1:] if argv is None else argv)
    flags = {"--retry-export-only", "--defer-davinci", "--davinci-export-only", "--auto"}
    retry_export_only = "--retry-export-only" in parts
    defer_davinci = "--defer-davinci" in parts
    davinci_export_only = "--davinci-export-only" in parts
    parts = _strip_compare_script_from_argv([p for p in parts if p not in flags])
    if len(parts) != 2:
        return None
    return parts[0], parts[1], retry_export_only, defer_davinci, davinci_export_only


if __name__ == "__main__":
    _ensure_stdio_utf8()
    settings = load_config()
    lang = _resolve_lang(settings)

    parsed = parse_compare_cli_args()
    if parsed is None:
        print(TEXTS[lang]['usage'], flush=True)
        sys.exit(1)
    source_video, deepfake_video, retry_export_only, defer_davinci, davinci_export_only = parsed

    try:
        if davinci_export_only:
            status = run_davinci_export_from_checkpoint(source_video, deepfake_video, settings)
        elif retry_export_only:
            status = run_retry_export_from_checkpoint(source_video, deepfake_video, settings)
        else:
            status = main(source_video, deepfake_video, settings, defer_davinci=defer_davinci)
    except Exception as e:
        import traceback
        print(TEXTS[lang]['unexpected_error'].format(error=e), flush=True)
        traceback.print_exc()
        sys.exit(1)
    sys.exit(int(status or 0))
