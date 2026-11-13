# WinDiag Developer Docs

WinDiag is a Windows app (WPF, .NET 8) with hardware inventory, explained diagnostics, one-click repair tools and reversible optimizations for gaming and office PCs. These pages are for people who read or extend the code. To download or use the app, see the [repository](https://github.com/dougllascda/WinDiag).

## Start here

- [Architecture overview](../ARCHITECTURE.md): projects, principles, scan phases and contracts.
- Module guides, each with a "How to extend" section:
  [scan engine](../modules/scan-engine.md), [collectors](../modules/collectors.md),
  [analysis and rules](../modules/analysis-and-rules.md), [optimizations](../modules/optimizations.md),
  [tools](../modules/tools.md), [updates](../modules/updates.md),
  [sensors](../modules/sensors.md), [work profile](../modules/work-profile.md),
  [history and report](../modules/history-and-report.md), [user interface](../modules/ui.md),
  [localization](../modules/localization.md).
- [API reference](xref:WinDiag.Core): generated from the XML documentation comments of `WinDiag.Core` and `WinDiag.App`.

## Working rules

- Collectors are read-only and never crash the app; a failed read becomes a note and the field shows `n/d`.
- Every reversible adjustment goes through `FixEngine`, with a snapshot written first and an undo available.
- Every one-off tool goes through `ToolRunner`: fixed commands shown to the user before they run, a dry-run mode, a block reason when it cannot run, and a history entry. Tools have no snapshot.
- The only download is the app's own update, from the official releases, verified by SHA-256 before it runs (`UpdateInstaller`). No third-party code is downloaded or run.
- No obfuscation and no runtime code generation. Only documented Win32 P/Invoke.

## Building these docs

```powershell
dotnet tool install -g docfx
docfx docs\api\docfx.json
```

The site is written to `docs\api\_site`. Metadata extraction builds the two projects with `TargetFramework=net8.0-windows`, so it must run on Windows.

