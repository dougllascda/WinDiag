# User interface

`WinDiag.App` is a WPF application (`net8.0-windows`) with one window, `MainWindow`. The UI is mostly built in code (`Views\*.cs`); only `App.xaml`, `MainWindow.xaml`, `Themes\Styles.xaml` and `Assets\Logo.xaml` are XAML. The UI never changes the system by itself: adjustments go to `FixEngine` and tools go to `ToolRunner`, both after a `ConfirmDialog`.

## MainWindow and tabs by key

Tabs are described by a private `Page` record: `Key` (stable internal identifier), `PtLabel`/`Label`, `Keys` (the section keys it shows) and an optional `View`, `Live` (`LiveCard`) or `IsSummary`. `BuildPages` registers them in this order:

| Tab key | Section keys | View |
|---|---|---|
| `overview` | none | `DashboardView` |
| `summary` | all | summary (`RefreshSummary`) |
| `plan` | none | `PlanView` |
| `work` | `trabalho` | `WorkView` |
| `cpu` | `cpu` | `SectionView` + `LiveCard` |
| `board` | `sistema` | `SectionView` |
| `memory` | `memoria` | `SectionView` + `LiveCard` |
| `gpu` | `gpu` | `SectionView` + `LiveCard` |
| `sensors` | `sensores` | `SensorsView` |
| `displays` | `monitores` | `SectionView` |
| `storage` | `armazenamento` | `SectionView` |
| `system` | `seguranca`, `processos`, `eventos` | `SectionView` |
| `optimizations` | none | `OptimizationsView` (own icon) |
| `tools` | none | `ToolsView` |
| `session` | none | `GameSessionView` |
| `capture` | none | `CaptureView` |
| `settings` | none | `SettingsView` (added by `AddSettingsPage`) |

`ScanEngine.SectionReady` calls `OnSection`, which refreshes every tab whose `Keys` contain the section key (`RefreshPage`). A scan is started by `StartScanAsync`; reload is F5, export is Ctrl+S.

## Modes

`Ui.Simple` (default true) comes from `UserPrefs.Mode` (`"Simple"` or `"Detailed"`); `Ui.SetSimple` raises `Ui.ModeChanged`, and `MainWindow.OnModeChanged` re-applies it to tabs, the overview and the plan without a new scan. In Simple mode only the tabs in `SimpleTabs` are visible (`overview`, `plan`, `optimizations`, `tools`, `session`, `sensors`, `work`, `settings`). `FindingRow` shows the plain-language `Finding.Simple` text in Simple mode.

## Profiles

`ProfileUi` caches the `UsageProfile` from `Prefs` (`Current`, `IsWork`, `Set`, `Changed`, `NeedsChoice`). On first run `AskProfile` opens `ProfileChooserWindow`. `IsHiddenByProfile` hides `session` and `capture` in the Work profile and `work` in the Gaming profile. The header profile chip leads to Settings. Findings, score, plan and fixes are filtered by `ScopeRules.Matches` (see [analysis-and-rules](analysis-and-rules.md)).

## Navigation with highlight

`MainWindow.GoTo(sectionKey, findingId)` (also exposed as `Ui.GoTo`) finds the page whose `Keys` contain the key (falling back to the summary), skips pages hidden by profile, switches to Detailed mode if the target tab is hidden in Simple mode, selects the tab and, at `ContextIdle`, `ScrollToTarget` finds the `FindingRow` with that id, expands it, brings it into view and lights `HighlightOverlay` (a 2 px accent border and soft fill that fade in about 1.5 s). `Ui.ShowBios(topic)` opens `BiosGuideWindow`.

## ThemeManager

`ThemeManager` (`Themes\ThemeManager.cs`) holds `Mode` (`ThemeMode`: `Auto`, `Light`, `Dark`), `IsDark` and the `Changed` event. It owns a palette of `SolidColorBrush` objects registered as application resources and updates their colors in place, so every view bound to a `DynamicResource` follows without rebuilding. `Auto` follows Windows through `SystemEvents.UserPreferenceChanged`. `Register(window)` also applies the dark title bar (`DwmSetWindowAttribute`). `Ui.Res(key)` fetches a brush.

## Prefs

`Prefs` (internal) is the only way the app reads or writes `UserPrefs` (`%AppData%\WinDiag\settings.json`). `Update(change)` re-reads the file, applies one change and saves, so concurrent changes do not overwrite other fields. If the read fell back to defaults (`UserPrefs.IsFallback`), it does not save and raises `SaveBlocked`; `MainWindow.OnPrefsSaveBlocked` tells the user the preference is valid for this session only.

## Tools tab

`ToolsView` lists the tools by category in both profiles. Each button opens a confirmation (read-only tools without administrator run directly) that shows the exact commands, whether administrator rights or a restart are needed, the estimated duration and the risk. A tool that has a `BlockReason` shows it instead of running. While a tool runs, the live output is shown with a Cancel button. For medium-risk tools the view offers a restore point first. Rules and flow: [Tools](tools.md).

## Settings: updates

`SettingsView` has an Updates block: check frequency (On app start, Daily, Weekly (default), Monthly, Manually), "Last check" and "Check now". `MainWindow.Update.cs` runs the scheduled check after startup and shows a banner (Update now, What's new, Skip this version, Later); `Views\UpdateWindows.cs` has the notes window and the download flow. Nothing is downloaded or installed without a click. WinDiag does not download or run third-party code; the update check queries only the official repository, and the network tools and DISM use the internet only when you run them. Flow and trust model: [Updates](updates.md).

## Shared widgets

`Ui` (cards, labels, tables, badges, `FindingList`, info icons via `Glossary`), `LiveCard` (live values, `Update(LiveSample)`), `Segmented` (mode and profile switch), `LineChart`, `SmoothScroll`.

## How to extend

1. Add a tab: call `Add(icon, key, ptLabel, enLabel, sectionKeys, view: ...)` in `BuildPages`. Use an existing view type or create a `UserControl`-like class in `Views`.
2. Decide Simple/Detailed visibility (`SimpleTabs`) and profile visibility (`IsHiddenByProfile`).
3. Use `Loc.T` for all labels; use `Fmt` for numbers and dates; show `n/d` for missing values.
4. For navigation from a finding, make sure the row carries the finding `Id` so `GoTo` can highlight it.
5. Dispose timers and event subscriptions with `TrackView`/`Dispose` to avoid leaks on rescan.
