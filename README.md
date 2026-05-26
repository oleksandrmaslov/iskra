# Iskra

Production Windows flashing tool for ARM Cortex-M targets supported by Black
Magic Probe. It drives the probe through `arm-none-eabi-gdb`, validates signed
firmware catalog metadata, and logs every flash attempt to SQLite.

> **Status:** WPF app, CLI, catalog/signature flow, logging, and installer are in place.

## Repository layout

```text
Iskra.sln
src/
  Iskra.Core/       Class library: services, state machine, models
  Iskra.Cli/        Console flasher
  Iskra.Wpf/        Operator UI
tests/
  Iskra.Core.Tests/ xUnit tests for the Core library
installer/
  Product.wxs       App MSI
  Bundle.wxs        Factory setup bundle
```

## Prerequisites

- Windows 10 / 11
- .NET 8 SDK for development builds
- ARM GNU Toolchain for development runs, unless using the setup bundle
- A Black Magic Probe attached to the target board

## Installer

Build the factory installer bundle:

```powershell
pwsh ./installer/build-installer.ps1 -Version 1.2.3
```

Use `installer/out/Iskra-<ver>-setup-x64.exe` on operator stations. It embeds
Arm GNU Toolchain 15.2.rel1 and installs it before Iskra, so
`arm-none-eabi-gdb` is available for Black Magic Probe flashing immediately
after setup. The sibling `Iskra-<ver>-x64.msi` is app-only and is kept as a
build artifact for diagnostics/IT use.

## Build

```powershell
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
dotnet build
```

## Run the CLI

```powershell
dotnet run --project src/Iskra.Cli -- `
  --catalog examples/catalog.json `
  --product ci-clop `
  --elf .\path\to\app.elf `
  --port \\.\COM30 `
  --power probe `
  --freq 1000000 `
  --connect-reset `
  --operator jdoe `
  --batch Lot-2026-05-25-A
```

## Test

```powershell
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
dotnet test
```

Detailed design and sprint status live in `CLAUDE.md`.

## License

TBD.
