# Changelog

All notable changes to WinDiag are documented in this file. The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

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
