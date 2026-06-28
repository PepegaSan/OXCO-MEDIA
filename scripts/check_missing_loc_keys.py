#!/usr/bin/env python3
import json
import re
from collections import Counter
from pathlib import Path

root = Path(__file__).resolve().parent.parent / "src" / "HailMary"
strings = json.loads((root / "Assets" / "UiStrings.en.json").read_text(encoding="utf-8"))
keys = set(strings)
missing = []
for p in (root / "ViewModels").rglob("*.cs"):
    for m in re.finditer(r'Loc\.T\("([^"]+)"\)', p.read_text(encoding="utf-8")):
        if m.group(1) not in keys:
            missing.append(m.group(1))
for p in (root / "Views").rglob("*.xaml"):
    t = p.read_text(encoding="utf-8")
    for m in re.finditer(r'loc:Loc\.(?:Key|HeaderKey|PlaceholderKey|ToolTipKey)="([^"]+)"', t):
        if m.group(1) not in keys:
            missing.append(m.group(1))
for k, n in sorted(Counter(missing).items()):
    print(f"{k} ({n}x)")
print("total unique:", len(set(missing)))
