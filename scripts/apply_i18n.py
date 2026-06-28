#!/usr/bin/env python3
"""
Apply ui_messages.json to Hail Mary:
  - Writes UiStrings.de.json / UiStrings.en.json
  - Patches Views/**/*.xaml literal German strings → loc:Loc.* attached properties
  - Patches ViewModels/**/*.cs string literals → Loc.T / Loc.F
Skips obj/ and bin/ folders.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SCRIPTS = Path(__file__).resolve().parent
MESSAGES_PATH = SCRIPTS / "ui_messages.json"
ASSETS = ROOT / "src" / "HailMary" / "Assets"
VIEWS = ROOT / "src" / "HailMary" / "Views"
VIEWMODELS = ROOT / "src" / "HailMary" / "ViewModels"

LOC_NS = 'xmlns:loc="using:HailMary.Services"'
USING_LOC = "using HailMary.Services;"

SKIP_DIR_NAMES = {"obj", "bin"}

XAML_ATTRS = {
    "Text": "Key",
    "Content": "Key",
    "Header": "HeaderKey",
    "PlaceholderText": "PlaceholderKey",
    "ToolTipService.ToolTip": "ToolTipKey",
}

# Match attribute="value" where value is a literal (not binding/expression)
XAML_ATTR_RE = re.compile(
    r'(?P<attr>Text|Content|Header|PlaceholderText|ToolTipService\.ToolTip)="(?P<val>(?:[^"\\]|\\.)*)"'
)

# C# string literal (simple — handles \" and \\)
CS_STRING_RE = re.compile(r'"(?:[^"\\]|\\.)*"')

# Skip lines already localized or bound
XAML_SKIP_PATTERNS = (
    "{x:Bind",
    "{Binding",
    "loc:Loc.",
    "AppServices.Localization",
)

CS_SKIP_PATTERNS = (
    "Loc.T(",
    "Loc.F(",
    "AppServices.Localization",
    'Get("',
    "Log.",
    "Path.",
    "Process.",
    "File.",
    "Directory.",
    "JsonSerializer",
    "InvalidOperationException",
    "ArgumentException",
    "throw new",
    "//",
    "/*",
    "$\"Projects-Root",
    "settings.ini",
    "scene_cutter",
    "cutter_app_config",
    "per_file",
    "compilation",
    "ffmpeg",
    "davinci",
    "libx264",
    "YouTube",
    "AutoCutPreset",
    "StashMarker",
    "explorer.exe",
    "python.exe",
    "FFFFFF",
    "888888",
    "mp4",
    "gif",
    "deepfake",
    "both",
    "source",
    "keep",
    "move",
    "delete",
    "ignore",
    "extension",
    "filename",
    "source_url",
    "move_to_backup",
    "Profil 1",
    "Regel 1",
    "Standard",
    "H.264",
    "H.265",
    "VP9",
    "AV1",
    "5000k",
    "System",
    "Light",
    "Dark",
    "de",
    "en",
    "Music",
    "Twitch",
)


def should_skip_dir(path: Path) -> bool:
    return any(part in SKIP_DIR_NAMES for part in path.parts)


def load_messages() -> dict[str, dict[str, str]]:
    with MESSAGES_PATH.open(encoding="utf-8") as f:
        data = json.load(f)
    if not isinstance(data, dict):
        raise SystemExit(f"{MESSAGES_PATH} must be a JSON object")
    return data


def write_catalogs(messages: dict[str, dict[str, str]]) -> tuple[Path, Path]:
    de: dict[str, str] = {}
    en: dict[str, str] = {}
    for key, pair in sorted(messages.items()):
        de[key] = pair["de"]
        en[key] = pair["en"]

    ASSETS.mkdir(parents=True, exist_ok=True)
    de_path = ASSETS / "UiStrings.de.json"
    en_path = ASSETS / "UiStrings.en.json"
    de_path.write_text(json.dumps(de, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    en_path.write_text(json.dumps(en, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return de_path, en_path


def build_de_index(messages: dict[str, dict[str, str]]) -> dict[str, str]:
    """Map exact German text → key (longest de string wins on tie)."""
    by_de: dict[str, list[str]] = defaultdict(list)
    for key, pair in messages.items():
        de = pair["de"]
        if de:
            by_de[de].append(key)

    index: dict[str, str] = {}
    for de, keys in by_de.items():
        keys.sort()
        index[de] = keys[0]
    return index


def unescape_xml(s: str) -> str:
    return (
        s.replace("&amp;", "&")
        .replace("&lt;", "<")
        .replace("&gt;", ">")
        .replace("&quot;", '"')
        .replace("&#39;", "'")
    )


def escape_xml_attr(s: str) -> str:
    return (
        s.replace("&", "&amp;")
        .replace("<", "&lt;")
        .replace(">", "&gt;")
        .replace('"', "&quot;")
    )


def ensure_xaml_loc_namespace(content: str) -> tuple[str, bool]:
    if LOC_NS in content:
        return content, False
    m = re.search(r"<(\w+)([^>]*)>", content)
    if not m:
        return content, False
    tag, attrs = m.group(1), m.group(2)
    if "xmlns:loc=" in attrs:
        return content, False
    new_open = f"<{tag}{attrs} {LOC_NS}>"
    return content.replace(f"<{tag}{attrs}>", new_open, 1), True


def patch_xaml_file(path: Path, de_index: dict[str, str], dry_run: bool) -> int:
    text = path.read_text(encoding="utf-8")
    if any(p in text for p in ("loc:Loc.Key=", "loc:Loc.HeaderKey=")) and not de_index:
        return 0

    changes = 0

    def replacer(m: re.Match[str]) -> str:
        nonlocal changes
        attr = m.group("attr")
        raw_val = m.group("val")
        val = unescape_xml(raw_val)

        if not val or val in ("✓", "#"):
            return m.group(0)
        if any(p in m.group(0) for p in XAML_SKIP_PATTERNS):
            return m.group(0)
        if attr == "Content" and val in ("↑", "↓", "▶", "×", "▲", "▼", "▲▲", "▼▼"):
            return m.group(0)

        key = de_index.get(val)
        if not key:
            return m.group(0)

        loc_prop = XAML_ATTRS[attr]
        # Preserve indentation from line start if possible
        changes += 1
        return f'loc:Loc.{loc_prop}="{key}"'

    new_text = XAML_ATTR_RE.sub(replacer, text)
    if changes:
        new_text, _ = ensure_xaml_loc_namespace(new_text)
        if not dry_run:
            path.write_text(new_text, encoding="utf-8")
    return changes


def cs_unescape(s: str) -> str:
    inner = s[1:-1]
    out: list[str] = []
    i = 0
    while i < len(inner):
        if inner[i] == "\\" and i + 1 < len(inner):
            n = inner[i + 1]
            if n == "n":
                out.append("\n")
            elif n == "r":
                out.append("\r")
            elif n == "t":
                out.append("\t")
            elif n == '"':
                out.append('"')
            elif n == "\\":
                out.append("\\")
            else:
                out.append(n)
            i += 2
        else:
            out.append(inner[i])
            i += 1
    return "".join(out)


def is_format_string(s: str) -> bool:
    return "{0}" in s or "{1}" in s


def csharp_to_loc_call(key: str, de: str) -> str:
    if is_format_string(de):
        return f'Loc.F("{key}"'
    return f'Loc.T("{key}")'


def patch_cs_file(path: Path, de_index: dict[str, str], dry_run: bool) -> int:
    lines = path.read_text(encoding="utf-8").splitlines(keepends=True)
    changed = False
    uses_loc = "Loc." in "".join(lines)
    new_lines: list[str] = []

    for line in lines:
        if any(p in line for p in CS_SKIP_PATTERNS):
            new_lines.append(line)
            if "Loc." in line:
                uses_loc = True
            continue

        def cs_repl(m: re.Match[str]) -> str:
            nonlocal changed, uses_loc
            lit = m.group(0)
            try:
                val = cs_unescape(lit)
            except Exception:
                return lit

            key = de_index.get(val)
            if not key:
                return lit

            uses_loc = True
            changed = True
            call = csharp_to_loc_call(key, val)
            if call.startswith("Loc.F("):
                # Keep surrounding context — only replace the string literal
                return call + ", "  # caller must fix — handle below
            return call

        # Replace string literals on lines that look like UI assignments
        if '"' in line and not line.strip().startswith("//"):
            original = line
            # Process each string literal
            parts: list[str] = []
            last = 0
            for m in CS_STRING_RE.finditer(line):
                lit = m.group(0)
                try:
                    val = cs_unescape(lit)
                except Exception:
                    continue
                key = de_index.get(val)
                if not key:
                    continue
                if any(p in line for p in CS_SKIP_PATTERNS):
                    continue
                # Only replace in likely UI contexts
                ctx = line[: m.start()]
                if not re.search(
                    r"(Status\s*=|ConnectionInfo\s*=|LoadedSummary\s*=|"
                    r"PartialSelectButtonText\s*=>|CommitSegmentLabel\s*=>|"
                    r"SegmentEditorTitle\s*=>|BatchExportLabel\s*=>|"
                    r"BatchButtonLabel\s*=>|PrimaryActionLabel\s*=>|"
                    r"PathSortLabel\s*=>|IntroBarLabel\s*=>|MainBarLabel\s*=>|"
                    r"OutroBarLabel\s*=>|CutSummary\s*=>|BatchSummary\s*=>|"
                    r"RunButtonLabel\s*=>|LocalFileMissingHint\s*=>|"
                    r"TargetFolderDisplay\s*=>|Description\s*=>|Title\s*=>|"
                    r"return\s+\")",
                    ctx,
                ) and "return \"" not in ctx and "? \"" not in line and ": \"" not in line:
                    # Allow ternary and return patterns
                    if not re.search(r'\?\s*"[^"]*"\s*:', line) and 'return "' not in line:
                        continue

                uses_loc = True
                changed = True
                if is_format_string(val):
                    replacement = f'Loc.F("{key}"'
                    # Count placeholders for remaining args after first string
                    parts.append(line[last : m.start()])
                    parts.append(replacement)
                    last = m.end()
                else:
                    parts.append(line[last : m.start()])
                    parts.append(f'Loc.T("{key}")')
                    last = m.end()

            if last > 0:
                parts.append(line[last:])
                line = "".join(parts)

            # Fix Loc.F without closing — if we opened Loc.F, the rest of args stay
            if "Loc.F(" in line and line.count("Loc.F(") > line.count(")"):
                pass  # string.Format style — Loc.F("key", arg1, arg2) needs manual arg preservation

            # Simpler second pass for Status = "..." assignments
            if line == original:
                m = re.search(r'(Status\s*=\s*)"([^"]*(?:\\.[^"]*)*)"', line)
                if m:
                    try:
                        val = cs_unescape(f'"{m.group(2)}"')
                    except Exception:
                        val = m.group(2)
                    key = de_index.get(val)
                    if key:
                        uses_loc = True
                        changed = True
                        if is_format_string(val):
                            line = re.sub(
                                r'Status\s*=\s*"(?:[^"\\]|\\.)*"',
                                f'Status = Loc.F("{key}"',
                                line,
                                count=1,
                            )
                        else:
                            line = re.sub(
                                r'Status\s*=\s*"(?:[^"\\]|\\.)*"',
                                f'Status = Loc.T("{key}")',
                                line,
                                count=1,
                            )

                for prop in ("ConnectionInfo", "LoadedSummary", "BatchSummary", "CutSummary",
                             "IntroBarLabel", "MainBarLabel", "OutroBarLabel"):
                    pm = re.search(rf'({prop}\s*=\s*)"([^"]*(?:\\.[^"]*)*)"', line)
                    if pm:
                        try:
                            val = cs_unescape(f'"{pm.group(2)}"')
                        except Exception:
                            val = pm.group(2)
                        key = de_index.get(val)
                        if key:
                            uses_loc = True
                            changed = True
                            rep = f'{prop} = Loc.T("{key}")' if not is_format_string(val) else f'{prop} = Loc.F("{key}"'
                            line = re.sub(
                                rf'{prop}\s*=\s*"(?:[^"\\]|\\.)*"',
                                rep,
                                line,
                                count=1,
                            )

        new_lines.append(line)

    if changed and uses_loc and USING_LOC not in "".join(new_lines):
        # Insert using after last existing using
        inserted = False
        out: list[str] = []
        for i, line in enumerate(new_lines):
            out.append(line)
            if not inserted and line.startswith("using ") and (
                i + 1 >= len(new_lines) or not new_lines[i + 1].startswith("using ")
            ):
                out.append(USING_LOC + "\n")
                inserted = True
        if not inserted:
            out.insert(0, USING_LOC + "\n")
        new_lines = out

    if changed and not dry_run:
        path.write_text("".join(new_lines), encoding="utf-8")
    return 1 if changed else 0


def iter_files(base: Path, pattern: str) -> list[Path]:
    results: list[Path] = []
    for p in base.rglob(pattern):
        if should_skip_dir(p):
            continue
        results.append(p)
    return sorted(results)


def main() -> int:
    parser = argparse.ArgumentParser(description="Apply ui_messages.json i18n patches")
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Report changes without writing XAML/CS (still writes JSON catalogs unless --no-write-json)",
    )
    parser.add_argument(
        "--no-write-json",
        action="store_true",
        help="Skip writing UiStrings.*.json",
    )
    args = parser.parse_args()

    if not MESSAGES_PATH.is_file():
        print(f"Missing {MESSAGES_PATH}", file=sys.stderr)
        return 1

    messages = load_messages()
    key_count = len(messages)
    de_index = build_de_index(messages)

    if not args.no_write_json:
        de_path, en_path = write_catalogs(messages)
        print(f"Wrote {key_count} keys -> {de_path.relative_to(ROOT)}")
        print(f"Wrote {key_count} keys -> {en_path.relative_to(ROOT)}")
    else:
        print(f"Loaded {key_count} keys from ui_messages.json")

    xaml_files = iter_files(VIEWS, "*.xaml")
    cs_files = iter_files(VIEWMODELS, "*.cs")

    xaml_patched: list[tuple[str, int]] = []
    cs_patched: list[str] = []

    for xf in xaml_files:
        n = patch_xaml_file(xf, de_index, dry_run=args.dry_run)
        if n:
            xaml_patched.append((str(xf.relative_to(ROOT)), n))

    for cf in cs_files:
        if patch_cs_file(cf, de_index, dry_run=args.dry_run):
            cs_patched.append(str(cf.relative_to(ROOT)))

    mode = "Would patch" if args.dry_run else "Patched"
    print()
    print(f"=== Summary ({mode}) ===")
    print(f"Message keys: {key_count}")
    print(f"Unique German strings indexed: {len(de_index)}")
    print(f"XAML files {mode.lower()}: {len(xaml_patched)} ({sum(c for _, c in xaml_patched)} attribute replacements)")
    for rel, count in xaml_patched:
        print(f"  {rel} ({count})")
    print(f"C# ViewModel files {mode.lower()}: {len(cs_patched)}")
    for rel in cs_patched:
        print(f"  {rel}")

    duplicate_de = [de for de, keys in defaultdict(list, {d: [k for k, p in messages.items() if p["de"] == d] for d in {p["de"] for p in messages.values()}}).items() if len(keys) > 1]
    if duplicate_de:
        print(f"\nNote: {len(duplicate_de)} German strings map to multiple keys (first key wins).")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
