# History and report

## ScanHistory

`ScanHistory` (`WinDiag.Core\History`) keeps a short history of scans and compares the latest two. It is stored in `%LocalAppData%\WinDiag\history` (`ScanHistory.DefaultDir`) as indented JSON with accents unescaped and enums written as text.

| Member | Purpose |
|---|---|
| `Summarize(engine)` | Builds a `ScanSummary` (`When`, `Version`, `Facts`, `Findings`, `Score`) from the current scan. |
| `RecordAndCompare(engine, dir)` | Compares with the newest previous file, writes the current one and keeps the 30 newest (`MaxFiles`). Returns `null` if there was no previous scan. |
| `LoadLatest(dir)` | Newest readable summary; unreadable files are ignored. |
| `Compare(previous, current)` | Returns a `ScanDiff`: `Resolved`, `Improved`, `Worsened`, `New` (`FindingChange`) and `FactChanges` (`FactChange`), plus `PreviousScore`/`CurrentScore` and `IsEmpty`. |
| `NormalizeFacts(facts)` | Converts old Portuguese-label keys to the stable language-independent keys. |

Design points:

- Findings are matched by `FindingSummary.Key`, the stable finding `Id` (older files used `section:title`), so changing the language does not create false differences. Fact values are stored in a neutral (Portuguese) form and translated only for display.
- The scan records whether it ran as administrator (special key `Admin`). If the mode differs between two scans, findings of the sections that depend on it (`armazenamento`, `sistema`, `seguranca`) are ignored in the comparison.
- `Resolved` means a finding that was `Aviso` or `Atencao` and no longer is; `New` is the opposite.

## ReportWriter

`ReportWriter` (`WinDiag.Core\Report`) exports one file at a time from a finished `ScanEngine`.

| Member | Output |
|---|---|
| `WriteHtml(engine, path)` | Self-contained HTML: embedded CSS, no JavaScript, no external resources, tabs through radio inputs, automatic light/dark theme (`prefers-color-scheme`), print CSS that shows every panel. |
| `WriteTxt(engine, path)` | UTF-8 text with BOM, 78 columns wide. |
| `Write(engine, dir)` | Writes both and returns `(TxtPath, HtmlPath)`. |
| `DefaultDir()` / `DefaultFileName(ext)` | Default folder (the Desktop when available) and a name like `WinDiag_Report_yyyyMMdd_HHmm.html` (`WinDiag_Relatorio_...` in Portuguese). Dates use `dd/MM/yyyy`. |

The `analise` section (`ReportWriter.AnalysisKey`) is placed before the Summary. `BuildTxt` and `BuildHtml` are `internal` and used by tests.

## ReportMask

`ReportMask` (internal) is applied **only while building the exported file**. The UI shows everything unmasked.

- `User(text, ...)` replaces the user name and the user profile folder name with `<USUARIO>` (`<USER>` in English) and the computer name with `<PC>`. Only whole words are replaced (no letter, digit or underscore attached), so `Admin` does not corrupt `Administrator`. Terms shorter than two characters are ignored.
- `Serial(value)` shows `****` plus the last four characters. `IsSerialName(label)` decides which rows hold serial numbers (labels containing "Série", "Serie" or "Serial"); `RowValue(label, value)` applies it.
- `Text(text)` is the shortcut used for titles, evidence and notes; `OneLine`, `Wrap` and `LabelCell` are layout helpers for the text report.

## How to extend

- **New fact in the comparison.** Add its stable key to the facts list in `ScanHistory` (`FactDefs`) with a bilingual label; keep values neutral and translate on display.
- **New section in the report.** Sections are rendered generically from `SectionResult.Groups`, `Findings` and `Notes`; a new collector appears without changes. Pass every user-derived string through `ReportMask.Text`.
- **New masked field.** Extend `IsSerialName` or `User`, and add a test in `ReportTests` asserting the raw value is absent from the output.
