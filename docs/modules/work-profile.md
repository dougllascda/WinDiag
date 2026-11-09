# Work profile

The Work profile (`UsageProfile.Work`) swaps the gaming-oriented view for office-oriented checks: printers, Windows Update, battery, power plan, boot time and low-resource hardware. It is chosen by the user (`ProfileChooserWindow` on first run, `SettingsView` afterwards) and stored in `UserPrefs.Profile`.

## WorkCollector

`WorkCollector` (`Key = "trabalho"`) is a normal `ICollector`; it runs on every scan, and the profile only controls what is shown and scored. Its checks are separated from the readings so they can be tested: `WorkChecks(InstalledRamGB, SystemDriveIsHdd, SpoolerDisabled, Now)` carries data that is not in `SystemFacts`.

| Finding id | Check | Fix |
|---|---|---|
| `trabalho.printer_offline` | Printer offline or with an error state (`Win32_Printer`) | none |
| `trabalho.print_stuck` | Jobs in the queue for more than 10 minutes (`Win32_PrintJob`) | `PRINT_QUEUE_CLEAR` |
| `trabalho.spooler_stopped` / `trabalho.spooler_disabled` | Spooler service state (`Win32_Service`) | `SPOOLER_RESTART` (when stopped) |
| `trabalho.bloat` | Installed programs classified by `BloatwareDb` | Uninstaller (see below) |
| `trabalho.reboot_pending` | `RebootRequired` or `RebootPending` registry keys | none |
| `trabalho.update_old` | Latest `Win32_QuickFixEngineering` install date is old; `ParseInstalledOn` accepts `M/d/yyyy`, local culture or a hexadecimal FILETIME | none |
| `trabalho.battery_wear` | Battery wear from `BatteryReport` | none |
| `trabalho.power_plan`, `trabalho.power_battery_high` | Active power plan vs the recommended one; plugged state from `Win32_Battery.BatteryStatus` | `POWER_PLAN_WORK` |
| `trabalho.slow_boot` | Last measured boot time | none |
| `trabalho.low_ram`, `trabalho.system_hdd` | Installed RAM and system drive type | none |

All of them are created with `scope: Scope.Work`. In the score, `GameScore.AreaOf(Finding, Work)` maps ids to areas (printing, battery and power, updates and security, storage and memory).

## Printers

`PrinterInfo` (`Name`, `IsDefault`, `IsNetwork`, `StatusText`, `Offline`, `HasError`, `JobCount`, `StuckJobs`) is built from `Win32_Printer` and `Win32_PrintJob` and stored in `SystemFacts.Printers`. Actions: `SPOOLER_RESTART` restarts the Spooler; `PRINT_QUEUE_CLEAR` stops it, deletes the files of `FixEngine.SpoolDirectory` (`ClearSpoolDirectory`) and starts it again. Both are one-off actions run by `FixEngine.RunAction`, not reversible, and the card says so. The spooler action guarantees that the service is started again even when clearing the queue fails midway. When the spooler is disabled on purpose (`SystemFacts.SpoolerDisabled`), the action is not applicable and the UI shows no button.

`POWER_PLAN_WORK` is the only reversible work item (snapshot with the previous plan GUID). It is offered on a desktop PC whose active plan is Power saver (switches to High performance; Balanced and High performance both count as fine) and on a laptop running High or Ultimate performance (switches to Balanced), and only when the target plan exists in `powercfg /list`. Custom plans are never touched.

## BloatwareDb and Uninstaller

- `BloatwareDb.Classify(name, publisher)` returns `(BloatVerdict, Reason)?`. `BloatVerdict`: `Remove` (promotion, trial or casual game shipped by the OEM), `Optional` (redundant, user decides), `Keep` (driver, panel or BIOS support tool). `Label` gives the localized text. Unknown programs return `null` and are not listed.
- Items are `BloatItem` (`Name`, `Publisher`, `Version`, `Verdict`, `Reason`, `UninstallCommand`, `IsAppx`, `PackageFullName`) in `SystemFacts.Bloat`.
- Paid antivirus products (McAfee, Norton and similar) are never classified as `Remove`: a PC without a working antivirus is a worse outcome than a redundant one, so they are `Optional` and the text asks the user to check that another protection is active before uninstalling.
- `Uninstaller` never removes anything silently and has no silent mode. `Parse(command)` splits an `UninstallString` into file and arguments; `Launch(item)` starts the vendor's own interactive uninstaller, or opens `ms-settings:appsfeatures` (`AppsSettingsUri`) for Appx packages. It refuses commands that come from the user hive (`HKCU`) and script interpreters (PowerShell, `cmd`, `wscript`, `mshta` and similar), because those are not an installer the vendor shipped. `BloatCardView` asks for confirmation first; there is no snapshot and no undo.

## Battery

`BatteryReport.Read(out error)` runs `powercfg /batteryreport /xml` into a temporary folder, parses it with `Parse(xml)` into `BatteryCapacity(DesignMWh, FullChargeMWh)` and deletes the file. `WearPct` is derived from the two capacities. Without a battery or without the report, the field is `n/d` and a note is recorded.

## How to extend

- **New check.** Add it to `WorkCollector.Collect` with `scope: Scope.Work` and an id starting with `trabalho.`; add the prefix to the area mapping in `GameScore.AreaOf(Finding, profile)`.
- **New program entry.** Add a pattern and verdict to `BloatwareDb` with a bilingual reason. Prefer `Keep` or `Optional` when unsure; `Remove` only for clear bloat.
- **New one-off action.** Add a `FixDefinition` with `IsAction = true`, `Scope.Work`, a new flag, and handle it in `FixEngine.RunAction`.
