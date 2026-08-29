# Iskra

Iskra is a factory flashing tool for ARM Cortex-M targets supported by Black
Magic Probe. It verifies a signed firmware catalog, checks firmware integrity
and memory ranges, drives a guarded `gdb` transaction, and records every attempt
in SQLite.

> **Status (2026-08-28): 2.2.1 engineering release, not factory-approved.**
> WPF remains the supported Windows variant. The Avalonia app and CLI now build
> for Windows, Linux, and macOS and share the same flash workflow, but production
> signing, clean-machine/HIL evidence, catalog-key rotation, board identity, and
> append-only central audit logging remain release gates. See
> [`docs/ARCHITECTURE_SECURITY_AUDIT_2026-08-25.md`](docs/ARCHITECTURE_SECURITY_AUDIT_2026-08-25.md)
> and [`docs/RELEASE_EVIDENCE_2.2.1.md`](docs/RELEASE_EVIDENCE_2.2.1.md).

Release history is in [`CHANGELOG.md`](CHANGELOG.md); forward gates are in
[`ROADMAP.md`](ROADMAP.md).

## Architecture

```text
WPF (Windows)     Avalonia (Win/Linux/macOS)     CLI
       \                    |                    /
        +----------- Iskra.Application --------+
        | workflows, readiness, settings, history, auth |
        +--------------------+-------------------+
                             |
                         Iskra.Core
       signed catalog, rollback floor, firmware validation,
       guarded GDB/MI session, SQLite log, GitHub clients
                             |
                  Black Magic Probe + target
```

Repository layout:

```text
src/Iskra.Core/          Trust, firmware, GDB, storage, platform adapters
src/Iskra.Application/   UI-neutral operator workflows and policy
src/Iskra.Wpf/           Supported Windows operator UI
src/Iskra.Desktop/       Cross-platform Avalonia operator UI
src/Iskra.Cli/           Cross-platform console application
tests/                   Core, Application, and headless Avalonia suites
installer/               WiX, Linux, macOS, and portable bundle builders
```

The flash path fails closed: a verified catalog session and exactly one probe
are required; revoked releases, bad hashes, invalid ELF/HEX maps, target
mismatches, and audit-write failures cannot report PASS. GDB scans and attaches
to the physical target before the application gate; it does not open or load the
firmware until that gate accepts the target.

## Platform support

| Platform | UI/package path | Secure GitHub token store | Current acceptance |
|---|---|---|---|
| Windows x64 | WPF and Avalonia; MSI/Burn setup | Per-user DPAPI store | Code/tests and local packaging; renewed HIL and Authenticode pending |
| Linux x64/arm64 | Avalonia + CLI; portable tar and native `.deb` | Secret Service via `secret-tool` | Code/cross-publish complete; native clean-machine, udev, keyring, and HIL pending |
| macOS arm64/x64 | Avalonia `.app` + CLI; tar/zip/DMG | Login Keychain via `/usr/bin/security` | IOKit-backed code/cross-publish complete; native signing/notarization, Keychain, and HIL pending |

There is no plaintext credential fallback. Missing or locked platform credential
services disable sign-in and private firmware acquisition. Linux packages use a
least-privilege udev rule (`0660`, `uaccess`) rather than world-writable serial
devices. macOS probe discovery binds `/dev/cu.usbmodem*` to IOKit
VID/PID/interface/serial or location identity and refuses unidentified
endpoints; native reconnect/contention HIL remains an acceptance gate.

Operator presentation supports Ukrainian (default), English, and German in WPF,
Avalonia, and CLI (`--lang uk|en|de`). Protocol values, CLI flags, hashes, error
codes, logs, and raw GDB output remain English/ASCII.

## Build and test

The repository pins .NET SDK 10.0.301 in [`global.json`](global.json), uses
locked NuGet dependency graphs, and pins WiX in
[`dotnet-tools.json`](.config/dotnet-tools.json).

```powershell
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
& $dotnet restore Iskra.sln --locked-mode
& $dotnet build Iskra.sln -c Release --no-restore -warnaserror
& $dotnet test Iskra.sln -c Release --no-build --no-restore
& $dotnet list Iskra.sln package --vulnerable --include-transitive --no-restore
```

Run the Avalonia app:

```powershell
& $dotnet run --project src/Iskra.Desktop
```

Run station diagnostics:

```powershell
& $dotnet run --project src/Iskra.Cli -- --doctor
```

## Engineering release artifacts

Build a self-contained Windows x64 bundle containing WPF, Avalonia, and CLI:

```powershell
pwsh ./installer/build-localized-exes.ps1 -Version 2.2.1
```

Build the WPF and side-by-side Avalonia Windows installers:

```powershell
pwsh ./installer/build-installer.ps1 -Version 2.2.1
pwsh ./installer/build-avalonia-installer.ps1 -Version 2.2.1
```

Build deterministic portable Linux/macOS bundles from any host:

```powershell
pwsh ./installer/build-unix-bundles.ps1 -Version 2.2.1 -AllowDirty
```

Native `.deb` and macOS DMG creation must run on matching native hosts:

```bash
# Linux x64 or arm64
ISKRA_ALLOW_UNSIGNED=1 bash installer/build-linux-package.sh 2.2.1

# macOS arm64 or x64
ISKRA_ALLOW_UNSIGNED=1 bash installer/build-macos-package.sh 2.2.1
```

`ISKRA_ALLOW_UNSIGNED=1` is for clearly labelled engineering artifacts only.
Official Linux builds require a GPG key for the digest manifest. Official macOS
builds require a Developer ID identity and notarization profile. Tagged Windows
publication is intentionally blocked until Authenticode and timestamping are
configured. The required environment, secrets, and fail-closed policy are
documented in [`docs/RELEASE_SIGNING.md`](docs/RELEASE_SIGNING.md).

Generate and validate an SPDX SBOM for any unpacked release drop:

```powershell
pwsh ./installer/generate-sbom.ps1 -Version 2.2.1 `
  -BuildDropPath ./artifacts/Iskra-2.2.1-win-x64 `
  -OutputPath ./artifacts/Iskra-2.2.1-win-x64.spdx.json
```

The CI matrix builds/tests Windows x64, Ubuntu x64/arm64, and macOS arm64/x64
natively. The release workflow creates native packages only on the corresponding
OS/architecture and refuses missing release signing credentials.

## Catalog and lab mode

Signed catalogs are required by default. Unsigned sideloading requires both
`--allow-unsigned-catalog` and `ISKRA_LAB_ALLOW_UNSIGNED_CATALOG=1`; manual
firmware overrides also require `--allow-manual-flash`. These switches are
compiled out of ordinary Release binaries and require an explicitly
lab-enabled build. Signed-catalog target, hash, and firmware overrides require
the same explicit lab/manual gate and must never be enabled on an operator
station.

## Factory acceptance gates

Do not deploy this candidate to a production batch until all stop-ship items in
the audit are closed. The critical remaining items are:

- rotate the embedded development catalog key to an offline/HSM-controlled
  production key and deploy reviewer-gated catalog signing;
- Authenticode-sign Windows artifacts, sign the Linux release manifest/repo,
  and Developer-ID-sign plus notarize the macOS app and DMG;
- replace mutable shared-key GitHub log shipping with per-station authenticated,
  append-only/tamper-evident ingestion;
- add trustworthy per-product board identity, not only MCU-family matching;
- complete clean-machine install, secure-store, USB identity/permissions,
  wrong-board, offline, rollback/recovery, and HIL tests on every supported OS
  and architecture;
- repeat the 50-consecutive-PASS bench run with signed release artifacts after
  the guarded GDB transaction changes.

WPF remains available and maintained during this acceptance process. Avalonia
does not remove or silently replace it.

## License

TBD.
