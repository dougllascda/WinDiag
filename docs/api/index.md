# WinDiag Developer Docs

WinDiag is a Windows diagnostic and optimization app (WPF, .NET 8). These pages are for people who read or extend the code.

## Start here

- [Architecture overview](../ARCHITECTURE.md): projects, principles, scan phases and contracts.
- Module guides, each with a "How to extend" section:
  [scan engine](../modules/scan-engine.md), [collectors](../modules/collectors.md),
  [analysis and rules](../modules/analysis-and-rules.md), [optimizations](../modules/optimizations.md),
  [sensors](../modules/sensors.md), [work profile](../modules/work-profile.md),
  [history and report](../modules/history-and-report.md), [user interface](../modules/ui.md),
  [localization](../modules/localization.md).
- [API reference](xref:WinDiag.Core): generated from the XML documentation comments of `WinDiag.Core` and `WinDiag.App`.

## Working rules

- Collectors are read-only and never crash the app; a failed read becomes a note and the field shows `n/d`.
- Every change to the system goes through `FixEngine`, with a snapshot written first and an undo available.
- No obfuscation, no runtime downloads, no runtime code generation. Only documented Win32 P/Invoke.

## Building these docs

```powershell
dotnet tool install -g docfx
docfx docs\api\docfx.json
```

The site is written to `docs\api\_site`. Metadata extraction builds the two projects with `TargetFramework=net8.0-windows`, so it must run on Windows.

