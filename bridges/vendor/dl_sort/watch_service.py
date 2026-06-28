# -*- coding: utf-8 -*-
"""Watchdog-Überwachung in einem Hintergrund-Thread mit Verarbeitungs-Warteschlange."""

from __future__ import annotations

import logging
import queue
import sys
import threading
from pathlib import Path
from typing import Callable, Optional

from watchdog.events import FileSystemEvent, FileSystemEventHandler

from config_io import AppConfig
from rule_engine import apply_first_matching_rule, is_temporary_download_path, wait_until_file_stable

if sys.platform == "win32":
    # Native Observer übersieht auf manchen Windows-Setups Ereignisse; Polling ist zuverlässiger.
    from watchdog.observers.polling import PollingObserver as _PlatformObserver
else:
    from watchdog.observers import Observer as _PlatformObserver

log = logging.getLogger(__name__)


class _DownloadEventHandler(FileSystemEventHandler):
    """Leitet relevante Dateiereignisse an eine Callback-Funktion weiter."""

    def __init__(self, on_file_path: Callable[[str], None]) -> None:
        super().__init__()
        self._on_file_path = on_file_path

    def on_created(self, event: FileSystemEvent) -> None:
        if event.is_directory:
            return
        self._dispatch(event.src_path)

    def on_moved(self, event: FileSystemEvent) -> None:
        if event.is_directory:
            return
        dest = getattr(event, "dest_path", None)
        if dest:
            self._dispatch(dest)

    def on_modified(self, event: FileSystemEvent) -> None:
        if event.is_directory:
            return
        self._dispatch(event.src_path)

    def _dispatch(self, path: str) -> None:
        if is_temporary_download_path(path):
            return
        self._on_file_path(path)


class WatchController:
    """
    Start/stopp der Überwachung; Verarbeitung läuft in einem Worker-Thread.

    Konfiguration wird **nur** über set_runtime_config gesetzt (vom Tk-Hauptthread).
    Der Worker liest **niemals** direkt aus der GUI — Tkinter ist nicht thread-sicher.
    """

    def __init__(self) -> None:
        self._observer: Optional[_PlatformObserver] = None
        self._stop_event = threading.Event()
        self._queue: queue.Queue[str | None] = queue.Queue()
        self._worker: Optional[threading.Thread] = None
        self._config_lock = threading.Lock()
        self._runtime_config = AppConfig()

    @property
    def is_running(self) -> bool:
        return self._observer is not None

    def set_runtime_config(self, cfg: AppConfig) -> None:
        """Aktuelle Regeln/Zeiten — nur vom GUI-Thread aufrufen (z. B. alle 300 ms)."""
        snap = cfg.copy()
        with self._config_lock:
            self._runtime_config = snap

    def _get_config_copy(self) -> AppConfig:
        with self._config_lock:
            return self._runtime_config.copy()

    def start(self, watch_folder: str, initial_config: AppConfig) -> None:
        if self._observer is not None:
            return

        folder = str(Path(watch_folder).expanduser().resolve())
        if not folder:
            raise ValueError("Kein Überwachungsordner gesetzt.")

        self.set_runtime_config(initial_config)

        self._queue = queue.Queue()
        self._stop_event.clear()

        def enqueue(path: str) -> None:
            try:
                self._queue.put_nowait(path)
                log.info("Datei-Ereignis in Warteschlange: %s", path)
            except queue.Full:
                log.warning("Warteschlange voll – Ereignis verworfen: %s", path)

        handler = _DownloadEventHandler(enqueue)
        self._observer = _PlatformObserver(timeout=0.8)
        self._observer.schedule(handler, folder, recursive=False)
        self._observer.start()
        log.info("Überwachung gestartet: %s (Observer=%s)", folder, type(self._observer).__name__)

        self._worker = threading.Thread(target=self._worker_loop, daemon=True)
        self._worker.start()

    def enqueue_path(self, path: str) -> None:
        """Eine Datei wie nach einem Watchdog-Ereignis in die Warteschlange legen."""
        if not self.is_running:
            return
        p = Path(path)
        try:
            if not p.is_file():
                return
        except OSError:
            return
        ps = str(p.resolve())
        if is_temporary_download_path(ps):
            return
        try:
            self._queue.put_nowait(ps)
            log.info("Datei-Ereignis in Warteschlange: %s", ps)
        except queue.Full:
            log.warning("Warteschlange voll – verworfen: %s", ps)

    def scan_folder_now(self, folder: str) -> int:
        """
        Bereits vorhandene Dateien im Ordner (nicht rekursiv) einmal einreihen.

        Watchdog meldet nur neue Änderungen — liegende Testdateien sonst nie.
        """
        if not self.is_running:
            return 0
        root = Path(folder).expanduser().resolve()
        if not root.is_dir():
            return 0
        n = 0
        try:
            for child in root.iterdir():
                if child.is_file() and not is_temporary_download_path(str(child)):
                    self.enqueue_path(str(child))
                    n += 1
        except OSError as e:
            log.warning("Ordner-Scan fehlgeschlagen: %s (%s)", root, e)
            return n
        log.info("Ordner-Scan: %d Datei(en) eingereiht in %s", n, root)
        return n

    def stop(self) -> None:
        self._stop_event.set()
        if self._observer is not None:
            self._observer.stop()
            self._observer.join(timeout=5.0)
            self._observer = None
        try:
            self._queue.put_nowait(None)
        except queue.Full:
            pass
        if self._worker is not None:
            self._worker.join(timeout=10.0)
            self._worker = None
        log.info("Überwachung gestoppt")

    def _worker_loop(self) -> None:
        while True:
            item = self._queue.get()
            if item is None:
                break

            cfg = self._get_config_copy()
            if is_temporary_download_path(item):
                log.info("Überspringe temporäre Datei: %s", item)
                continue

            ok = wait_until_file_stable(
                item,
                settle_delay=cfg.settle_delay_seconds,
                poll_interval=cfg.stable_poll_interval_seconds,
                max_wait=cfg.max_wait_seconds,
                stop_event=self._stop_event,
            )
            if not ok or self._stop_event.is_set():
                log.warning(
                    "Datei nicht stabil oder abgebrochen (ok=%s): %s",
                    ok,
                    item,
                )
                continue

            try:
                result = apply_first_matching_rule(cfg, item)
                if result is None:
                    log.info("Keine passende Regel für: %s (Regeln geladen: %d)", item, len(cfg.rules))
                else:
                    log.info("Aktion %r für: %s", result, item)
            except Exception:
                log.exception("Fehler bei der Verarbeitung von %s", item)
