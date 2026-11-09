## Summary

<!-- What changes and why. -->

## How it was validated

- [ ] `dotnet build app\WinDiag.sln -c Debug` with no new errors or warnings
- [ ] `dotnet test app\WinDiag.sln` passing
- [ ] Tested with the CLI (`--dump`, `--fixes`, `--dry-*`), when applicable

## Contributor License Agreement

- [ ] I have read and agree to the Contributor License Agreement in CONTRIBUTING.md

## Checklist

- [ ] User-facing text in both languages (`Loc.T(pt, en)`), pt-BR with correct accents
- [ ] New readings are read-only and wrapped in `try/catch` (a failure becomes a note and `n/d`)
- [ ] Every change to the system goes through `FixEngine`, with a snapshot and undo
- [ ] No cause is claimed without a measurement
- [ ] No runtime download, obfuscation or runtime-generated code
- [ ] `CHANGELOG.md` updated, if users are affected
