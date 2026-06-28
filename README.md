# OXCO-MEDIA / Hail Mary

Unified **Windows desktop shell** for OXCO media workflows: one WinUI app that hosts video tools, Stash integrations, Oxco compare, sync, and download sorting. Legacy Python GUIs are replaced or wrapped by **bridge jobs** (`bridges/`) launched from the C# host.

> **Repository:** [github.com/PepegaSan/OXCO-MEDIA](https://github.com/PepegaSan/OXCO-MEDIA)

---

## Features

| Area | Tools |
|------|--------|
| **Video** | Scene Cutter, Intro Cutter, Text to Video, Bitrate Changer, Audio Cleaner, Clip Joiner, DaVinci Batch Render |
| **Stash** | Stash Cutter, Marker Autocut, Marker Updater, Stash Pathfinder |
| **Oxco** | Compare, bitrate pipeline, autotagger integration |
| **Workflow** | Daten Sync (Robocopy), DL Sort, Autotagger monitor |

UI language: **German / English** (Settings → Appearance).

---

## Requirements

### To run a published build

- **Windows 10/11** (x64)
- **FFmpeg** and **ffprobe** on `PATH`
- **Python 3.10+** (path configurable in Settings)
- **DaVinci Resolve** (optional; required for Resolve export paths)
- Optional Python packages for some tools — see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)

### To build from source

- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- Windows SDK / WinUI workload (installed with Visual Studio 2022 or Build Tools)
- Same runtime deps as above for testing bridges

---

## Quick start (development)

```powershell
git clone https://github.com/PepegaSan/OXCO-MEDIA.git
cd OXCO-MEDIA

# Build
dotnet build src\HailMary\HailMary.csproj -c Debug -p:Platform=x64

# Or use the batch helper
build_hail_mary.bat
start_hail_mary.bat
```

Executable (Debug):

`src\HailMary\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64\HailMary.exe`

### Projects root

Many tools expect a **Projects root** folder (sibling tool repos, configs, sync profiles). On first run, set **Settings → Projects root**, or point `HAIL_MARY_PROJECTS_ROOT` at your workspace when using `start_hail_mary.bat`.

User settings (theme, language, paths) are stored under:

`%LOCALAPPDATA%\HailMary\`

---

## Release build (portable folder)

Self-contained publish — no separate Windows App SDK installer for end users:

```powershell
dotnet publish src\HailMary\HailMary.csproj -c Release -p:Platform=x64
```

Output:

`src\HailMary\bin\Release\net8.0-windows10.0.26100.0\win-x64\publish\`

Zip that folder for a portable release, or wrap it with an installer (e.g. Inno Setup). See GitHub Releases when available.

---

## Project layout

```
OXCO-MEDIA/
├── src/HailMary/          # WinUI 3 app (C#)
├── bridges/               # Python bridge jobs + vendor cores
├── scripts/               # i18n and maintenance scripts
├── HailMary.sln
├── build_hail_mary.bat
├── start_hail_mary.bat
├── LICENSE
├── THIRD_PARTY_NOTICES.md
└── README.md
```

- **`tools.json`** — tool registry (labels, bridge scripts, groups)
- **`scripts/ui_messages.json`** — source for UI strings; run `python scripts/apply_i18n.py` after edits

---

## Configuration

| What | Where |
|------|--------|
| App settings | In-app ⚙ → General / DaVinci / Stash |
| Oxco compare defaults | `bridges/vendor/oxco/settings.example.ini` (copy to your projects tree) |
| i18n | Settings → Language, or edit `scripts/ui_messages.json` |

Do **not** commit personal API keys or local `settings.ini` with secrets. Stash credentials live in local app data, not in the repo.

---

## License

- **This repository (Hail Mary shell + vendored bridge code):** [MIT License](LICENSE) — Copyright (c) 2026 PepegaSan
- **Third-party libraries and external programs:** [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)

---

## Disclaimer

This software automates local video and library workflows. You are responsible for complying with licenses of FFmpeg, DaVinci Resolve, Stash, and any other software you connect. No warranty — see LICENSE.
