# Iskra.Desktop preview

This project is the first side-by-side Avalonia shell for the cross-platform
port. It is **not yet a feature-complete operator app and must not replace the
shipping WPF app**.

The Ukrainian preview has the same four top-level destinations as WPF:
Прошивка, Історія, Каталог, and Налаштування. It reads the existing settings
and consumes the shared `Iskra.Application` catalog session,
station-readiness service, and batch policy. It performs read-only checks for
exactly one Black Magic Probe, ARM GDB, the signed catalog, and the local SQLite
path. Zero or multiple probes are blocked states. Flashing and settings
mutation stay disabled until the shared workflow has test and
hardware-in-the-loop parity.

The approved repository-wide runtime upgrade is complete: `global.json` pins
.NET SDK 10.0.301, this project targets .NET 10, and Avalonia is pinned to
12.1.0. That toolchain migration does not change the preview's safety scope.

No Windows/Linux/macOS visual, packaging, or HIL parity is claimed by this
preview.

```powershell
$dotnet = "C:\Users\IMT - Teilnehmer\AppData\Local\Microsoft\dotnet\dotnet.exe"
& $dotnet run --project src\Iskra.Desktop
```
