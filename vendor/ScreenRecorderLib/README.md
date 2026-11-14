# Clipsy recorder patches

This package builds ScreenRecorderLib 6.6.0 from upstream commit
`39ad1e2f1750fa06669b73743dbbaa25371dec21` with recording fixes:

- Clear Windows Graphics Capture's minimum update interval when the operating system supports it. Windows 11 otherwise uses a 16 ms interval that can deliver fewer than 60 fresh frames per second on high-refresh displays.
- Schedule constant-rate capture against the media clock so frame-copy overhead does not accumulate in every frame interval. Variable-rate recording keeps the upstream behavior.
- Preserve captured surfaces when resuming so static content does not stall while waiting for a repaint.

The package contains rebuilt x64 and ARM64 libraries. Clipsy's normal build restores
the checked-in package from `vendor/packages`; it does not require a C++ toolchain.
The upstream MIT license is included.

## Rebuild

Install Visual Studio C++ v143 tools for x64 and ARM64, ATL, Windows SDK 10.0.26100
or newer, and the .NET Framework 4.8 targeting pack. ARM64 linking also needs the
.NET Framework 4.8.1 SDK's ARM64 import libraries.

Run `vendor/ScreenRecorderLib/rebuild.ps1` from PowerShell. It checks the upstream
commit, applies `capture.patch`, copies `FramePacing.h`, builds both architectures,
runs the frame pacing tests, and packs the local NuGet dependency. Review the
package hash and changes before committing a rebuilt package.

`test-frame-pacing.ps1` also runs independently with the Microsoft x64 C++ compiler
and Windows SDK. It checks sustained cadence with variable capture/render costs
and recovery after a missed deadline.
