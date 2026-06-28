#!/usr/bin/env python3
from pathlib import Path
import re

root = Path(__file__).resolve().parent.parent / "src" / "HailMary" / "ViewModels"

vm_files = [
    "AudioCleanerViewModel.cs",
    "AutotaggerViewModel.cs",
    "BitrateChangerViewModel.cs",
    "ClipJoinerViewModel.cs",
    "DatenSyncViewModel.cs",
    "DavinciBatchRenderViewModel.cs",
    "DlSortViewModel.cs",
    "IntroCutterViewModel.cs",
    "MarkerAutocutViewModel.cs",
    "MarkerUpdaterViewModel.cs",
    "OxcoCompareViewModel.cs",
    "SceneCutterViewModel.cs",
    "StashCutterViewModel.cs",
    "StashPathfinderViewModel.cs",
    "TextToVideoViewModel.cs",
    "ToolTabViewModel.cs",
]

refresh_stub = '''
    public void RefreshLocalization()
    {
        LocalizationNotify.Description(this);
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(OpenFullGuiLabel));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ExportButtonLabel));
        OnPropertyChanged(nameof(BatchButtonLabel));
        OnPropertyChanged(nameof(RunCurrentLabel));
        OnPropertyChanged(nameof(BatchSummary));
        OnPropertyChanged(nameof(PartialSelectModeLabel));
        OnPropertyChanged(nameof(PartialSelectHint));
        OnPropertyChanged(nameof(CutSummaryText));
        OnPropertyChanged(nameof(EditorSectionTitle));
        OnPropertyChanged(nameof(SegmentEditorTitle));
    }
'''

for name in vm_files:
    p = root / name
    if not p.exists():
        continue
    t = p.read_text(encoding="utf-8")
    if ", ILocalizable" not in t:
        t = re.sub(
            r"(public partial class \w+ : [^\n{]+)(\n\{)",
            lambda m: m.group(1) + ", ILocalizable" + m.group(2)
            if "ILocalizable" not in m.group(1)
            else m.group(0),
            t,
            count=1,
        )
    if "using HailMary.Services;" not in t:
        t = "using HailMary.Services;\n\n" + t
    p.write_text(t, encoding="utf-8")
    print("vm", name)

    shell = root / name.replace(".cs", ".Shell.cs")
    if not shell.exists():
        # put RefreshLocalization in main file
        if "void RefreshLocalization()" not in t:
            t2 = p.read_text(encoding="utf-8")
            t2 = t2.rstrip()[:-1] + refresh_stub + "\n}\n"
            p.write_text(t2, encoding="utf-8")
        continue

    st = shell.read_text(encoding="utf-8")
    if "void RefreshLocalization()" in st:
        continue
    st = st.rstrip()
    if st.endswith("}"):
        st = st[:-1] + refresh_stub + "\n}\n"
    if "using HailMary.Services;" not in st:
        st = "using HailMary.Services;\n\n" + st
    shell.write_text(st, encoding="utf-8")
    print("shell", shell.name)
