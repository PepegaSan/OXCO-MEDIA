# -*- coding: utf-8 -*-
"""Laden und Speichern der Konfiguration (config.json)."""

from __future__ import annotations

import json
import uuid
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Literal

IfType = Literal["extension", "filename", "source_url"]
Condition = Literal["contains", "equals"]
Action = Literal["move", "delete", "ignore"]

CONFIG_FILENAME = "config.json"


@dataclass
class RuleCriterion:
    """
    Ein Filterkriterium: ein Typ + Bedingung, mehrere Werte = logisches ODER.

    Beispiel: Dateiendung „enthält“ mit Werten jpg und jpeg.
    """

    if_type: IfType = "extension"
    condition: Condition = "contains"
    values: list[str] = field(default_factory=list)

    def __post_init__(self) -> None:
        if not self.values:
            self.values = [""]

    def to_dict(self) -> dict[str, Any]:
        return {"if_type": self.if_type, "condition": self.condition, "values": self.values}

    @staticmethod
    def from_dict(d: dict[str, Any]) -> "RuleCriterion":
        vals = d.get("values")
        if isinstance(vals, list) and vals:
            value_list = [str(v) for v in vals]
        else:
            value_list = [str(d.get("value", ""))]
        if not value_list:
            value_list = [""]
        return RuleCriterion(
            if_type=_coerce_if_type(d.get("if_type", "extension")),
            condition=_coerce_condition(d.get("condition", "contains")),
            values=value_list,
        )


@dataclass
class Rule:
    """
    Regel mit einer Liste von Kriterien.

    Alle Kriterien müssen zur selben Datei passen (logisches UND).
    Mehrere Regeln in der Konfiguration: erste passende Regel gewinnt (ODER-Priorität).
    """

    criteria: list[RuleCriterion] = field(default_factory=list)
    action: Action = "move"
    target_folder: str = ""

    def __post_init__(self) -> None:
        if not self.criteria:
            self.criteria = [RuleCriterion()]

    def to_dict(self) -> dict[str, Any]:
        return {
            "criteria": [c.to_dict() for c in self.criteria],
            "action": self.action,
            "target_folder": self.target_folder,
        }

    @staticmethod
    def from_dict(d: dict[str, Any]) -> "Rule":
        crit_raw = d.get("criteria")
        if isinstance(crit_raw, list) and crit_raw:
            criteria = [RuleCriterion.from_dict(x) for x in crit_raw if isinstance(x, dict)]
            if not criteria:
                criteria = [RuleCriterion()]
        else:
            # Alte config.json: ein Kriterium aus den Top-Level-Feldern
            criteria = [
                RuleCriterion(
                    if_type=_coerce_if_type(d.get("if_type", "extension")),
                    condition=_coerce_condition(d.get("condition", "contains")),
                    values=[str(d.get("value", ""))],
                )
            ]
        return Rule(
            criteria=criteria,
            action=_coerce_action(d.get("action", "move")),
            target_folder=str(d.get("target_folder", "")),
        )


@dataclass
class WatchProfile:
    """Überwachungsprofil: eigener Ordner, eigene Regeln, optional parallel aktiv (run_enabled)."""

    profile_id: str = ""
    name: str = "Profil 1"
    watch_folder: str = ""
    rules: list[Rule] = field(default_factory=list)
    run_enabled: bool = False

    def __post_init__(self) -> None:
        if not self.profile_id:
            self.profile_id = str(uuid.uuid4())
        if not self.rules:
            self.rules = [Rule()]

    def to_dict(self) -> dict[str, Any]:
        return {
            "profile_id": self.profile_id,
            "name": self.name,
            "watch_folder": self.watch_folder,
            "rules": [r.to_dict() for r in self.rules],
            "run_enabled": self.run_enabled,
        }

    @staticmethod
    def from_dict(d: dict[str, Any]) -> "WatchProfile":
        rules_raw = d.get("rules") or []
        rules = [Rule.from_dict(x) for x in rules_raw if isinstance(x, dict)] if rules_raw else []
        if not rules:
            rules = [Rule()]
        pid = str(d.get("profile_id") or d.get("id") or "").strip()
        if not pid:
            pid = str(uuid.uuid4())
        return WatchProfile(
            profile_id=pid,
            name=str(d.get("name", "Profil")),
            watch_folder=str(d.get("watch_folder", "")),
            rules=rules,
            run_enabled=bool(d.get("run_enabled", False)),
        )


@dataclass
class AppConfig:
    """Globale App-Einstellungen plus eine oder mehrere WatchProfile."""

    watch_folder: str = ""
    settle_delay_seconds: float = 1.5
    stable_poll_interval_seconds: float = 0.4
    max_wait_seconds: float = 120.0
    rules: list[Rule] = field(default_factory=list)
    ui_language: str = "de"
    ui_appearance: str = "dark"
    profiles: list[WatchProfile] = field(default_factory=list)

    def to_dict(self) -> dict[str, Any]:
        profs = self.profiles if self.profiles else _legacy_single_profile(self)
        out: dict[str, Any] = {
            "settle_delay_seconds": self.settle_delay_seconds,
            "stable_poll_interval_seconds": self.stable_poll_interval_seconds,
            "max_wait_seconds": self.max_wait_seconds,
            "ui_language": self.ui_language,
            "ui_appearance": self.ui_appearance,
            "profiles": [p.to_dict() for p in profs],
        }
        if profs:
            out["watch_folder"] = profs[0].watch_folder
            out["rules"] = [r.to_dict() for r in profs[0].rules]
        else:
            out["watch_folder"] = self.watch_folder
            out["rules"] = [r.to_dict() for r in self.rules] if self.rules else [Rule().to_dict()]
        return out

    @staticmethod
    def from_dict(d: dict[str, Any]) -> AppConfig:
        lang = str(d.get("ui_language", "de")).lower()
        if lang not in ("de", "en"):
            lang = "de"
        app_mode = str(d.get("ui_appearance", "dark")).lower()
        if app_mode not in ("dark", "light"):
            app_mode = "dark"
        base = AppConfig(
            watch_folder=str(d.get("watch_folder", "")),
            settle_delay_seconds=float(d.get("settle_delay_seconds", 1.5)),
            stable_poll_interval_seconds=float(d.get("stable_poll_interval_seconds", 0.4)),
            max_wait_seconds=float(d.get("max_wait_seconds", 120.0)),
            rules=[],
            ui_language=lang,
            ui_appearance=app_mode,
            profiles=[],
        )
        profiles_raw = d.get("profiles")
        if isinstance(profiles_raw, list) and profiles_raw:
            base.profiles = [WatchProfile.from_dict(x) for x in profiles_raw if isinstance(x, dict)]
            if not base.profiles:
                base.profiles = [_migrate_legacy_to_profile(d)]
        else:
            base.profiles = [_migrate_legacy_to_profile(d)]
        first = base.profiles[0]
        base.watch_folder = first.watch_folder
        base.rules = [Rule.from_dict(r.to_dict()) for r in first.rules]
        return base

    def copy(self) -> "AppConfig":
        """Thread-sichere Kopie (über JSON-Dict-Roundtrip, keine Tk-Widgets)."""
        return AppConfig.from_dict(self.to_dict())


def _migrate_legacy_to_profile(d: dict[str, Any]) -> WatchProfile:
    """config ohne profiles[] → ein Profil aus watch_folder + rules."""
    rules_raw = d.get("rules") or []
    rules = [Rule.from_dict(x) for x in rules_raw if isinstance(x, dict)] if rules_raw else []
    if not rules:
        rules = [Rule()]
    return WatchProfile(
        profile_id=str(uuid.uuid4()),
        name="Profil 1",
        watch_folder=str(d.get("watch_folder", "")),
        rules=rules,
        run_enabled=False,
    )


def _legacy_single_profile(cfg: AppConfig) -> list[WatchProfile]:
    if cfg.profiles:
        return cfg.profiles
    return [
        WatchProfile(
            profile_id=str(uuid.uuid4()),
            name="Profil 1",
            watch_folder=cfg.watch_folder,
            rules=list(cfg.rules) if cfg.rules else [Rule()],
            run_enabled=False,
        )
    ]


def runtime_config_for_profile(app: AppConfig, profile: WatchProfile) -> AppConfig:
    """Worker-Snapshot: Wartezeiten aus app, Regeln aus profile (Kopie)."""
    rules_copy = [Rule.from_dict(r.to_dict()) for r in profile.rules]
    return AppConfig(
        watch_folder=profile.watch_folder,
        settle_delay_seconds=app.settle_delay_seconds,
        stable_poll_interval_seconds=app.stable_poll_interval_seconds,
        max_wait_seconds=app.max_wait_seconds,
        rules=rules_copy,
        ui_language=app.ui_language,
        profiles=[],
    )


def config_path(base_dir: Path | None = None) -> Path:
    root = base_dir or Path(__file__).resolve().parent
    return root / CONFIG_FILENAME


def ensure_profiles(cfg: AppConfig) -> None:
    """Stellt sicher, dass mindestens ein Profil existiert (neue/leere Konfiguration)."""
    if cfg.profiles:
        return
    cfg.profiles = [
        WatchProfile(
            name="Profil 1",
            watch_folder=cfg.watch_folder,
            rules=list(cfg.rules) if cfg.rules else [Rule()],
            run_enabled=False,
        )
    ]
    first = cfg.profiles[0]
    cfg.watch_folder = first.watch_folder
    cfg.rules = [Rule.from_dict(r.to_dict()) for r in first.rules]


def load_config(base_dir: Path | None = None) -> AppConfig:
    path = config_path(base_dir)
    if not path.is_file():
        cfg = AppConfig()
        ensure_profiles(cfg)
        return cfg
    try:
        with path.open("r", encoding="utf-8") as fh:
            data = json.load(fh)
        if not isinstance(data, dict):
            cfg = AppConfig()
            ensure_profiles(cfg)
            return cfg
        cfg = AppConfig.from_dict(data)
        ensure_profiles(cfg)
        return cfg
    except (OSError, json.JSONDecodeError):
        cfg = AppConfig()
        ensure_profiles(cfg)
        return cfg


def save_config(cfg: AppConfig, base_dir: Path | None = None) -> None:
    path = config_path(base_dir)
    tmp = path.with_suffix(path.suffix + ".tmp")
    text = json.dumps(cfg.to_dict(), ensure_ascii=False, indent=2)
    with tmp.open("w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)
    tmp.replace(path)


def _coerce_if_type(v: Any) -> IfType:
    s = str(v).lower()
    if s in ("extension", "filename", "source_url"):
        return s  # type: ignore[return-value]
    return "extension"


def _coerce_condition(v: Any) -> Condition:
    s = str(v).lower()
    if s in ("contains", "equals"):
        return s  # type: ignore[return-value]
    return "contains"


def _coerce_action(v: Any) -> Action:
    s = str(v).lower()
    if s in ("move", "delete", "ignore"):
        return s  # type: ignore[return-value]
    return "move"
