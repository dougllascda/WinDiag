# Third-party notices

WinDiag is distributed under the MIT License (see [LICENSE](LICENSE)). It uses the third-party components below, each under its own license. The full license texts are available at the links given.

## Distributed with WinDiag

### CommunityToolkit.Mvvm
- License: MIT. Copyright (c) .NET Foundation and Contributors.
- Project: https://github.com/CommunityToolkit/dotnet
- License text: https://github.com/CommunityToolkit/dotnet/blob/main/License.md

### System.Management
- License: MIT. Copyright (c) .NET Foundation and Contributors / Microsoft.
- Project: https://github.com/dotnet/runtime
- License text: https://github.com/dotnet/runtime/blob/main/LICENSE.TXT

### .NET 8 runtime (self-contained builds only: installer and portable)
- License: MIT. Copyright (c) .NET Foundation and Contributors / Microsoft.
- Projects: https://github.com/dotnet/runtime and https://github.com/dotnet/wpf
- License text: https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
- The runtime's own third-party notices: https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT

### LibreHardwareMonitorLib 0.9.6
- License: Mozilla Public License 2.0 (MPL-2.0).
- Project and source code: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor (version 0.9.6, commit 3d331e3370efb858411f19511373eff65a218701)
- License text: https://www.mozilla.org/en-US/MPL/2.0/
- The library is used **unmodified**, as a binary dependency. The source code of the version used is available in the repository above. WinDiag's own files remain under the MIT License.
- This version reads CPU, motherboard and memory sensors through the PawnIO driver (not WinRing0). WinDiag does **not include, download or install** that driver; it is used only if the user installed it from the official website.

Dependencies of LibreHardwareMonitorLib 0.9.6 for .NET 8 (as listed in the package .nuspec):

### HidSharp 2.6.4
- License: Apache License 2.0. Copyright 2010-2025 James F. Bellinger.
- Project: https://www.zer7.com/software/hidsharp
- License text: https://www.apache.org/licenses/LICENSE-2.0

### DiskInfoToolkit 1.1.2
- License: Mozilla Public License 2.0 (MPL-2.0). Copyright Florian K.
- Project and source code: https://github.com/Blacktempel/DiskInfoToolkit
- License text: https://www.mozilla.org/en-US/MPL/2.0/

### RAMSPDToolkit-NDD 1.4.2
- License: Mozilla Public License 2.0 (MPL-2.0). Copyright Florian K.
- Project and source code: https://github.com/Blacktempel/RAMSPDToolkit
- License text: https://www.mozilla.org/en-US/MPL/2.0/

### BlackSharp.Core 1.0.7 (dependency of DiskInfoToolkit and RAMSPDToolkit-NDD)
- License: Mozilla Public License 2.0 (MPL-2.0). Copyright Florian K.
- Project and source code: https://github.com/Blacktempel/BlackSharp
- License text: https://www.mozilla.org/en-US/MPL/2.0/

### Mono.Posix.NETStandard 1.0.0
- License: the package .nuspec has no SPDX expression; it only points to https://go.microsoft.com/fwlink/?linkid=869050 (Microsoft terms for .NET libraries). Copyright (c) Microsoft Corporation. Check that text before redistributing.
- Note: the package is referenced by LibreHardwareMonitorLib, but its code targets Linux/macOS and is not used on Windows.

### System.IO.Ports 10.0.3, System.Threading.AccessControl 10.0.3 and System.Management 10.0.2
- License: MIT. Copyright (c) .NET Foundation and Contributors / Microsoft.
- Project: https://github.com/dotnet/runtime
- License text: https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
- Indirect dependencies under the same license: System.CodeDom 10.0.2, System.Configuration.ConfigurationManager 8.0.1, System.Diagnostics.EventLog 8.0.1, System.Security.Cryptography.ProtectedData 8.0.0 and the runtime.*.native.System.IO.Ports 10.0.3 packages (other operating systems only).

## Used only for development (not distributed)

### xUnit
- License: Apache License 2.0. Used only by the test project (WinDiag.Tests); not part of the published executables.
- Project: https://github.com/xunit/xunit
- License text: https://www.apache.org/licenses/LICENSE-2.0

## Trademarks

NVIDIA, GeForce, Intel, AMD, Windows, CPU-Z, GPU-Z, HWiNFO and other names mentioned in the documentation and in the interface belong to their respective owners. They appear only to describe features or reference tools. WinDiag is not affiliated with, sponsored or endorsed by these owners.
