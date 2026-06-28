# Third-party notices

Hail Mary (OXCO-MEDIA) bundles some libraries and talks to external programs you must install separately. This file lists the most important ones for compliance and attribution.

**Summary:** Application source code in this repository is under the [MIT License](LICENSE). Third-party components keep their own licenses. External tools (FFmpeg, DaVinci Resolve, etc.) are **not** shipped with Hail Mary.

---

## 1. NuGet packages (C# / WinUI shell)

These are referenced in `src/HailMary/HailMary.csproj` and included in a self-contained publish build.

| Component | Version | License | Notes |
|-----------|---------|---------|--------|
| [Microsoft Windows App SDK](https://github.com/microsoft/microsoft-ui-xaml) | 2.2.0 | [Microsoft Software License](https://www.nuget.org/packages/Microsoft.WindowsAppSDK/2.2.0/License) | WinUI 3 runtime (self-contained in publish) |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.2 | [MIT](https://github.com/CommunityToolkit/dotnet/blob/main/License.md) | MVVM helpers |
| [Microsoft.Windows.SDK.BuildTools](https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools) | 10.0.28000.1839 | Microsoft Software License | Build tooling |
| [Microsoft.Windows.SDK.BuildTools.WinApp](https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools.WinApp) | 0.3.2 | See NuGet package page | `dotnet run` support for WinUI |

.NET 8 and the Windows SDK are subject to [Microsoft’s terms](https://dotnet.microsoft.com/en-us/dotnet_library_license.htm).

Full license texts for NuGet packages: open each package on [nuget.org](https://www.nuget.org) or inspect `%USERPROFILE%\.nuget\packages` after restore.

---

## 2. Optional Python packages (not bundled)

Bridge jobs under `bridges/` run with **your** Python installation. Standard library only for many tools; some features need extra packages:

| Package | Used for | Typical license |
|---------|----------|-----------------|
| [OpenCV (`opencv-python`)](https://github.com/opencv/opencv-python) | Oxco compare preview, frame diff, clip probe | Apache-2.0 |
| [NumPy](https://numpy.org/) | Oxco preview / image buffers | BSD-3-Clause |
| [Pillow](https://python-pillow.org/) | Oxco preview encoding | HPIL (similar to BSD) |
| [DeepFilterNet](https://github.com/Rikorose/DeepFilterNet) (`deepfilternet` or `deep-filter.exe`) | Audio Cleaner noise reduction | MIT (see upstream repo) |

Example install (adjust versions as needed):

```bash
pip install opencv-python numpy Pillow
pip install deepfilternet   # optional, for Audio Cleaner
```

There is no `requirements.txt` in the repo yet; Python deps depend on which tools you use.

---

## 3. External programs (not included in this repository)

You must install and license these yourself. Hail Mary invokes them via `PATH`, settings, or subprocess.

| Program | Role in Hail Mary | License / terms |
|---------|-------------------|-----------------|
| [FFmpeg / ffprobe](https://ffmpeg.org/) | Encode, probe, concat, bitrate, audio, text overlays | LGPL-2.1+ / GPL-2+ (build-dependent); [legal FAQ](https://ffmpeg.org/legal.html) |
| [Python 3](https://www.python.org/) | Runs all `bridges/*.py` jobs | PSF License |
| [DaVinci Resolve](https://www.blackmagicdesign.com/products/davinciresolve) | Scene Cutter, Intro Cutter, Marker Autocut, Batch Render, Oxco export, Clip Joiner (Resolve path) | Proprietary — [Blackmagic Design EULA](https://www.blackmagicdesign.com/) |
| [Robocopy](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/robocopy) | Daten Sync | Part of Windows |
| [Stash](https://github.com/stashapp/stash) | Stash Cutter, Marker tools, Pathfinder, GraphQL (optional) | AGPL-3.0 — integration only; Stash is not bundled |
| NVIDIA / AMD / Intel encoders | Optional hardware encode (`h264_nvenc`, etc.) | Vendor drivers and terms |

**DeepFilterNet CLI:** If you use `deep-filter.exe` instead of the Python package, follow [DeepFilterNet’s license](https://github.com/Rikorose/DeepFilterNet).

---

## 4. Vendored Python modules in `bridges/vendor/`

Code under `bridges/vendor/` (Oxco, Scene Cutter, Bitrate, Autotagger, etc.) is **part of this repository** and covered by the [MIT License](LICENSE), unless noted otherwise in a file header. It was consolidated from separate OXCO media tooling projects into one tree for the Hail Mary shell.

---

## 5. Fonts and assets

- UI icons and WinUI assets in `src/HailMary/Assets/` are project assets under the MIT License unless a file states otherwise.
- Text-to-video and subtitle rendering may use **fonts you configure** on your system; font licenses are your responsibility.

---

## 6. Trademarks

DaVinci Resolve, Blackmagic Design, Windows, and other names are trademarks of their respective owners. This project is not affiliated with Blackmagic Design or Microsoft.

---

If you redistribute a published build of Hail Mary, you should:

1. Include this file and `LICENSE`.
2. Comply with Microsoft Windows App SDK redistribution terms for self-contained builds.
3. Not bundle FFmpeg or DaVinci Resolve unless you satisfy their respective licenses.
4. Document that users need their own Python, FFmpeg, and optional Resolve installation.
