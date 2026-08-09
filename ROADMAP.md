# Iskra roadmap

This is the canonical forward plan. `CHANGELOG.md` records what shipped; new
goals and acceptance gates live here.

## Current position

- Windows WPF remains a supported Windows operator UI throughout Sprint 8 and
  beyond. Avalonia is a cross-platform sibling, not a reason to remove the
  proven WPF station variant; any change of the default Windows frontend still
  requires feature parity and hardware-in-the-loop acceptance.
- `Iskra.Core` remains the target-agnostic flashing and trust engine.
- `Iskra.Application` is the UI-neutral seam for catalog-session,
  station-readiness, optional batch policy, flash transactions, read-only
  history/export, settings validation, and atomic settings persistence.
- The cross-platform target is Windows, Linux, and macOS with a native Avalonia
  desktop UI. `Iskra.Desktop` is now a read-only safety preview beside WPF, not
  a replacement or a claim of platform/HIL parity.
- Security audit status (2026-07-12): **lab-ready, not factory-production-ready**
  until the owner/architecture gates in Sprint 9 are closed.

## 2026-07-12 implementation slice

- Began the portable foundation: generic CLI target, `ITokenStore`, Unix GDB
  endpoints, Linux sysfs BMP discovery, and non-Windows diagnostics that refuse
  private-token operations until Keychain/libsecret adapters exist.
- Hardened signed-catalog defaults and gated unsigned/manual CLI modes, remote response limits/rollback checks,
  firmware-cache paths and staging, GDB startup/cancellation/verification,
  full-digest local batch reservations in both WPF and CLI, CSV export, key-file
  creation, dependency versions, and package locking.
- Reworked the catalog workflow templates around explicit repository approval,
  independent firmware hashing, immutable versions, pinned revisions/actions,
  and reviewer-gated signing. These templates still require owner deployment.
- Added `Iskra.Application` with fail-closed catalog selection, exactly-one-BMP
  station readiness, and shared optional-batch policy, plus focused tests.
- Extracted the complete flash transaction into the UI-neutral
  `FlashWorkflow`: catalog selection gates, revocation, optional batch
  reservation, local/remote firmware acquisition, preflight and SHA-256,
  two-phase GDB execution, and durable attempt logging. WPF now consumes this
  service through its Windows DPAPI/GitHub adapter and remains supported.
- Extracted `HistoryWorkflow`, `SettingsWorkflow`, and shared database-path
  policy. WPF now consumes the shared history/export and settings services;
  the Avalonia alpha shows real read-only recent history and uses a narrow
  reload-before-save language update so it cannot overwrite newer WPF values.
- Added the Ukrainian four-tab `Iskra.Desktop` Avalonia preview. It reads the
  current settings and performs read-only BMP/GDB/catalog/SQLite checks. It is
  explicitly branded alpha/read-only, and flash execution stays disabled until
  workflow-test and HIL parity with WPF.
- Improved the shipping WPF UX: Settings auto-save when leaving the tab or
  closing the window, save/dirty/error state is visible, BMP discovery has an
  explicit refresh action, and zero or multiple probes block flashing.
- Made batches opt-in and disabled by default. The toggle is beside the cloud
  log `.pem` setting; disabled mode records a blank batch ID and creates no
  reservation, while enabled mode retains the existing digest lock.
- Preserved the intentionally deleted `design_assets/` tree. Branding integration
  waits for the replacement pack defined below; no placeholder logo was added.
- Installed and pinned .NET SDK 10.0.301 in `global.json`, retargeted the
  solution to .NET 10, and migrated `Iskra.Desktop` to Avalonia 12.1.0. This
  completes the runtime/toolkit upgrade, not visual, packaging, workflow, or
  HIL parity.
- Hardened the Windows engineering-release scripts around locked restores and
  isolated publish staging. The standalone bundle names the preview
  `Iskra.Avalonia.Alpha.exe`; the WiX setup continues to install supported WPF
  plus CLI and embeds the SHA-256-pinned Arm GNU Toolchain prerequisite.

## Production blockers and conditional gates carried forward

1. Repeat the 50-consecutive-PASS bench run after the two-phase GDB changes.
2. **Sprint 6.5 deferred by owner decision (2026-08-08).** Batch mode is off in
   production, so cross-station locking is not a blocker and building it now
   would mean guessing at factory network topology. The gap is real and is
   recorded here rather than half-built:

   - **Today's behaviour.** `SqliteLogStore.ReserveBatchLock` is per station.
     Two stations running the same batch ID keep two independent locks and will
     not detect a conflicting product/version/digest between them. With batch
     mode disabled the app records a blank batch ID and reserves nothing, so
     there is nothing to diverge.
   - **Trigger to build it.** Enabling `BatchesEnabled` on more than one station
     that shares a batch ID. At that point the shared lock is a release blocker.
   - **Decided policy, so the implementer does not have to re-litigate it.**
     Fail closed, always: with batch mode on and the shared store unreachable,
     flashing stops. No silent fallback to the local lock — that reintroduces
     precisely the split brain the sprint exists to prevent. A supervisor
     override, if ever added, must be a separately audited action that writes an
     explicit override flag and reason into the attempt record.
   - **Required binding.** batch ID, product, version, firmware SHA-256, target
     descriptor (BMP match + flash size), and station identity. Note the current
     `BatchLockDescriptor` carries everything except station identity, so it
     needs one extra field.
   - **Open decision.** Where shared state lives. A shared network folder with
     atomic exclusive-create is the leading candidate (no server, no per-flash
     internet, supervisor-readable). The GitHub `iskra-logs` route is rejected
     for this purpose: it adds an internet round-trip to every flash in a
     500-unit batch and, with today's shared write key, lets a compromised
     station forge another station's lock.
3. Sprint 7: trustworthy board identity is now a production gate, not optional
   polish. `bmp_match` identifies only an MCU family and cannot distinguish two
   products built on the same chip. Add a signed catalog board-ID/UID policy and
   read it before any flash write.
4. ✅ **Done (2026-08-08).** ELF/HEX load-range validation against
   catalog-declared flash/RAM address ranges. `FirmwareImage` reads the real
   load map (ELF PT_LOAD physical addresses and file sizes; Intel HEX data
   records with extended segment/linear addressing), and `FirmwareRangeCheck`
   refuses anything that cannot belong to the target: `E_FW_TOO_LARGE` when the
   image exceeds `flash_kb`, `E_FW_ADDRESS_RANGE` when a segment falls outside a
   declared window. Wired into both `FlashWorkflow` and the CLI, after the
   SHA-256 check so a corrupt download still reports as an integrity failure.
   The catalog memory map is optional, so previously signed catalogs stay valid
   and fall back to size-only checking.

## Sprint 8 — cross-platform application

### 8.0 — platform-neutral application layer

**Extraction complete (2026-08-08):** `Iskra.Application` owns fail-closed
catalog-session selection, station readiness, optional batch policy, the flash
transaction, read-only history/export, settings validation/persistence, GitHub
credential classification, and cloud log status/shipping. WPF, Avalonia, and the
CLI all render the same snapshots rather than each carrying a copy.

- ✅ Extract startup, catalog, flash, history, settings, authentication, update,
  and cloud-sync orchestration from `MainWindow.xaml.cs` into testable services.
  `AuthWorkflow` classifies credentials (secure store absent, client not
  configured, corrupt, not signed in, session expired, signed in) and answers
  whether remote firmware can currently be fetched; `CloudLogWorkflow` owns
  ship-readiness, pending counts, and the manual flush. Update checking was
  deliberately left as direct `RemoteCatalogClient` / `AppUpdateClient` calls:
  those Core types already are the service, and wrapping them would add a layer
  that only forwards.
- Done for the flash transaction: `FlashWorkflow` is UI-neutral and covered by
  workflow tests for blocking, integrity refusal, batch conflict, remote-auth
  failure, target overrides, two-phase execution, and PASS/FAIL logging.
- Done for history/settings: `HistoryWorkflow` avoids creating a database during
  read-only inspection and centralizes export/batch counts; `SettingsWorkflow`
  centralizes invariant validation, normalization, trust locks, and atomic
  persistence. Toolkit dialogs, localization, and WPF dirty-state UX stay in
  their frontends.
- Keep Ukrainian operator text in the application/UI layer; Core error codes and
  diagnostics remain English/ASCII.
- ✅ Moved to .NET 10 LTS, pinned SDK 10.0.301 in `global.json`, and refreshed
  package locks.

The approved coordinated runtime upgrade is complete: the repository targets
.NET 10 and `Iskra.Desktop` uses Avalonia 12.1.0. WPF remains the shipping UI;
the upgrade does not waive the remaining feature-parity and HIL gates.

### 8.1 — OS adapters and CLI parity

- Secure credentials: Windows Credential Manager/DPAPI with restrictive ACL,
  Linux Secret Service/libsecret, macOS Keychain. Never add plaintext fallback.
- Probe discovery: Windows registry, Linux sysfs/udev, macOS IOKit; preserve a
  stable physical probe identity across reconnects.
- Platform paths, file dialogs, browser launch, clipboard, sound, and update
  package selection become interfaces.
- ✅ **`--doctor` extended (2026-08-08)** with the current runtime identifier and
  framework, GDB provenance (the toolchain's own `--version` banner, so a
  station running a different build than the installer pins is visible), shared
  credential classification via `AuthWorkflow`, and cloud-log readiness with the
  pending row count. Filesystem permission checks were already present. Linux
  udev/serial permission checks remain, and need a Linux station to verify.

Started in the 2026-07-12 slice: `ITokenStore`, Unix GDB endpoints, Linux BMP
sysfs discovery, and a generic `net10.0` CLI with private-token features gated
until secure-store adapters exist.

### 8.2 — Avalonia operator UI redesign

**Functional parity landed (2026-08-07, released as 2.0.0):** `src/Iskra.Desktop`
executes the same `FlashWorkflow` transaction as WPF — giant PASS/FAIL band in
the WPF palette, full-width FLASH button with the configured hotkey, and a live
dark gdb console. Gating, pre-request probe rediscovery, batch locking, and
durable SQLite logging all come from the shared application layer, so no safety
policy is duplicated in the frontend. Settings are fully editable through
`SettingsWorkflow` with auto-save on tab change and window close; GitHub Device
Flow, catalog/app update checks, cloud-log upload, and CSV export are all
present. The header badge reads "HIL acceptance pending" — the remaining gap is
bench acceptance and non-Windows packaging, not features.

- ✅ Continue `src/Iskra.Desktop` beside `src/Iskra.Wpf` using MVVM/commands.
- ✅ Port the four operator tabs and Device Flow dialog without weakening the
  single-action factory flow, giant PASS/FAIL state, hotkey safety, or complete
  Ukrainian/English/German presentation. Done in 2.0.0: Flash tab, PASS/FAIL
  band, hotkey, gdb console, Device Flow window, editable settings with
  auto-save, catalog/app update checks, cloud-log upload, and CSV export.
  Completed 2026-08-08: the catalog browser now shows per-release detail
  (version, default/GitHub/revoked badges, firmware kind, date, artefact name,
  full SHA-256, revocation reason) and the startup background catalog fetch
  raises a reload notice rather than swapping the catalog under a station that
  may be mid-batch. No WPF-only operator surface remains.
- Remote firmware on Linux/macOS stays fail-closed until an encrypted
  `ITokenStore` exists for those platforms; the Device Flow window itself is
  cross-platform but the credential store is not.
- ✅ Add persisted Ukrainian/English/German selection across WPF and Avalonia,
  plus invocation-level `--lang uk|en|de` for CLI. Keep Ukrainian as the
  compatibility default and keep logs/protocol values language-neutral.
- Centralize color, spacing, typography, focus, high-contrast, and semantic
  status resources.
- ✅ **Avalonia headless UI tests (2026-08-08).** `tests/Iskra.Desktop.Tests`
  runs the real `App` on Avalonia's headless platform via `Avalonia.Headless.XUnit`
  (which requires xunit v3; the Core and Application suites stay on v2). Covers
  flash gating for probe/gdb/catalog/operator/batch, banner colour and text per
  state, catalog selection and remote-release labelling, PASS and refusal paths
  through the real workflow, settings validation and auto-save, hotkey mapping,
  and language switching. It immediately found a real defect: a dispatcher-queued
  progress report could repaint over the final PASS/FAIL verdict — fixed in both
  Avalonia and WPF.
- Keep WPF maintained as a supported Windows variant. Avalonia may become the
  cross-platform/default frontend only after Windows behavior parity,
  packaged-app acceptance, and HIL; it does not delete WPF support.

### 8.3 — packaging, CI, and HIL parity

**Windows Avalonia packaging done (2026-08-08).** `installer/Product.Avalonia.wxs`,
`Bundle.Avalonia.wxs`, and `build-avalonia-installer.ps1` produce an
`Iskra-Avalonia-<ver>-setup-x64.exe` carrying the same SHA-256-pinned Arm GNU
Toolchain as the WPF setup, so a bare station needs no other download. It is a
separate product by construction — distinct UpgradeCode, distinct ProductCode,
`C:\Program Files\Iskra Avalonia\`, its own Start Menu entry and registry key
path — so it installs beside the production WPF station and can never upgrade,
repair, or remove it. Both bundles mark the Arm toolchain permanent, so removing
either product leaves the compiler in place for the other. Toolchain pins live in
`installer/arm-toolchain.pins.ps1`, dot-sourced by both builders, so the two
setup EXEs cannot ship different compilers. Linux and macOS packaging remain.


- First release order: Windows x64, Ubuntu/Debian x64, macOS arm64, then macOS x64.
- Keep WiX for Windows; add a Linux package/udev policy and signed/notarized macOS
  `.app`/DMG. Select updates by exact OS and architecture.
- Add Windows/Linux/macOS CI, locked restores, vulnerability gates, SBOM and
  provenance, publish smoke tests, and per-OS BMP HIL.
- ✅ **`.editorconfig` baseline added (2026-08-08)**, describing the house style
  with every rule at `suggestion` severity. It is intentionally not a gate: the
  repository still has pre-existing drift, so a formatting sweep must land
  before `dotnet format --verify-no-changes` becomes required. `CA1416` is kept
  at warning — it is what stops a Windows-only credential call from silently
  shipping in the portable CLI or the cross-platform frontend.

## Sprint 8.4 — approved branding

- Integrate only the new owner-approved brand pack; the retired
  `design_assets/` files are intentionally deleted.
- ✅ **Windows installer branding (2026-08-07).** The owner-supplied
  `docs/wix-banner.png` (493x58) and `docs/wix-dialog.png` (493x312) are
  converted to `installer/assets/banner.bmp` and `dialog.bmp` — Windows
  Installer bitmap controls render DIB/BMP only, never PNG — and bound through
  `WixUIBannerBmp` / `WixUIDialogBmp`. `docs/favicon.ico` ships as
  `installer/assets/iskra.ico` for `ARPPRODUCTICON`, the Start Menu shortcut,
  and the Burn bundle icon; a 64x64 `logo.png` is the bootstrapper `LogoFile`.
  Verified by an MSI smoke build: the embedded `WixUI_Bmp_Banner` and
  `WixUI_Bmp_Dialog` streams match the generated files byte-for-byte.
- Apply the design system to Avalonia resources, app/window icons, Linux desktop
  icons, macOS ICNS, README, and GitHub social preview.
- The required handoff is listed in
  [`docs/BRANDING_ASSET_REQUIREMENTS.md`](docs/BRANDING_ASSET_REQUIREMENTS.md).
- Brand colors never replace accessible semantic PASS/FAIL/warning states.

## Sprint 9 — final production security and release acceptance

This is the last overall security gate after cross-platform and branding work.
It is complete only when all of the following are evidenced, not merely planned:

- Production catalog key rotated out of the current dev key into offline/HSM/KMS
  custody; key ID and rehearsed rotation/revocation process documented.
- Reviewer-gated catalog signing deployed, firmware repositories explicitly
  allowlisted, release bytes independently re-hashed, and existing versions
  immutable.
- Protected branches/rulesets/CODEOWNERS, least-privilege Actions, pinned actions,
  Dependabot, CodeQL/dependency review, and secret scanning enabled.
- WPF/Avalonia/CLI, MSI/Burn, Linux packages, and macOS app are code-signed and
  timestamped/notarized; signed digest manifest, SBOM, and provenance published.
- GDB/toolchain provenance is pinned and verified; firmware cannot auto-load GDB
  scripts; cancellation kills the full process tree.
- Per-station authenticated, append-only/tamper-evident central logging replaces
  the shared repository-wide write key. Operator identity is authenticated.
- If production batch mode is enabled, digest-based local and cross-station
  reservations are fail-closed and concurrency-tested.
- Board identity and firmware address-range checks pass on every supported OS.
- Parser/catalog fuzzing, rollback/crash recovery, clean-machine installation,
  offline behavior, wrong-board refusal, and full HIL matrix pass.
- Renewed 50-PASS production run completes with signed release artifacts.

## Owner decisions before cross-platform production parity

Recommended defaults are in parentheses:

1. Platform order: Windows x64, Ubuntu/Debian x64, macOS arm64/x64.
2. Private GitHub firmware on Linux/macOS v1: defer and use public signed assets
   until Keychain/libsecret support is complete.
3. Station account model: one locked-down service/operator account per station.
4. GDB distribution: pinned OS prerequisite initially; bundle only after signing
   and license/provenance review.
5. Brand lockup: `ISKRA`, `ІСКРА`, or both; light+dark themes are recommended.
6. ✅ Runtime baseline approved and completed: per-user SDK 10.0.301,
   repository pin via `global.json`, .NET 10 targets, and Avalonia 12.1.0.
