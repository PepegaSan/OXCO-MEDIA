#!/usr/bin/env python3
from pathlib import Path
import re

root = Path(__file__).resolve().parent.parent / "src" / "HailMary" / "ViewModels"
stub = '''
    public void RefreshLocalization()
    {
        LocalizationNotify.Description(this);
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(OpenFullGuiLabel));
        OnPropertyChanged(nameof(StatusText));
    }
'''
pattern = re.compile(r"\n    public void RefreshLocalization\(\)\s*\{.*?\n    \}\n", re.DOTALL)

for p in root.rglob("*.Shell.cs"):
    t = p.read_text(encoding="utf-8")
    if "void RefreshLocalization()" not in t:
        continue
    t2 = pattern.sub(stub, t)
    if t2 != t:
        p.write_text(t2, encoding="utf-8")
        print(p.name)

# main VMs with RefreshLocalization in main file
for p in root.glob("*ViewModel.cs"):
    t = p.read_text(encoding="utf-8")
    if "void RefreshLocalization()" not in t:
        continue
    t2 = pattern.sub(stub, t)
    if t2 != t:
        p.write_text(t2, encoding="utf-8")
        print(p.name)
