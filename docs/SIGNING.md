# WinDiag code signing

A guide for the maintainer. **Prices, deadlines, eligibility rules and availability by country change often: always check the official websites before deciding.** This document describes the paths at a high level and does not guarantee any figures.

## Why sign

- **Integrity and authorship:** an Authenticode signature binds the `.exe` to a certificate. Any change to the file invalidates the signature, so nobody can modify the binary without it being detectable.
- **Publisher name:** UAC and the file Properties show the verified publisher instead of "Unknown".
- **Limit:** signing does **not** remove the SmartScreen warning immediately. Reputation builds over time and with the number of downloads. While there is no signature (or reputation), users may see "More info > Run anyway".

Even without a certificate, the repository already provides `SHA256SUMS.txt` and the GitHub build provenance attestation (`actions/attest-build-provenance`). They prove which commit and workflow produced the file, but they do not replace a signature for Windows.

## How the repository is prepared

- `tools\publish.ps1` runs the `WINDIAG_SIGN_CMD` environment variable for every `.exe` (portable, fx and installer) before computing the hashes. The `{file}` token becomes the file path. Local example:
  ```powershell
  $env:WINDIAG_SIGN_CMD = 'signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /f C:\cert\codesign.pfx /p PASSWORD "{file}"'
  pwsh -File tools\publish.ps1
  ```
- `release.yml` has an active step for a `.pfx` certificate stored as a secret (option B below) and a commented block for SignPath (option A).
- Always use a timestamp (`/tr` + `/td SHA256`), so the signature stays valid after the certificate expires.

## Options

### A. SignPath Foundation (free for open source)
1. Check https://signpath.org/ for the current criteria for open source projects (approved open source license, public repository; WinDiag is source-available under a proprietary license, so it may not qualify, no prohibited components, etc.) and submit the application. There is a manual review and approval.
2. After approval, create the project and the signing policy in the SignPath dashboard and install/connect the GitHub app as described in their documentation.
3. Generate an API token and store it in **Settings > Secrets > Actions** as `SIGNPATH_API_TOKEN`.
4. In `release.yml`, uncomment the "SignPath" block: the build uploads the unsigned artifact, SignPath signs it and returns it in `dist-signed`. Adjust `organization-id`, `project-slug` and `signing-policy-slug`.
5. Because signing happens after the build, copy the signed files back to `dist\` and **regenerate `SHA256SUMS.txt`** before attesting and publishing. The installer must be signed too (and the inner `.exe`, if you want the installed app signed, must be signed before building the Setup; in this flow that requires splitting the build into two stages).
6. Note: approved projects usually have transparency requirements (for example a privacy/signing policy and a build reproducible by CI). Read the current policy.

### B. Certum Open Source Code Signing (individual, low cost)
1. Check Certum's website (https://shop.certum.eu/) for the current "Open Source Code Signing" offer and conditions (open source project, identity verification).
2. Buy it, complete the identity verification and receive the certificate on a card/reader or in the cloud (SimplySign).
3. Local: install Certum's software, authenticate (SimplySign needs an active session) and use `WINDIAG_SIGN_CMD` with `signtool` pointing to the certificate installed in the Windows store (`/a` or `/sha1 <thumbprint>`).
4. CI: certificates on a card or in the cloud usually do **not** export the `.pfx`; in that case sign locally or use a self-hosted runner. If the vendor allows an exportable `.pfx`, convert it to base64 (`[Convert]::ToBase64String([IO.File]::ReadAllBytes('cert.pfx'))`) and save it in the secrets `SIGN_PFX_BASE64` and `SIGN_PFX_PASSWORD`. The "Preparar assinatura (signtool)" step of `release.yml` already uses these names and sets `WINDIAG_SIGN_CMD` by itself.

### C. Azure Trusted Signing (Azure Artifact Signing)
1. Check the Microsoft portal for availability in your country and account type (individual or organization) and the current price. Availability by country varies.
2. Create the signing account, complete identity validation and create a certificate profile.
3. In CI, use Microsoft's official signing action with OIDC/Service Principal authentication (`AZURE_*` secrets), or call `signtool` with the Trusted Signing dlib through `WINDIAG_SIGN_CMD`. The certificates are short-lived, which is why the timestamp is essential.

### D. EV certificate
Check the current conditions. Historically it was expensive and gave immediate SmartScreen reputation, but that special advantage was reduced starting in 2024. For a small independent project, options B and C are usually more proportionate. Reassess only if the rules change.

## How end users verify

1. **Signature (when it exists):** right-click the `.exe` > Properties > Digital Signatures > Details. Or:
   ```powershell
   Get-AuthenticodeSignature .\WinDiag-Setup-<version>.exe | Format-List Status, SignerCertificate
   ```
   `Status` should be `Valid`.
2. **SHA256:** compare with `SHA256SUMS.txt` from the Release:
   ```powershell
   Get-FileHash .\WinDiag-Setup-<version>.exe -Algorithm SHA256
   ```
3. **Provenance (GitHub CLI):**
   ```powershell
   gh attestation verify .\WinDiag-Setup-<version>.exe --repo dougllascda/WinDiag
   ```
   Confirms that the file was built by this repository's release workflow.
