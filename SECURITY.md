# Security

**English** | [Português (Brasil)](SECURITY.pt-BR.md)

## Principles

- **Read-only by default:** WinDiag changes something only when you turn a switch on and confirm. High-risk items require typing `CONFIRM` (`CONFIRMO` in the Portuguese interface).
- **Reversible:** before every change, the previous state is saved to `%ProgramData%\WinDiagMaint\snapshots\`. Turning the switch off or restoring a snapshot goes back to the saved value.
- **No network:** the app does not download, upload or update anything by itself, and has no telemetry.
- **No hidden code:** no obfuscation, packer, runtime-generated code or executable temporary files. The only native calls are documented Windows APIs, for display modes.
- **Privacy in the report:** the exported report masks the user name and serial numbers. The interface shows the full data to you only.

## Supported versions

Security fixes are made in the latest release only.

## Verify a download

Compare the hash with `SHA256SUMS.txt` from the release:

```powershell
Get-FileHash .\WinDiag-Setup-<version>.exe -Algorithm SHA256
```

Confirm that the file was built by this repository's release workflow:

```powershell
gh attestation verify .\WinDiag-Setup-<version>.exe --repo dougllascda/WinDiag
```

See [docs/SIGNING.md](docs/SIGNING.md) for code-signing status and details.

## Antivirus false positives

The executables are not code-signed yet, and WinDiag changes system settings (registry, BCD, power) when you ask it to. Some antivirus products may be suspicious of that. Each release is scanned with Kaspersky before it is published. If your antivirus flags something, please open an issue with the detection name and the version.

## Reporting a vulnerability

Please do not publish exploit details or a proof of concept before a fix exists. Preferred way: use GitHub's private vulnerability reporting (the "Report a vulnerability" button under the repository's **Security** tab), if it is enabled. Otherwise, open an issue labeled `security` without exploitable details and ask for a private channel, or contact [@dougllascda](https://github.com/dougllascda) on GitHub.
