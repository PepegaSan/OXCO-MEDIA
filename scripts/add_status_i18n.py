#!/usr/bin/env python3
"""Add remaining status strings and patch partial ViewModels."""
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MSG = ROOT / "scripts" / "ui_messages.json"
VM = ROOT / "src" / "HailMary" / "ViewModels"

pairs = {
    "texttovideo.status.newSegmentHint": (
        "Neuer Abschnitt — Text und Zeiten eingeben, dann hinzufügen.",
        "New segment — enter text and times, then add.",
    ),
    "texttovideo.status.clipboardEmpty": (
        "Zwischenablage enthält keinen Text.",
        "Clipboard has no text.",
    ),
    "texttovideo.status.clipboardPasted": (
        "Text aus Zwischenablage eingefügt.",
        "Text pasted from clipboard.",
    ),
    "texttovideo.status.selectSrt": (
        "Bitte zuerst eine SRT-Datei wählen.",
        "Choose an SRT file first.",
    ),
    "texttovideo.status.needSegmentOrSrt": (
        "Mindestens ein Text-Abschnitt oder SRT nötig.",
        "At least one text segment or SRT required.",
    ),
    "texttovideo.status.needSegmentOrSrtBatch": (
        "Mindestens ein Text-Abschnitt oder SRT für den Batch nötig.",
        "At least one text segment or SRT required for batch.",
    ),
    "datensync.status.fillSourceTarget": (
        "Quelle und Ziel ausfüllen.",
        "Fill in source and target.",
    ),
    "datensync.status.selectProfile": (
        "Profil im Dropdown auswählen.",
        "Select a profile from the dropdown.",
    ),
    "markerupdater.status.noMarkersSelected": (
        "Keine Marker ausgewählt.",
        "No markers selected.",
    ),
    "markerupdater.status.tagNotOnScene": (
        "Im gewählten Umfang hat keine Szene diesen Tag — nichts geändert.",
        "No scene in scope has this tag — nothing changed.",
    ),
    "markerupdater.status.pickTagToDelete": (
        "Tag-Eintrag in Stash zum Löschen wählen.",
        "Choose a tag entry in Stash to delete.",
    ),
    "oxco.status.pickOriginal": (
        "Zuerst ein Original in der Liste auswählen.",
        "Select an original in the list first.",
    ),
    "oxco.status.noDeepfake": (
        "Kein Deepfake zum Vergleichen (auswählen oder Original klicken).",
        "No deepfake to compare (select one or click original).",
    ),
    "oxco.status.batchDavinciRunning": (
        "Compare-Batch: letzter DaVinci-Export läuft…",
        "Compare batch: last DaVinci export running…",
    ),
    "oxco.status.thresholdsSaved": (
        "Schwellen gespeichert (Filter-Tab + oxco_config.json). Beim Compare-Lauf werden sie in settings.ini übernommen.",
        "Thresholds saved (Filter tab + oxco_config.json). They apply to settings.ini on the next compare run.",
    ),
    "oxco.status.noFilesMarked": (
        "Keine Dateien markiert — alle verarbeiten oder Zeilen wählen.",
        "No files marked — process all or select rows.",
    ),
    "oxco.status.invalidTaggerSelection": (
        "Ungültige Markierung in der Autotagger-Liste — „Liste laden“ und Zeilen wählen.",
        "Invalid selection in autotagger list — load list and pick rows.",
    ),
    "oxco.status.taggerTargetInvalid": (
        "Autotagger-Zielordner ungültig (Tab Pfade).",
        "Autotagger target folder invalid (Paths tab).",
    ),
    "oxco.status.tagDistributionRunning": (
        "Tag-Verteilung läuft…",
        "Tag distribution running…",
    ),
}

m = json.loads(MSG.read_text(encoding="utf-8"))
for k, (de, en) in pairs.items():
    m[k] = {"de": de, "en": en}
MSG.write_text(json.dumps(m, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

de_to_key = {de: k for k, (de, en) in pairs.items()}

patched = 0
for p in VM.rglob("*.cs"):
    t = p.read_text(encoding="utf-8")
    orig = t
    for de, key in sorted(de_to_key.items(), key=lambda x: -len(x[0])):
        t = t.replace(f'"{de}"', f'Loc.T("{key}")')
    if "Loc." in t and "using HailMary.Services;" not in t:
        t = "using HailMary.Services;\n\n" + t
    if t != orig:
        p.write_text(t, encoding="utf-8")
        patched += 1

# regen assets
de = {k: v["de"] for k, v in m.items()}
en = {k: v["en"] for k, v in m.items()}
assets = ROOT / "src" / "HailMary" / "Assets"
(assets / "UiStrings.de.json").write_text(json.dumps(de, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
(assets / "UiStrings.en.json").write_text(json.dumps(en, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(f"Patched {patched} files, {len(pairs)} keys added")
