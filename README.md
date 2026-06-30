# OXCO-MEDIA / Hail Mary

A **Windows desktop app** that brings many OXCO media tools into one place: cut videos, compare (Oxco), adjust bitrate, Stash integration, folder sync, download sorting, and more.

> Repository: [github.com/PepegaSan/OXCO-MEDIA](https://github.com/PepegaSan/OXCO-MEDIA)

---

## Before you start

### FFmpeg is required

Most video tools in Hail Mary call **FFmpeg** and **ffprobe** (convert, cut, read metadata). Without FFmpeg, most actions will fail.

- Download: [ffmpeg.org](https://ffmpeg.org/download.html) (Windows builds e.g. from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/))
- After install, `ffmpeg` and `ffprobe` must be available in a **Command Prompt** (on your PATH)
- Quick test: run `ffmpeg -version` — if that works, Hail Mary is good to go

### DaVinci Resolve — Free vs. Studio

Some tools talk to **DaVinci Resolve** via its API (timeline, export, batch jobs):

| Tool (examples) | Resolve needed? |
|-----------------|-----------------|
| Scene Cutter, Intro Cutter (Resolve export) | Yes |
| Marker Autocut | Yes |
| **DaVinci Batch Render** | **Yes — DaVinci Resolve Studio only** |
| Clip Joiner (Resolve path) | Yes |
| Oxco (DaVinci export) | Yes |

The **free Resolve version** is enough for some exports, **not for everything** — especially **Batch Render and automated Studio features** require **DaVinci Resolve Studio** (paid). Resolve must be installed; set the API path and `Resolve.exe` in the app **Settings**.

### Python

The app UI is **not Python** — but **Python scripts** run in the background (`bridges/`). You need Python on the machine, or run **`setup_python.bat`** once (see below).

### Stash (optional)

Tools like Stash Cutter or Marker Updater need a running **[Stash](https://github.com/stashapp/stash)** instance — only if you use those features.

---

## Installation (for users)

### Option A — From source (clone the repo)

How to run Hail Mary after cloning from GitHub:

**1. Install prerequisites**

| What | Why |
|------|-----|
| **FFmpeg** | Video conversion — used by almost all tools |
| **.NET 8 SDK** | Only for **building** the app — [dotnet.microsoft.com/download](https://dotnet.microsoft.com/download) |
| **DaVinci Resolve (Studio)** | Only for Resolve / batch tools |

**2. Clone the repo**

```powershell
git clone https://github.com/PepegaSan/OXCO-MEDIA.git
cd OXCO-MEDIA
```

**3. Set up Python (once)**

Double-click **`setup_python.bat`**

- If **Python is not installed**, the script tries to install it via **winget** (Windows package manager)
- It creates a **`.venv`** folder and installs required packages (OpenCV, NumPy, Pillow)
- When you start via `start_hail_mary.bat`, that Python is used automatically

**4. Build the app (once, or after code updates)**

Double-click **`build_hail_mary.bat`**

- Compiles the Windows app from source
- Output: `HailMary.exe` under `src\HailMary\bin\`
- **You only need this when the code changed** — not on every launch

**5. Start the app**

Double-click **`start_hail_mary.bat`**

- Launches Hail Mary (builds automatically if the EXE is missing)
- First run: open **Settings (⚙)** → set **Projects root** (workspace for profiles, sync, etc.)
- Optionally check Python path and DaVinci paths in Settings

App settings are stored at: `%LOCALAPPDATA%\HailMary\`

---

### Option B — Ready-made release (planned)

For installation without Visual Studio / dotnet:

1. Download **`HailMary-x64.zip`** from [GitHub Releases](https://github.com/PepegaSan/OXCO-MEDIA/releases) (when published)
2. Extract, e.g. to `C:\Program Files\HailMary\`
3. Run **`setup_python.bat`** once in that folder
4. Install **FFmpeg** (see above)
5. Start **`HailMary.exe`**

A classic setup installer (single `.exe`) can be added later — typically by zipping the release build and wrapping it with Inno Setup.

---

## Batch files explained

| File | Purpose |
|------|---------|
| **`setup_python.bat`** | Python + virtual environment (`.venv`) + packages — run **once** after install |
| **`build_hail_mary.bat`** | **Recompile** the app from source — developers or after updates |
| **`start_hail_mary.bat`** | **Start** Hail Mary — use this day to day |

---

## Included tools

| Area | Tools |
|------|--------|
| **Video** | Scene Cutter, Intro Cutter, Text to Video, Bitrate Changer, Audio Cleaner, Clip Joiner, DaVinci Batch Render |
| **Stash** | Stash Cutter, Marker Autocut, Marker Updater, Stash Pathfinder |
| **Oxco** | Compare, bitrate pipeline, autotagger |
| **Workflow** | Daten Sync, DL Sort, Autotagger monitor |

UI language: **German / English** (Settings → Appearance).

---

## Project layout

```
OXCO-MEDIA/
├── src/HailMary/         # Windows app
├── bridges/              # Python background jobs
├── setup_python.bat      # Set up Python
├── build_hail_mary.bat   # Compile the app
├── start_hail_mary.bat   # Start the app
├── requirements.txt      # Python packages for setup_python.bat
└── README.md
```

---

## License & third parties

- **This repository:** [MIT License](LICENSE)
- **FFmpeg, Resolve, Python packages, NuGet:** [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)

---

## Disclaimer

You are responsible for how you use FFmpeg, DaVinci Resolve, Stash, and other software. No warranty — see LICENSE.
