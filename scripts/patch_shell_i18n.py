#!/usr/bin/env python3
from pathlib import Path

root = Path(__file__).resolve().parent.parent / "src" / "HailMary" / "ViewModels"
replacements = [
    ('"Originales GUI…"', 'Loc.T("common.originalGui")'),
    ('"Originales GUI..."', 'Loc.T("common.originalGui")'),
    ('"Volles GUI öffnen"', 'Loc.T("intro.openFullGui")'),
    ('"Plus-GUI (Vorschau)…"', 'Loc.T("common.plusGui")'),
    ('"Mit FFmpeg exportieren"', 'Loc.T("texttovideo.primaryAction")'),
    ('"Compare starten"', 'Loc.T("oxco.primaryAction")'),
    ('"Konvertierung starten"', 'Loc.T("bitrate.primaryAction")'),
    ('"Monitor starten"', 'Loc.T("common.startMonitor")'),
    ('"Jobs starten"', 'Loc.T("datensync.primaryAction")'),
    ('"Suchen"', 'Loc.T("stashpathfinder.primaryAction")'),
    ('"Szene speichern"', 'Loc.T("markerupdater.primaryAction")'),
    ('"Tool öffnen"', 'Loc.T("common.openTool")'),
    (
        '? "DaVinci Export starten" : "Export starten"',
        '? Loc.T("scenecutter.primaryAction.davinci") : Loc.T("scenecutter.primaryAction.export")',
    ),
]

intro_primary = '''    public string PrimaryActionLabel
    {
        get
        {
            var included = BatchItems.Count(i => i.IsIncluded);
            return included > 1 ? Loc.F("intro.primaryAction.batch", included) : Loc.T("intro.primaryAction");
        }
    }'''

for p in root.rglob("*.Shell.cs"):
    t = p.read_text(encoding="utf-8")
    orig = t
    if "Loc." in t or any(old in t for old, _ in replacements):
        if "using HailMary.Services;" not in t:
            t = "using HailMary.Services;\n\n" + t
    for old, new in replacements:
        t = t.replace(old, new)
    if p.name == "IntroCutterViewModel.Shell.cs" and "Schnitt starten" in t:
        import re
        t = re.sub(
            r"public string PrimaryActionLabel\s*\{[^}]+\}[^}]+\}",
            intro_primary,
            t,
            count=1,
            flags=re.DOTALL,
        )
    if t != orig:
        p.write_text(t, encoding="utf-8")
        print(p.name)
