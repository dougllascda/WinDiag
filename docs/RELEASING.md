# How to publish a release

A checklist for the maintainer. The `.github/workflows/release.yml` workflow does the rest when the tag is pushed.

## Before the tag

1. Update the version in `app\Directory.Build.props` (`Version`, `AssemblyVersion`, `FileVersion`, `InformationalVersion`). It is the single source: `publish.ps1` and the installer read it from there.
2. Update `CHANGELOG.md`: add a section `## [X.Y.Z] - yyyy-MM-dd` (moving the entries from `## [Unreleased]`). The workflow uses exactly that section (up to the next `## `) as the Release body.
3. Check that the README mentions nothing tied to a fixed version (the badges are dynamic and the download links point to `releases/latest`).
4. Check locally:
   ```powershell
   dotnet build app\WinDiag.sln -c Release
   dotnet test app\WinDiag.sln -c Release
   pwsh -File publish.ps1 -Scan
   ```
   With Inno Setup 6 installed, `dist\` will also contain `WinDiag-Setup-X.Y.Z.exe`. Without it, the script warns and skips the installer.
5. Commit to `main` and wait for CI to turn green.

## Tag and push

```powershell
git tag vX.Y.Z
git push origin vX.Y.Z
```

The tag must be `v` + the version in `Directory.Build.props`.

## After the push

1. Watch the Actions tab: the **Release** workflow tests, builds the artifacts, attests provenance and creates the Release.
2. Check the Release:
   - `WinDiag-Setup-X.Y.Z.exe`, `WinDiag-Portable-X.Y.Z.exe`, `WinDiag-X.Y.Z-fx.exe` and `SHA256SUMS.txt`;
   - the hashes in `SHA256SUMS.txt` match the downloaded files;
   - the attestation: `gh attestation verify WinDiag-Portable-X.Y.Z.exe --repo dougllascda/WinDiag`;
   - if signing is configured, `Get-AuthenticodeSignature` returns `Valid` (see `docs\SIGNING.md`).
3. Install the Setup on a VM or test machine, open the app, uninstall, and confirm that the question about preferences and history appears.
4. Review the Release notes (edit if needed) and publish.

## If something fails

- Fix it, delete the local and remote tag (`git tag -d vX.Y.Z`; `git push origin :refs/tags/vX.Y.Z`) and push again. Do not reuse a tag whose files users have already downloaded: publish a new version instead.
