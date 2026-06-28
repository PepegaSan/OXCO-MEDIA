# -*- coding: utf-8 -*-
"""Watchdog tagger monitor logic (vendored from Watchdog tagger/app.py)."""
from __future__ import annotations

import json
import queue
import re
import shutil
import threading
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable


LogFn = Callable[[str], None]


@dataclass
class QueueProfile:
    name: str
    tag: str
    output_folder: str


@dataclass
class AutotaggerConfig:
    input_folder: str = ""
    output_folder: str = ""
    keep_suffix: str = "_hyb,_pro,_exp"
    ignore_suffix: str = "_p"
    drop_suffix: str = ""
    pattern_to_replace: str = "YYMMDDHHmmSS"
    process_existing: bool = False
    profiles: list[QueueProfile] = field(default_factory=list)


class AutotaggerMonitor:
    DEFAULT_KEEP_SUFFIX = "_hyb,_pro,_exp"
    DEFAULT_IGNORE_SUFFIX = "_p"
    DEFAULT_DROP_SUFFIX = ""
    DEFAULT_PATTERN_REPLACE = "YYMMDDHHmmSS"

    def __init__(self, config: AutotaggerConfig, log: LogFn) -> None:
        self.config = config
        self.log = log
        self.stop_event = threading.Event()
        self.monitor_thread: threading.Thread | None = None
        self.ui_queue: queue.Queue[tuple[str, str]] = queue.Queue()
        self.seen_files: dict[Path, tuple[int, float]] = {}
        self.ready_files: set[Path] = set()
        self.first_stable_at: dict[Path, float] = {}
        self.stable_wait_seconds = 60
        self.ready_order_queue: list[str] = []
        self.ready_order_set: set[str] = set()
        self.file_retry_counts: dict[str, int] = {}
        self.max_move_retries = 20
        self.retry_delay_ms = 1500
        self.pending_locked_file: str | None = None
        self.cancelled_files: set[str] = set()
        self.skip_pattern_mismatch: set[str] = set()
        self.processed_count = 0
        self.profiles: list[dict[str, str]] = [
            {"name": p.name, "tag": p.tag, "output_folder": p.output_folder} for p in config.profiles
        ]

    @classmethod
    def from_json(cls, data: dict, log: LogFn) -> "AutotaggerMonitor":
        profiles = []
        for item in data.get("profiles") or []:
            if not isinstance(item, dict):
                continue
            name = str(item.get("name", "")).strip()
            tag = str(item.get("tag", "")).strip()
            out = str(item.get("output_folder", "")).strip()
            if name and tag:
                profiles.append(QueueProfile(name=name, tag=tag, output_folder=out))
        cfg = AutotaggerConfig(
            input_folder=str(data.get("input_folder", "")).strip(),
            output_folder=str(data.get("output_folder", "")).strip(),
            keep_suffix=str(data.get("keep_suffix", cls.DEFAULT_KEEP_SUFFIX)),
            ignore_suffix=str(data.get("ignore_suffix", cls.DEFAULT_IGNORE_SUFFIX)),
            drop_suffix=str(data.get("drop_suffix", cls.DEFAULT_DROP_SUFFIX)),
            pattern_to_replace=str(data.get("pattern_to_replace", cls.DEFAULT_PATTERN_REPLACE)),
            process_existing=bool(data.get("process_existing", False)),
            profiles=profiles,
        )
        return cls(cfg, log)

    def start(self) -> None:
        if self.monitor_thread and self.monitor_thread.is_alive():
            self.log("Monitor laeuft bereits.")
            return
        input_path = Path(self.config.input_folder)
        if not input_path.is_dir():
            raise ValueError("Eingabeordner existiert nicht.")
        if self.config.process_existing and not self.profiles:
            raise ValueError("Warteschlange leer — vorhandene Dateien koennen nicht verarbeitet werden.")

        self.stop_event.clear()
        self.seen_files.clear()
        self.ready_files.clear()
        self.first_stable_at.clear()
        self.ready_order_queue.clear()
        self.ready_order_set.clear()
        self.pending_locked_file = None
        self.cancelled_files.clear()
        self.skip_pattern_mismatch.clear()
        self.processed_count = 0
        seed = self.config.process_existing
        self.monitor_thread = threading.Thread(target=self._monitor_loop, args=(seed,), daemon=True)
        self.monitor_thread.start()
        drain = threading.Thread(target=self._drain_loop, daemon=True)
        drain.start()
        self.log("Monitor gestartet.")

    def stop(self) -> None:
        self.stop_event.set()
        if self.monitor_thread and self.monitor_thread.is_alive():
            self.monitor_thread.join(timeout=3)
        self.log("Monitor gestoppt.")

    def cancel_locked_file(self) -> None:
        if not self.pending_locked_file:
            self.log("Keine gesperrte Datei zum Abbrechen.")
            return
        self._cancel_file_and_consume_profile(Path(self.pending_locked_file), "Vom Nutzer abgebrochen")

    def _drain_loop(self) -> None:
        while not self.stop_event.is_set():
            try:
                action, payload = self.ui_queue.get(timeout=0.3)
            except queue.Empty:
                continue
            if action == "process_file":
                self._process_new_file(Path(payload))
            elif action == "retry":
                self.ui_queue.put(("process_file", payload))

    def _monitor_loop(self, seed_existing: bool) -> None:
        input_path = Path(self.config.input_folder)
        if seed_existing:
            n = self._seed_existing_files(input_path)
            self.log(f"{n} vorhandene Datei(en) eingereiht (aelteste zuerst).")
        while not self.stop_event.is_set():
            try:
                files = sorted(input_path.glob("*.mp4"), key=lambda p: p.stat().st_ctime)
            except (FileNotFoundError, OSError):
                files = []

            for file_path in files:
                try:
                    st = file_path.stat()
                except FileNotFoundError:
                    continue
                if str(file_path) in self.cancelled_files:
                    continue
                key = str(file_path)
                has_pat = self._pattern_found_in_stem(file_path.stem)
                if key in self.skip_pattern_mismatch:
                    if has_pat:
                        self.skip_pattern_mismatch.discard(key)
                    else:
                        continue
                elif not has_pat:
                    self.skip_pattern_mismatch.add(key)
                    self.log(f"Muster nicht im Dateinamen — uebersprungen: {file_path.name}")
                    continue
                if self._should_ignore_file(file_path.stem):
                    self.ready_files.discard(file_path)
                    self.seen_files[file_path] = (st.st_size, st.st_mtime)
                    continue
                current = (st.st_size, st.st_mtime)
                previous = self.seen_files.get(file_path)
                self.seen_files[file_path] = current
                if previous is not None and previous == current:
                    if file_path not in self.first_stable_at:
                        self.first_stable_at[file_path] = time.time()
                    stable_for = time.time() - self.first_stable_at[file_path]
                    if stable_for >= self.stable_wait_seconds:
                        self.ready_files.add(file_path)
                        file_key = str(file_path)
                        if file_key not in self.ready_order_set:
                            self.ready_order_queue.append(file_key)
                            self.ready_order_set.add(file_key)
                else:
                    self.first_stable_at.pop(file_path, None)
                    self.ready_files.discard(file_path)

            if self.pending_locked_file:
                self.stop_event.wait(2)
                continue
            while self.ready_order_queue:
                if self.stop_event.is_set():
                    break
                file_key = self.ready_order_queue.pop(0)
                self.ready_order_set.discard(file_key)
                file_path = Path(file_key)
                if file_path not in self.ready_files:
                    continue
                self.ready_files.discard(file_path)
                self.seen_files.pop(file_path, None)
                self.first_stable_at.pop(file_path, None)
                self.ui_queue.put(("process_file", file_key))
            self.stop_event.wait(2)

    def _seed_existing_files(self, input_path: Path) -> int:
        try:
            files = sorted(input_path.glob("*.mp4"), key=lambda p: p.stat().st_ctime)
        except (FileNotFoundError, OSError):
            return 0
        count = 0
        for file_path in files:
            if str(file_path) in self.cancelled_files or str(file_path) in self.skip_pattern_mismatch:
                continue
            if self._should_ignore_file(file_path.stem) or not self._pattern_found_in_stem(file_path.stem):
                continue
            try:
                st = file_path.stat()
            except (FileNotFoundError, OSError):
                continue
            key = str(file_path)
            if key in self.ready_order_set:
                continue
            self.ready_files.add(file_path)
            self.ready_order_queue.append(key)
            self.ready_order_set.add(key)
            self.seen_files[file_path] = (st.st_size, st.st_mtime)
            count += 1
        return count

    def _process_new_file(self, file_path: Path) -> None:
        if self.pending_locked_file and str(file_path) != self.pending_locked_file:
            return
        if not file_path.exists():
            self.file_retry_counts.pop(str(file_path), None)
            if self.pending_locked_file == str(file_path):
                self.pending_locked_file = None
            return
        if str(file_path) in self.cancelled_files:
            return
        if not self.profiles:
            self.log(f"Kein Profil fuer Datei: {file_path.name}")
            return

        profile = self.profiles[0]
        output_dir = Path(profile["output_folder"].strip() or self.config.output_folder.strip())
        if not output_dir.is_dir():
            self.log(f"Ungueltiger Ausgabeordner in Profil {profile['name']}: {output_dir}")
            return
        if not self._pattern_found_in_stem(file_path.stem):
            self.skip_pattern_mismatch.add(str(file_path))
            self.log(f"Muster fehlt — uebersprungen: {file_path.name}")
            return

        kept_suffix = self._pick_suffix_to_keep(file_path.stem)
        base_name = self._remove_date_token(file_path.stem)
        base_name = self._remove_trailing_suffixes(base_name)
        tag_text = profile["tag"].strip()
        if tag_text:
            new_name = f"{base_name}_{tag_text}{kept_suffix}.mp4" if base_name else f"{profile['name']}_{tag_text}{kept_suffix}.mp4"
        else:
            new_name = f"{base_name}{kept_suffix}.mp4" if base_name else f"{profile['name']}{kept_suffix}.mp4"

        target_path = self._make_unique_path(output_dir / new_name)
        try:
            shutil.move(str(file_path), str(target_path))
            self.processed_count += 1
            self.profiles.pop(0)
            self.file_retry_counts.pop(str(file_path), None)
            if self.pending_locked_file == str(file_path):
                self.pending_locked_file = None
            self.log(f"Datei #{self.processed_count}: {file_path.name} -> {target_path.name}")
        except PermissionError as exc:
            self._schedule_retry(file_path, f"Datei gesperrt: {exc}")
        except Exception as exc:
            msg = str(exc).lower()
            if "used by another process" in msg or "permission denied" in msg:
                self._schedule_retry(file_path, f"Datei evtl. gesperrt: {exc}")
            else:
                self.log(f"Verschieben fehlgeschlagen fuer {file_path.name}: {exc}")

    def _schedule_retry(self, file_path: Path, reason: str) -> None:
        key = str(file_path)
        count = self.file_retry_counts.get(key, 0) + 1
        self.file_retry_counts[key] = count
        self.pending_locked_file = key
        if count > self.max_move_retries:
            self._cancel_file_and_consume_profile(file_path, f"Aufgegeben nach {self.max_move_retries} Versuchen")
            return
        self.log(f"Wiederholung {count}/{self.max_move_retries} fuer {file_path.name} ({reason})")
        threading.Timer(self.retry_delay_ms / 1000.0, lambda: self.ui_queue.put(("process_file", key))).start()

    def _cancel_file_and_consume_profile(self, file_path: Path, reason: str) -> None:
        key = str(file_path)
        self.cancelled_files.add(key)
        self.file_retry_counts.pop(key, None)
        if self.pending_locked_file == key:
            self.pending_locked_file = None
        self.ready_order_set.discard(key)
        self.ready_order_queue = [p for p in self.ready_order_queue if p != key]
        self.ready_files.discard(file_path)
        self.seen_files.pop(file_path, None)
        self.first_stable_at.pop(file_path, None)
        if self.profiles:
            removed = self.profiles.pop(0)
            self.log(f"{reason}: {file_path.name} uebersprungen; Profil {removed['name']} entfernt.")
        else:
            self.log(f"{reason}: {file_path.name} uebersprungen.")

    def _parse_suffix_list(self, raw_text: str) -> list[str]:
        values = []
        for part in raw_text.split(","):
            value = part.strip()
            if not value:
                continue
            if not value.startswith("_"):
                value = f"_{value}"
            values.append(value.lower())
        return values

    def _pick_suffix_to_keep(self, original_stem: str) -> str:
        stem_lower = original_stem.lower()
        keep_list = self._parse_suffix_list(self.config.keep_suffix)
        drop_list = self._parse_suffix_list(self.config.drop_suffix)
        for suffix in keep_list:
            if stem_lower.endswith(suffix):
                return "" if suffix in drop_list else original_stem[-len(suffix) :]
        for suffix in drop_list:
            if stem_lower.endswith(suffix):
                return ""
        return ""

    def _should_ignore_file(self, original_stem: str) -> bool:
        stem_lower = original_stem.lower()
        for suffix in self._parse_suffix_list(self.config.ignore_suffix):
            if stem_lower.endswith(suffix):
                return True
        return False

    def _extract_pattern_match(self, original_stem: str) -> str:
        pattern_text = (self.config.pattern_to_replace or "YYMMDDHHmmSS").strip()
        pattern_text = pattern_text.replace("{", "").replace("}", "")
        token_map = {
            "YYYY": r"(?P<YYYY>\d{4})",
            "YY": r"(?P<YY>\d{2})",
            "MM": r"(?P<MM>\d{2})",
            "DD": r"(?P<DD>\d{2})",
            "HH": r"(?P<HH>\d{2})",
            "mm": r"(?P<mm>\d{2})",
            "SS": r"(?P<SS>\d{2})",
            "DIGITS": r"(?P<DIGITS>\d+)",
            "LETTERS": r"(?P<LETTERS>[A-Za-z]+)",
            "ALNUM": r"(?P<ALNUM>[A-Za-z0-9]+)",
            "ANY": r"(?P<ANY>.+?)",
        }
        token_regex = re.escape(pattern_text)
        for token in ["YYYY", "YY", "MM", "DD", "HH", "mm", "SS", "DIGITS", "LETTERS", "ALNUM", "ANY"]:
            token_regex = token_regex.replace(re.escape(token), token_map[token])
        match = re.search(token_regex, original_stem)
        if not match:
            return pattern_text if pattern_text in original_stem else ""
        return match.group(0)

    def _pattern_found_in_stem(self, original_stem: str) -> bool:
        return bool(self._extract_pattern_match(original_stem))

    def _remove_date_token(self, original_stem: str) -> str:
        found = self._extract_pattern_match(original_stem)
        if not found:
            return original_stem.strip("_- ")
        cleaned = original_stem.replace(found, "")
        while "__" in cleaned:
            cleaned = cleaned.replace("__", "_")
        while "--" in cleaned:
            cleaned = cleaned.replace("--", "-")
        return cleaned.strip("_- ")

    def _remove_trailing_suffixes(self, stem: str) -> str:
        all_suffixes = self._parse_suffix_list(self.config.keep_suffix) + self._parse_suffix_list(self.config.drop_suffix)
        current = stem
        changed = True
        while changed and current:
            changed = False
            lower_current = current.lower()
            for suffix in all_suffixes:
                if lower_current.endswith(suffix):
                    current = current[: -len(suffix)].rstrip("_- ")
                    changed = True
                    break
        return current

    @staticmethod
    def _make_unique_path(path: Path) -> Path:
        if not path.exists():
            return path
        base = path.stem
        suffix = path.suffix
        folder = path.parent
        counter = 1
        while True:
            candidate = folder / f"{base}_{counter}{suffix}"
            if not candidate.exists():
                return candidate
            counter += 1
