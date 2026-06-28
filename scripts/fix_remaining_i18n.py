#!/usr/bin/env python3
"""Patch remaining German literals in XAML and ViewModels using ui_messages.json."""
from __future__ import annotations

import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MESSAGES = json.loads((ROOT / "scripts" / "ui_messages.json").read_text(encoding="utf-8"))
DE_TO_KEY: dict[str, str] = {}
for key, pair in MESSAGES.items():
    de = pair.get("de", "")
    if de and de not in DE_TO_KEY:
        DE_TO_KEY[de] = key

VIEWS = ROOT / "src" / "HailMary" / "Views"
VMS = ROOT / "src" / "HailMary" / "ViewModels"

XAML_MAP = {
    "Text": "Key",
    "Content": "Key",
    "Header": "HeaderKey",
    "PlaceholderText": "PlaceholderKey",
    "ToolTipService.ToolTip": "ToolTipKey",
}

# Manual overrides where XAML text differs slightly from catalog
MANUAL_XAML = {
    (
        "Views/OxcoComparePanel.xaml",
        "Text",
        "Gleiche Pastellfarbe = gleiche Länge/Auflösung (ffprobe). Gelb = passend zum gewählten Original. Grün = bester Treffer. Dicker blauer Rand = markiert / aktives Paar.",
    ): "oxco.colorLegend",
    (
        "Views/OxcoComparePanel.xaml",
        "Text",
        "Markiert / Ausgewählt",
    ): "oxco.legendMarkedSelected",
    (
        "Views/OxcoComparePanel.xaml",
        "Text",
        "Deepfake-Liste: Rechtsklick → verschieben / Papierkorb. Mehrfachauswahl für Batch-Compare.",
    ): "oxco.deepfakeListHint",
    (
        "Views/OxcoComparePanel.xaml",
        "Text",
        "Ersetzt das Datums-Muster — frei eintippen oder aus Tag-Verteilung wählen, danach eigene Ergänzungen möglich.",
    ): "oxco.tagHintFull",
    (
        "Views/OxcoComparePanel.xaml",
        "Text",
        "Keine Markierung: alle Dateien. Mit Markierung: nur gewählte Zeilen.",
    ): "oxco.scopeHintFull",
    (
        "Views/OxcoComparePanel.xaml",
        "Content",
        "Batch-Pipeline: Analyse während DaVinci-Render",
    ): "oxco.batchPipeline",
    (
        "Views/OxcoComparePanel.xaml",
        "Content",
        "Preset übernehmen",
    ): "oxco.applyPreset",
    (
        "Views/OxcoComparePanel.xaml",
        "Content",
        "Nur senken (nie erhöhen)",
    ): "oxco.onlyLowerNever",
    (
        "Views/OxcoComparePanel.xaml",
        "Content",
        "Nach erfolgreicher Bitrate-Konvertierung: Original im Eingangsordner löschen",
    ): "oxco.deleteOriginalAfterOk",
    (
        "Views/MarkerUpdaterPanel.xaml",
        "Text",
        "Findet Marker, deren Start- oder Endzeit über die Videolänge hinausgeht (z. B. durch fehlerhaften AI-Tagger).",
    ): "markerupdater.cleanupHintFull",
    (
        "Views/MarkerUpdaterPanel.xaml",
        "Header",
        "Tag wählen",
    ): "markerupdater.pickTag",
    (
        "Views/MarkerUpdaterPanel.xaml",
        "Text",
        "«Tag in Stash löschen» entfernt den Tag-Eintrag. «Von Treffern abnehmen» streicht nur die Zuordnung bei den passenden Szenen.",
    ): "markerupdater.tagDeleteHintFull",
    (
        "Views/DlSortPanel.xaml",
        "Text",
        "Ein Profil überwacht genau einen Ordner. Aktiviere „Aktiv“ und starte den Monitor in der Aktionsleiste.",
    ): "dlsort.profileHintFull",
    (
        "Views/DlSortPanel.xaml",
        "Text",
        "Regeln werden von oben nach unten geprüft (oben = höchste Priorität, mit ↑/↓ ändern). Alle Kriterien einer Regel müssen passen (UND). „+ ODER“ fügt einem Kriterium Alternativen hinzu, „+ Kriterium (UND)“ verknüpft ein weiteres. Die erste passende Regel gewinnt.",
    ): "dlsort.rulesHintFull",
    (
        "Views/BitrateChangerPanel.xaml",
        "Text",
        "Angehakte Zeilen werden konvertiert (nach Scan standardmäßig alle mit Aktion „convert“).",
    ): "bitrate.scanHintFull",
    (
        "Views/Controls/OxcoComparePreviewHost.xaml",
        "Text",
        "Pfade starten aus Workflow (Original/Deepfake). Eigene Pfade oder Abweichung deaktiviert die Verknüpfung.",
    ): "oxco.previewLinkHintFull",
}


def ensure_loc_ns(text: str) -> str:
    if "xmlns:loc=" in text:
        return text
    return text.replace(
        'xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"',
        'xmlns:loc="using:HailMary.Services"\n    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"',
        1,
    )


def patch_xaml(path: Path) -> int:
    rel = str(path.relative_to(ROOT / "src" / "HailMary")).replace("\\", "/")
    text = path.read_text(encoding="utf-8")
    orig = text
    count = 0
    for (file, attr, val), key in MANUAL_XAML.items():
        if file != rel:
            continue
        loc_attr = XAML_MAP[attr]
        old = f'{attr}="{val}"'
        new = f'loc:Loc.{loc_attr}="{key}"'
        if old in text:
            text = text.replace(old, new)
            count += 1
    if count:
        text = ensure_loc_ns(text)
        path.write_text(text, encoding="utf-8")
    return count


def patch_cs(path: Path) -> int:
    text = path.read_text(encoding="utf-8")
    orig = text
    count = 0

    def repl_string(match: re.Match[str]) -> str:
        nonlocal count
        literal = match.group(0)
        inner = literal[1:-1]
        inner = bytes(inner, "utf-8").decode("unicode_escape") if "\\" in inner else inner
        if inner not in DE_TO_KEY:
            return literal
        key = DE_TO_KEY[inner]
        count += 1
        if "{0}" in inner or "{1}" in inner:
            return f'Loc.F("{key}"'  # incomplete - skip format strings
        return f'Loc.T("{key}")'

    # Simple assignments only
    for de, key in sorted(DE_TO_KEY.items(), key=lambda x: -len(x[0])):
        if not de or len(de) < 3:
            continue
        if "{0}" in de:
            continue
        esc = de.replace("\\", "\\\\").replace('"', '\\"')
        patterns = [
            f'= "{esc}"',
            f'= "{de}"',
            f'("{esc}")',
            f'("{de}")',
        ]
        for pat in patterns:
            if pat.startswith('= "') and pat in text:
                text = text.replace(pat, f'= Loc.T("{key}")')
                count += 1
            elif pat.startswith('("') and f'Loc.T("{key}")' not in text:
                pass

    if "Loc." in text and "using HailMary.Services;" not in text:
        text = "using HailMary.Services;\n\n" + text

    if text != orig:
        path.write_text(text, encoding="utf-8")
    return count


xaml_n = sum(patch_xaml(p) for p in VIEWS.rglob("*.xaml"))
cs_n = sum(patch_cs(p) for p in VMS.rglob("*.cs"))
print(f"XAML patches: {xaml_n}, CS patches: {cs_n}")

# Regenerate asset json from messages
de = {k: v["de"] for k, v in MESSAGES.items()}
en = {k: v["en"] for k, v in MESSAGES.items()}
assets = ROOT / "src" / "HailMary" / "Assets"
(assets / "UiStrings.de.json").write_text(json.dumps(de, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
(assets / "UiStrings.en.json").write_text(json.dumps(en, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print("Regenerated UiStrings")
