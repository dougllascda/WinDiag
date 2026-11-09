# Localization

User-facing text is bilingual: Brazilian Portuguese (primary) and English. There are no resource files; both texts sit side by side in code, next to the logic that produces them.

## Loc

`Loc` (`WinDiag.Core`) is the single switch.

| Member | Behavior |
|---|---|
| `Loc.Set(preference)` | `"Auto"`, empty or `null` follow Windows (Portuguese if the UI culture is `pt`, English otherwise); `"pt"`, `"pt-BR"`, `"en"`, `"en-US"` force a language. Called once at startup from `UserPrefs.Language`, before the scan. |
| `Loc.T(pt, en)` | Returns the text for the current language. For interpolated messages pass two interpolated strings: `Loc.T($"{n} itens", $"{n} items")`. |
| `Loc.IsPt`, `Loc.Language`, `Loc.Culture` | Current state; `Culture` is `pt-BR` or `en-US`. |

## Fmt

`Fmt` formats values with the current language: `Num(value, decimals)` (comma in Portuguese, dot in English), `GB`, `MB`, `WithUnit`, `Val`, `ParseWmiDate`, and the date helpers `Date`, `DateTime`, `DateTimeSec`. Dates are always `dd/MM/yyyy`. `Fmt.NA` is `n/d` in Portuguese and `n/a` in English; use it for every value that could not be read.

## Rules

- Stable identifiers are never translated: finding ids (`gpu.hags`), fix ids (`GAMEDVR`), section keys (`cpu`, `analise`), snapshot actions (`Aplicar`, `Desfazer`), `SensorReading.HardwareKind` values, and history fact keys. Use `HardwareKinds.HardwareKindLabel` and similar helpers to translate for display only.
- Fix and glossary texts use pairs (`NameText`, `ExplainText`, ...) resolved at read time, so a language change applies without restarting the catalog. `Glossary` keeps English text in `Glossary.En.cs`.
- Portuguese text uses correct accents (UTF-8). Technical and sober tone; never "100% safe"; no cause is stated without a measurement.
- Language is set before the scan; strings captured earlier in a static field stay in the old language.

## Tests

English coverage is tested in the `Loc` xUnit collection (`LocCollection`, parallelization disabled because `Loc` is global state). `TestSetup` fixes `pt-BR` for every test; English tests call `Loc.Set("en")` and restore it.

- `EnglishScanTests` runs a scan in English and checks section titles, row labels, tables, columns, finding titles and explanations, bloat reasons and printer status for leftover Portuguese (regex-based).
- `EnglishRulesTests` covers `Insights`, `ActionPlan` (English and Portuguese markers), `GameScore`, `Glossary` (every entry must have an English version), `BiosGuides` and `ReportWriter`.
- `EnglishCoreTests` covers severity labels, fix names and risk, `FixStatus.StatusText`, capture summary, `ScanHistory` language switching and `HardwareKindLabel`.

## How to extend

1. Write every new string with `Loc.T(pt, en)`, never one language only.
2. Put new test classes that call `Loc.Set` in `[Collection("Loc")]` and restore the language.
3. For a new glossary entry fill both languages; `Glossary_EveryEntry_HasEnglishVersion` fails otherwise.
4. To add a third language, `Loc` needs to change from a boolean switch to a language identifier; the call sites of `Loc.T` would then need a third argument or a table.
