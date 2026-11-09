# Contributing to WinDiag

**English** | [Português (Brasil)](../docs/pt-BR/CONTRIBUTING.pt-BR.md)

Thanks for your interest. This guide summarizes how to build, test and extend the project. An overview of the code is in [docs/ARCHITECTURE.md](../docs/ARCHITECTURE.md). WinDiag is source-available under the [WinDiag License](../LICENSE), not open source. Issues and suggestions in English or Portuguese are welcome; pull requests are accepted only under the Contributor License Agreement below.

## Issues and suggestions

Bug reports, ideas and questions are welcome as GitHub issues. Please include the WinDiag version, the Windows version and, for a bug, what you expected and what happened. For security problems, see [SECURITY.md](SECURITY.md).

## Contributor License Agreement (CLA)

By submitting a contribution (a pull request, a patch, code, text, translation, or any other material) to this repository, you agree to the following:

1. **Authorship.** You declare that you are the author of the contribution, or that you have the right to submit it, and that it does not violate the rights of third parties.
2. **Assignment.** You assign and transfer to dougllascda all economic (patrimonial) rights in the contribution, to the fullest extent permitted by law.
3. **Fallback license.** Where the assignment is not possible or not valid under applicable law, you grant dougllascda a perpetual, worldwide, irrevocable, royalty-free and exclusive license to use, reproduce, modify, distribute and otherwise exploit the contribution, with the right to sublicense it.
4. **No obligation.** dougllascda has no obligation to use, keep or publish your contribution, and decides alone what is included in the project.
5. **No compensation.** The contribution is made without payment.

If you do not agree, please do not submit a pull request; open an issue instead. The pull request template asks you to confirm this agreement.

## Requirements

- Windows 10 or 11 (the app reads WMI, the registry and Windows performance counters).
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
- PowerShell 7 (`pwsh`) or Windows PowerShell 5.1, for `tools\publish.ps1`.

## Build and test

```powershell
dotnet build app\WinDiag.sln -c Debug
dotnet test app\WinDiag.sln
```

The Debug build uses the `asInvoker` manifest (no UAC prompt); Release asks for administrator rights on launch. Fix all new errors and warnings before submitting. Building locally is allowed only to evaluate the software and to prepare contributions under the CLA; do not distribute the binaries you build (see the [WinDiag License](../LICENSE)).

## Test CLI

The CLI runs without a UI and is the fastest way to validate a collector or an optimization item:

```powershell
dotnet run --project app\WinDiag.Cli -- --dump out.json        # full scan as JSON (stdout if no file)
dotnet run --project app\WinDiag.Cli -- --live 5               # 5 live samples, one per second
dotnet run --project app\WinDiag.Cli -- --fixes                # state of each optimization (changes nothing)
dotnet run --project app\WinDiag.Cli -- --dry-enable GAMEDVR   # simulates turning on (nothing is written)
dotnet run --project app\WinDiag.Cli -- --dry-disable GAMEDVR  # simulates undoing that item only
```

Run it in an elevated terminal to see complete readings.

## Conventions

- **Bilingual interface.** User-facing text exists in English and Brazilian Portuguese (`Loc.T(pt, en)`). The pt-BR text must have correct accents (files are UTF-8). Numbers and dates follow the selected language; pt-BR dates are `dd/MM/yyyy`.
- **Sober, technical tone.** Never write "100% safe". **Do not claim a cause without a measurement**: describe what was read and what it may indicate.
- **XML comments** (`///`) on public types and members.
- **Collectors are read-only** and must never crash the app. Wrap every read in `try/catch`; a failure becomes a note (for example "requires administrator") and the field shows `n/d`.
- **Every change to the system goes through `FixEngine`.** Reversible changes save a snapshot in `%ProgramData%\WinDiagMaint\snapshots` first and have an undo. One-off actions (restart spooler, clear print queue, clear temporary files) and uninstallations (which only open the program's own uninstaller, never silently) have no snapshot, but always ask for confirmation.
- External processes (`nvidia-smi`, `powercfg`, `bcdedit`, `fltmc`) go through `Util.Proc`: with a timeout and no window.
- No obfuscation, packers, runtime downloads, `Reflection.Emit` or runtime-generated code. Documented Win32 P/Invoke is allowed.
- Do not mask data in the UI; masking of user name and serial numbers applies only to the exported report.
- Line endings and encoding are defined by `.editorconfig` and `.gitattributes`.

## Adding a collector

1. Create `app\WinDiag.Core\Collectors\MyCollector.cs` implementing `ICollector` (`Key`, `Title`, `Collect(SystemFacts)`).
2. Build the `SectionResult`: groups with `Group(...).Add(label, value)`, tables, `Find(...)` for checks and `Note(...)` for partial readings.
3. If other components need a value, add a property to `SystemFacts` and fill it in the collector.
4. Register the collector in `ScanEngine.CreateCollectors()` (the order defines the tab and report order).
5. Create the matching tab in the App if it is a new area.
6. Add a test in `app\WinDiag.Tests` and confirm with `--dump`.

## Adding an analysis rule

Rules live in `app\WinDiag.Core\Rules\` and work on `SystemFacts` and the `Finding` objects already collected (the "Platform analysis" section, phase C).

1. Write the rule as a pure function over the facts, with no I/O.
2. Use measured evidence only; if the data is `n/d`, the rule concludes nothing.
3. Produce a `Finding` with `Evidence`, `Explain` ("what it means"), `Action` ("what to do") and `Impact` (0 to 100).
4. Cover the thresholds with tests, including the no-data case.

## Adding an optimization item

1. In `FixCatalog.All`, add a `FixDefinition` with a unique `Id`, `Name`, `ShortName`, `Risk`, `Reboot`, `Explain`, the targets (`Reg`, `Svc`, `Bcd`, `Power`...), `Applicable` and `IsActive`.
2. If the target is of a new kind, extend `FixState`/`Capture`/`Apply` in `FixEngine` and make sure the "before" state is saved and restored.
3. Items that cannot be restored must have `Reversible = false` and say so in `Explain`.
4. Test with `--fixes` and `--dry-enable ID` / `--dry-disable ID`, and add tests in `FixTests` (use `SnapshotStore` with a temporary folder).

## Publishing

```powershell
pwsh -File tools\publish.ps1                # tests and builds dist\
pwsh -File tools\publish.ps1 -SkipTests     # builds dist\ only
pwsh -File tools\publish.ps1 -Scan          # also scans dist\ with Kaspersky, if installed
```

Output in `dist\`: `WinDiag-Setup-<version>.exe`, `WinDiag-Portable-<version>.exe`, `WinDiag-<version>-fx.exe` and `SHA256SUMS.txt`. The release process is described in [docs/RELEASING.md](../docs/RELEASING.md).

## Pull requests

Describe what changed and how you validated it (commands and results). Keep changes small and focused, and update `CHANGELOG.md` when users are affected. The pull request template has a checklist, including the CLA confirmation. The owner decides what is merged.
