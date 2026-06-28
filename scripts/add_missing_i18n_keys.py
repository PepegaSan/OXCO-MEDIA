#!/usr/bin/env python3
import json
from pathlib import Path

p = Path(__file__).resolve().parent / "ui_messages.json"
m = json.loads(p.read_text(encoding="utf-8"))

add = {
    "oxco.colorLegend": {
        "de": "Gleiche Pastellfarbe = gleiche Länge/Auflösung (ffprobe). Gelb = passend zum gewählten Original. Grün = bester Treffer. Dicker blauer Rand = markiert / aktives Paar.",
        "en": "Same pastel color = same length/resolution (ffprobe). Yellow = match for selected original. Green = best hit. Thick blue border = marked / active pair.",
    },
    "oxco.legendMarkedSelected": {"de": "Markiert / Ausgewählt", "en": "Marked / selected"},
    "oxco.deepfakeListHint": {
        "de": "Deepfake-Liste: Rechtsklick → verschieben / Papierkorb. Mehrfachauswahl für Batch-Compare.",
        "en": "Deepfake list: right-click → move / trash. Multi-select for batch compare.",
    },
    "oxco.tagHintFull": {
        "de": "Ersetzt das Datums-Muster — frei eintippen oder aus Tag-Verteilung wählen, danach eigene Ergänzungen möglich.",
        "en": "Replaces the date pattern — type freely or pick from tag distribution, then add your own text.",
    },
    "oxco.scopeHintFull": {
        "de": "Keine Markierung: alle Dateien. Mit Markierung: nur gewählte Zeilen.",
        "en": "No selection: all files. With selection: selected rows only.",
    },
    "oxco.batchPipeline": {
        "de": "Batch-Pipeline: Analyse während DaVinci-Render",
        "en": "Batch pipeline: analyze during DaVinci render",
    },
    "oxco.applyPreset": {"de": "Preset übernehmen", "en": "Apply preset"},
    "oxco.onlyLowerNever": {"de": "Nur senken (nie erhöhen)", "en": "Only lower (never raise)"},
    "oxco.deleteOriginalAfterOk": {
        "de": "Nach erfolgreicher Bitrate-Konvertierung: Original im Eingangsordner löschen",
        "en": "After successful bitrate conversion: delete original in input folder",
    },
    "markerupdater.cleanupHintFull": {
        "de": "Findet Marker, deren Start- oder Endzeit über die Videolänge hinausgeht (z. B. durch fehlerhaften AI-Tagger).",
        "en": "Finds markers whose start or end exceeds video length (e.g. from a faulty AI tagger).",
    },
    "markerupdater.pickTag": {"de": "Tag wählen", "en": "Pick tag"},
    "markerupdater.tagDeleteHintFull": {
        "de": "«Tag in Stash löschen» entfernt den Tag-Eintrag. «Von Treffern abnehmen» streicht nur die Zuordnung bei den passenden Szenen.",
        "en": "Delete tag in Stash removes the tag entry. Remove from hits only clears the link on matching scenes.",
    },
    "dlsort.profileHintFull": {
        "de": "Ein Profil überwacht genau einen Ordner. Aktiviere „Aktiv“ und starte den Monitor in der Aktionsleiste.",
        "en": "Each profile watches one folder. Enable Active and start the monitor from the action bar.",
    },
    "dlsort.rulesHintFull": {
        "de": "Regeln werden von oben nach unten geprüft (oben = höchste Priorität, mit ↑/↓ ändern). Alle Kriterien einer Regel müssen passen (UND). „+ ODER“ fügt einem Kriterium Alternativen hinzu, „+ Kriterium (UND)“ verknüpft ein weiteres. Die erste passende Regel gewinnt.",
        "en": "Rules are checked top to bottom (top = highest priority, use arrows to reorder). All criteria in a rule must match (AND). + OR adds alternatives, + criterion (AND) adds another. First matching rule wins.",
    },
    "bitrate.scanHintFull": {
        "de": "Angehakte Zeilen werden konvertiert (nach Scan standardmäßig alle mit Aktion „convert“).",
        "en": "Checked rows are converted (after scan, all with action convert by default).",
    },
    "oxco.previewLinkHintFull": {
        "de": "Pfade starten aus Workflow (Original/Deepfake). Eigene Pfade oder Abweichung deaktiviert die Verknüpfung.",
        "en": "Paths start from workflow (original/deepfake). Custom paths or changes disable the link.",
    },
}
m.update(add)
p.write_text(json.dumps(m, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(f"Added {len(add)} keys")
