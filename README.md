# Iskra

Factory flashing tool for ARM Cortex-M targets supported by Black Magic Probe.
It drives the probe through `arm-none-eabi-gdb`, validates signed firmware
catalog metadata, and logs every flash attempt to SQLite.

> **Status (2026-07-12):** the Windows WPF app, CLI, catalog/signature flow,
> logging, and installer are in place. The UI-neutral application layer and a
> safe, read-only Avalonia preview now build beside WPF. The audited build
> remains lab-ready rather than factory-production-ready until the gates in
> [`ROADMAP.md`](ROADMAP.md) are closed.

## Repository layout

```text
Iskra.sln
src/
  Iskra.Core/        Target-agnostic flashing and trust engine
  Iskra.Application/ UI-neutral catalog, readiness, and batch policy
  Iskra.Cli/         Console flasher
  Iskra.Wpf/         Shipping Windows operator UI
  Iskra.Desktop/     Avalonia cross-platform preview (no flashing yet)
tests/
  Iskra.Core.Tests/        xUnit tests for the Core library
  Iskra.Application.Tests/ xUnit tests for shared application policy
installer/
  Product.wxs        App MSI
  Bundle.wxs         Factory setup bundle
```

## Current Windows app prerequisites

- Windows 10 / 11
- .NET SDK 10.0.301 for development builds (pinned by `global.json`)
- ARM GNU Toolchain for development runs, unless using the setup bundle
- A Black Magic Probe attached to the target board

The Windows/Linux/macOS Avalonia migration, platform adapters, release order,
and final security gates are tracked in [`ROADMAP.md`](ROADMAP.md).
Requirements for the new owner-provided logos and brand system are in
[`docs/BRANDING_ASSET_REQUIREMENTS.md`](docs/BRANDING_ASSET_REQUIREMENTS.md).

## Current desktop UIs

`Iskra.Wpf` remains the production-path Windows UI. Settings now save
automatically when the operator leaves the Settings tab or closes the window;
the header shows unsaved, saved, and save-error states. Probe readiness has an
explicit refresh action, and flashing stays blocked unless exactly one BMP is
detected.

Batch mode is disabled by default for the current unbatched workflow. An opt-in
toggle sits beside the cloud-log `.pem` configuration. With batches disabled,
the hidden batch text is ignored, attempts use a blank batch ID, and no batch
reservation is created. The existing digest-based lock remains available when
the setting is enabled.

`Iskra.Desktop` is the first Ukrainian four-tab Avalonia preview. It consumes
the shared `Iskra.Application` catalog, readiness, and batch policies, but its
flash and settings-mutation controls are deliberately disabled until workflow
tests and hardware-in-the-loop parity exist. It does not replace WPF yet.

```powershell
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
& $dotnet run --project src\Iskra.Desktop
```

The repository now targets .NET 10 and pins SDK 10.0.301 through `global.json`.
`Iskra.Desktop` uses Avalonia 12.1.0. This runtime/UI-toolkit migration does not
by itself establish Windows/Linux/macOS visual, packaging, workflow, or HIL
parity; WPF remains the shipping operator UI until those gates pass.

## Installer

Build the factory installer bundle:

```powershell
pwsh ./installer/build-installer.ps1 -Version 1.2.3
```

Use `installer/out/Iskra-<ver>-setup-x64.exe` on operator stations. It checks
for Windows 10/11 x64, detects an existing `arm-none-eabi-gdb.exe`, and installs
the embedded Arm GNU Toolchain 15.2.rel1 before Iskra when GDB is missing. The
sibling `Iskra-<ver>-x64.msi` is app-only: it uses an MSI-native x64 Windows
compatibility check and blocks a fresh install if `arm-none-eabi-gdb.exe` is not
already installed. Use the setup EXE for new factory PCs.

The build also emits `installer/out/Iskra-<ver>-preinstall-check.ps1`. Run it
before setup on a new station:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\Iskra-1.2.3-preinstall-check.ps1
```

After setup, run:

```powershell
"C:\Program Files\Iskra\Iskra.Cli.exe" --doctor
```

### Station checklist

Pre-install:

- [ ] Windows 10/11 x64.
- [ ] At least 3 GB free on the system drive.
- [ ] Installer can run elevated as administrator.
- [ ] `Iskra-<ver>-setup-x64.exe` is present.
- [ ] Black Magic Probe is available for final station acceptance.
- [ ] GitHub/network access is available if firmware comes from private GitHub releases.

Installer installs:

- [ ] Setup EXE: checks Windows 10/11 x64 before install.
- [ ] Setup EXE: installs Arm GNU Toolchain 15.2.rel1 when `arm-none-eabi-gdb.exe` is missing.
- [ ] MSI: blocks app install when `arm-none-eabi-gdb.exe` is missing.
- [ ] Iskra WPF app and `Iskra.Cli.exe`.
- [ ] Bundled `examples/catalog.json` and `examples/catalog.json.sig`.
- [ ] Installed `check-station.ps1` for later diagnostics.

Post-install acceptance:

- [ ] `Iskra.Cli --doctor` reports no failures.
- [ ] `Iskra.Cli --login` succeeds if private GitHub firmware is used.
- [ ] The WPF status strip shows `gdb` found and one BMP GDB COM port.
- [ ] A known-good board flashes once before handing the station to operators.

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

Signed catalogs are required by default. Unsigned sideloading requires both
`--allow-unsigned-catalog` and the explicit lab-only environment variable
`ISKRA_LAB_ALLOW_UNSIGNED_CATALOG=1`; raw `--elf` mode additionally requires
`--allow-manual-flash`. Never set the variable on an operator station.

## Test

```powershell
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
dotnet test
```

Historical sprint handoff details live in `CLAUDE.md`; new work and acceptance
criteria live in `ROADMAP.md`.

## License

TBD.
