# Collectors

A collector reads one area of the system and returns a `SectionResult`. Collectors live in `WinDiag.Core\Collectors` and are read-only.

## Contract

```csharp
public interface ICollector
{
    string Key { get; }                       // stable, e.g. "cpu"; selects the tab
    string Title { get; }                     // localized section title
    SectionResult Collect(SystemFacts facts); // never throws
}
```

Rules every collector follows:

- Each read that can fail is inside `try/catch`. The failure becomes `res.Note(...)` and the field shows `n/d` (`Fmt.NA`; `n/a` in English). `ScanEngine` also catches, but a collector should not rely on it.
- `InfoGroup.Add(label, value)` turns a null or empty value into `n/d`.
- Findings are created with `SectionResult.Find(sev, title, evidence, explain, action, fixId, impact, id, simple, guide, scope)`. The `id` is stable and language-independent (for example `gpu.hags`); when omitted it is `DefaultId(section, title)`. `Scope` defaults to `ScopeMap.For(id)`.
- WMI goes through `Util.Wmi` (`First`, `Query`, `DisposeAll`), the registry through `Util.Reg`, and external processes through `Util.Proc` (timeout, no window).
- A collector fills only the `SystemFacts` properties of its area.

## Collectors and sources

| Key | Class | Main sources | Needs administrator |
|---|---|---|---|
| `sistema` | `SystemCollector` | `Win32_OperatingSystem`, `Win32_ComputerSystem`, `Win32_BaseBoard`, `Win32_BIOS`, `Win32_Battery`, `Win32_PnPEntity` (chipset), registry (CurrentVersion, Fast Startup, hibernation, service start values), `bcdedit` through `Bcd`, `powercfg` through `PowerCfg` | BCD timers (`n/d` without it) |
| `cpu` | `CpuCollector` | `Win32_Processor`, `Win32_CacheMemory`, `Win32_ComputerSystem.HypervisorPresent`, `CpuCodename` | No. Core temperature is not exposed by Windows; the field points to the Sensors tab |
| `memoria` | `MemoryCollector` | `Win32_PhysicalMemory`, `Win32_PhysicalMemoryArray`, `Win32_PageFileUsage`, `Win32_OperatingSystem`, perf counters, `Get-MMAgent` through `powershell.exe` | Memory compression state; without it falls back to the "Memory Compression" process and the other two fields are `n/d` |
| `gpu` | `GpuCollector` | `Win32_VideoController`, registry (driver key, VRAM, HAGS, GameDVR, Game Mode, MPO, windowed optimizations), `nvidia-smi` through `NvidiaSmi` | No. Without `nvidia-smi` the driver fields are `n/d` |
| `monitores` | `DisplayCollector` | `WmiMonitorID`, `WmiMonitorBasicDisplayParams`, `WmiMonitorConnectionParams` (`root\wmi`), `DisplayApi` (GDI modes, CCD paths) | No |
| `armazenamento` | `StorageCollector` | `MSFT_PhysicalDisk`, `MSFT_Partition`, `MSFT_StorageReliabilityCounter`, `MSStorageDriver_*` (S.M.A.R.T.), `Smart`, `Steam` libraries | Raw S.M.A.R.T. and history; also TRIM (`fsutil`) |
| `seguranca` | `SecurityCollector` | `Win32_DeviceGuard`, HVCI registry, `Win32_OptionalFeature`, `Win32_Service`, `AntiVirusProduct` (SecurityCenter2), `MSFT_MpComputerStatus` (Defender), `fltmc` through `Filters` | Minifilters and optional features |
| `processos` | `ProcessCollector` | `Win32_Process`, GPU memory counters, `Win32_NetworkAdapter`, `Win32_SoundDevice`, `Startup` | No |
| `eventos` | `EventsCollector` | `EventLogReader` over the last 7 days (video TDR, disk I/O, WHEA, Kernel-Power 41), classified by provider and ID, `Win32_OperatingSystem.LastBootUpTime` | Some logs; failures become notes |
| `sensores` | `SensorsCollector` | `SensorHub` (see [sensors](sensors.md)), `MSFT_PhysicalDisk` for the disk type | Driver-backed sensors only |
| `trabalho` | `WorkCollector` | Printers and queue, Spooler, Windows Update state, battery, power plan (see [work-profile](work-profile.md)) | Battery report may be partial |
| `analise` | `Insights` (not an `ICollector`) | Facts and previous results (see [analysis-and-rules](analysis-and-rules.md)) | No |

## Testing seams

`SensorsCollector` has a constructor that accepts injected sources, and `WorkCollector` accepts a `WorkChecks` record, so tests do not need real hardware. Parsing helpers (for example the `Get-MMAgent` output parser in `MemoryCollector`, `Filters.Parse`, `Smart.Parse`, `BatteryReport.Parse`) are `public` or `internal` and covered by the test project through `InternalsVisibleTo`.

## How to extend

1. Create `MyCollector : ICollector` in `WinDiag.Core\Collectors`. Return a `SectionResult { Key, Title }`.
2. Wrap every system read in `try/catch` and add a note on failure. Use `Loc.T(pt, en)` for every user-visible string.
3. Give every finding a stable `id`, an `impact` (0 to 100) only when it can plausibly limit performance, and a `fixId` only if `FixCatalog` has a matching item.
4. Add facts to `SystemFacts` if rules or fixes need them.
5. Register it in `ScanEngine.CreateCollectors()` and add a page in the UI.
6. Add tests with injected data, plus the English scan test coverage (the existing `EnglishScanTests` scans all sections for untranslated text).

