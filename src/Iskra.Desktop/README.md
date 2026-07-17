# Iskra.Desktop alpha

This project is the side-by-side Avalonia alpha for the cross-platform port. It
is not yet a feature-complete operator app and must not replace the supported
WPF Windows app.

The localized alpha has the same four top-level destinations as WPF. It reads
the existing settings and consumes shared `Iskra.Application` catalog,
station-readiness, batch, history, and settings policies. It performs read-only
checks for exactly one Black Magic Probe, ARM GDB, the signed catalog, and the
local SQLite path, and it shows real recent attempt rows without creating a
missing database. Zero or multiple probes are blocked states.

The title and header explicitly identify this build as alpha/read-only with
flashing disabled. The language selector is its only settings write: it reloads
the latest settings and atomically saves only the language change so a stale
alpha session cannot replace newer WPF settings. All other settings mutation
and every flash action stay disabled until workflow and hardware-in-the-loop
parity.

`global.json` pins .NET SDK 10.0.301, the project targets .NET 10, and Avalonia
is pinned to 12.1.0. No Windows/Linux/macOS visual, packaging, or HIL parity is
claimed by this alpha.

```powershell
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
& $dotnet run --project src\Iskra.Desktop
```
