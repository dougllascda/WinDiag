# Changelog

All notable changes to WinDiag are documented in this file. The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.3.0] - 2026-11-13

### Added
- **Tools tab** (both profiles): one-click tools with fixed commands. WinDiag shows exactly what will run and asks for confirmation before any tool that changes something or needs administrator rights (read-only tools run directly). It shows the output live with a Cancel button, never restarts the PC, and records each run in `changes.log` and `tools-history.json`. Tools are not reversible, except through a restore point, which is offered before the medium-risk ones.
  - **Printing:** unstick the print queue, restart the Spooler, print a test page.
  - **Network:** connection test (router, 1.1.1.1, 8.8.8.8 and a name lookup: latency, jitter, loss and a plain verdict), flush DNS, renew IP, reset the network stack (the confirmation lists any static IPs it would erase), sync time.
  - **Cleanup:** disk cleanup with selectable items and sizes (temporary files older than 24 hours only; it never follows folder links and never touches a drive root, the user profile, Windows or Program Files), shader cache (DirectX, NVIDIA, AMD).
  - **System:** SFC, DISM, CHKDSK (read-only scan), reset Windows Update, restart Explorer, Windows Memory Diagnostic, create a restore point.
  - **Battery:** battery report.
  - **Shortcuts:** Device Manager, printers, apps, Windows Update, Disk Management, Reliability Monitor.
- **Four new reversible optimizations** (snapshot and undo): turn off mouse acceleration, turn off the Sticky/Filter/Toggle Keys shortcuts, High performance power plan on desktops (Gaming), and lock the default printer (Work).
- **Findings point to the tool** that resolves them, in the findings and in the action plan.
- **Self-update:** Settings > Updates sets the check frequency (On app start, Daily, Weekly by default, Monthly or Manually). The check only reads the latest version from the official repository. A banner offers Update now, What's new, Skip this version and Later. "Update now" asks for confirmation, downloads the file for the installed type (Setup, Portable or fx) only from the official releases, verifies its SHA-256 against the `SHA256SUMS.txt` of the same release (and aborts if it differs), closes the app, installs and reopens it. The installer accepts `/RELAUNCH=1` for this. The executables are not code-signed yet, so the check proves integrity, not authorship.
- **CLI:** `--tools` lists the tools, `--dry-tool ID` shows what a tool would run without running it, `--check-update` queries the latest version and `--download-update` downloads and verifies the file without applying it.

### Changed
- **New visual identity:** new logo (a W-shaped pulse line inside a chip frame, on a navy background), drawn as a vector in `Logo.xaml`, the single source. `tools\make-icon.ps1` generates the `.ico` (16 to 256 px, with a simplified small version up to 24 px), `tools\make-social-preview.ps1` generates `logo.png` and `social-preview.png`, and the new `tools\make-installer-images.ps1` generates the installer side image.
- **Printing and temporary files actions moved from Optimizations to Tools:** restart Spooler, clear print queue and temporary files are now tools. The Optimizations tab has only reversible adjustments.
- **Factory-installed programs** are sorted by Remove, Optional, Keep.
- **Partial score note:** the Work profile score shows a "Partial readings" line when a partial-score cap applies.
- **One operation at a time:** tools, optimizations, uninstalls and updates share a single busy state, and the window cannot be closed in the middle of an operation without cancelling it first, so no service is left stopped.
- **Opened with another administrator account:** per-user adjustments and cleanups are disabled, with an explanation, because they would change the administrator's profile instead of yours.
- **Links and folders** opened from the app no longer inherit administrator rights.

### Fixed
- Timeout message was shown without translation.
- Repeated icon in the menu (Optimizations now has its own icon).
- Tab content was not exposed to screen readers (the `PART_SelectedContentHost` name was missing in the `TabControl` template).
- Output of Windows tools with accented characters was corrupted when the app was in English (OEM code page).
- Text was cut off in a narrow window in Simple mode.
- The connection test picked the VPN adapter instead of the router.

## [1.2.0] - 2026-11-09

### Changed
- **License:** WinDiag is now source-available under the WinDiag License (all rights reserved). The official releases can be downloaded and used for free; copying, modifying, redistributing or selling the code or binaries requires written permission. Pull requests are accepted only under the Contributor License Agreement.
- **Repository reorganized:** `CONTRIBUTING.md` and `SECURITY.md` moved to `.github/`; Portuguese documents moved to `docs/pt-BR/`; `publish.ps1` moved to `tools/publish.ps1`; the Code of Conduct was removed.

### Added
- **Usage profile Gaming | Work:** the Work profile covers printers, factory-installed programs (recommendation and removal through the official uninstaller), laptop battery and power, Windows Update and boot time, and hides the game tabs.
- **Fully documented code:** every type and member of the app and the core library has XML documentation (enforced by an automated test), plus module guides in `docs/modules/` and an API reference site built with DocFX.

## [1.1.1] - 2026-11-08

First public release. Earlier versions were internal and are not published; the features below summarize what the app does today.

### Added
- **CPU-Z/GPU-Z-style inventory** read automatically on launch (collectors run in parallel), with live values every second (per-thread clock, load, RAM, GPU).
- **Overview:** a gaming configuration score (0 to 100, with the loss per area; it is not a benchmark) and clickable hardware cards.
- **Platform analysis:** where the limit is and what can be improved, using measured readings only.
- **Explained diagnostics:** each finding says what it means and what to do, and the likely gaming bottlenecks are ranked by impact. Every check has a stable identifier, independent of the language.
- **Action plan:** ordered steps (free first, then hardware) with expected gain and a "done" mark saved between sessions.
- **Since the last scan:** local history (last 30 scans) and an automatic comparison of what was resolved, what got worse, what is new and what changed in hardware or configuration.
- **Reversible optimizations:** one switch per item, a snapshot before every change, undo for that item alone and a snapshot history. Items already configured outside WinDiag (for example by AtlasOS) are shown as locked. Includes switches for startup apps.
- **Sensors tab** in the style of HWiNFO (current, min, max, average). Reads from HWiNFO with Shared Memory Support enabled and from the bundled LibreHardwareMonitor library (CPU, motherboard and memory also need the PawnIO driver, installed separately by the user). Alerts for temperature, stopped fan, VRM and hot disk; the capture records the maximum temperature and flags thermal throttling.
- **Power supply:** estimate of peak consumption and recommended PSU; with the PSU entered in Settings (or detected on digital Corsair/NZXT units over USB), the app warns if it looks undersized.
- **BIOS tutorials** by vendor (Gigabyte, ASUS, MSI, ASRock and generic): PCIe generation, Resizable BAR, XMP/EXPO, virtualization and BIOS update, including how to go back.
- **Simple / Detailed mode:** Simple (default) uses plain language, with "What is good" and "What to improve and how"; Detailed shows everything.
- **Languages:** interface, diagnostics, glossary (about 70 entries), BIOS tutorials and report in Brazilian Portuguese and English. Automatic follows Windows and can be changed in Settings; numbers and dates follow the language.
- **Settings tab:** theme, mode, language, your power supply, sensor status, data folders and licenses.
- **Monitors:** active mode and refresh rate with decimals (for example 59.96 Hz) without administrator rights, plus the refresh rates supported at the current resolution.
- **Capture and Game session:** live charts, game process detection by 3D GPU load and P50/P95/P99 percentiles; the Game session closes only the heavy background apps you select.
- **Themes:** Automatic, Light and Dark.
- **Report export:** a single HTML file (self-contained, light/dark, print CSS) or TXT, with user name and serial numbers masked in the file.
- **Navigation highlights:** "See details", cards, the plan and the history jump to the item and highlight it; clicking an area of the score shows the steps that improve it. Info buttons give plain-language explanations next to each title.
- **Test CLI** (`--dump`, `--live`, `--fixes`, `--dry-enable`, `--dry-disable`) and xUnit tests.

### Distribution
- **Installer** (Inno Setup, `WinDiag-Setup-<version>.exe`) in English and Portuguese, with a Start menu shortcut, an optional desktop icon and an optional prompt to remove preferences and history on uninstall.
- **Portable** (`WinDiag-Portable-<version>.exe`, self-contained) and **framework-dependent** (`WinDiag-<version>-fx.exe`, requires the .NET 8 Desktop Runtime) builds.
- `SHA256SUMS.txt` and a build provenance attestation (`actions/attest-build-provenance`), verifiable with `gh attestation verify`.
- Separate `ci.yml` and `release.yml` workflows; the GitHub Release uses this changelog section as its notes.
- Code-signing hook in `publish.ps1` (`WINDIAG_SIGN_CMD`). The executables are not code-signed yet.
- Documentation: `THIRD-PARTY-NOTICES.md`, `docs/ARCHITECTURE.md`, `docs/SIGNING.md` and `docs/RELEASING.md`.

### Notes
- Snapshot and S.M.A.R.T. history formats are compatible with the earlier internal versions, and older snapshots appear in the app.
- No telemetry, no runtime downloads and no runtime-generated code.
