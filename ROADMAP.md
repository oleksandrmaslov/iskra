# Iskra roadmap

This is the canonical forward plan. `AGENTS.md` and `CLAUDE.md` retain the
historical sprint handoff; new goals and acceptance gates live here.

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
2. Sprint 6.5 is conditional while batch mode remains disabled: cross-station
   locking is not a blocker for the current unbatched use case. If production
   enables batches, the shared lock becomes a release blocker and must include
   product, version, firmware SHA-256, target descriptor, station identity, and
   an explicit offline policy. Recommended policy: fail closed; supervisor
   recovery is a separately audited action.
3. Sprint 7: trustworthy board identity is now a production gate, not optional
   polish. `bmp_match` identifies only an MCU family and cannot distinguish two
   products built on the same chip. Add a signed catalog board-ID/UID policy and
   read it before any flash write.
4. Add real ELF/HEX load-range validation against catalog-declared flash/RAM
   address ranges, not only `flash_kb` and file-format checks.

## Sprint 8 — cross-platform application

### 8.0 — platform-neutral application layer

**In progress:** `Iskra.Application` now owns fail-closed catalog-session
selection, station readiness, optional batch policy, the flash transaction,
read-only history/export, and shared settings validation/persistence. WPF
consumes these services; Avalonia consumes the safe read-only subset.
Authentication, update, and cloud-sync orchestration still need extraction.

- Extract startup, catalog, flash, history, settings, authentication, update,
  and cloud-sync orchestration from `MainWindow.xaml.cs` into testable services
  and view models.
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
- Extend `--doctor` with udev/serial permissions, secure-store readiness, GDB
  provenance, filesystem permissions, and the current runtime identifier.

Started in the 2026-07-12 slice: `ITokenStore`, Unix GDB endpoints, Linux BMP
sysfs discovery, and a generic `net10.0` CLI with private-token features gated
until secure-store adapters exist.

### 8.2 — Avalonia operator UI redesign

**Alpha started:** `src/Iskra.Desktop` provides the localized four-tab shell,
shared read-only readiness checks, and real recent-history rows. The title and
header identify it as an alpha with flashing disabled. Its only settings write
is the shared narrow language update, which reloads the latest file before the
atomic save. No Windows/Linux/macOS visual, workflow, packaging, or HIL parity
is claimed by this slice.

- Continue `src/Iskra.Desktop` beside `src/Iskra.Wpf` using MVVM/commands.
- Port the four operator tabs and Device Flow dialog without weakening the
  single-action factory flow, giant PASS/FAIL state, hotkey safety, or complete
  Ukrainian/English/German presentation.
- ✅ Add persisted Ukrainian/English/German selection across WPF and Avalonia,
  plus invocation-level `--lang uk|en|de` for CLI. Keep Ukrainian as the
  compatibility default and keep logs/protocol values language-neutral.
- Centralize color, spacing, typography, focus, high-contrast, and semantic
  status resources. Add Avalonia headless UI tests.
- Keep WPF maintained as a supported Windows variant. Avalonia may become the
  cross-platform/default frontend only after Windows behavior parity,
  packaged-app acceptance, and HIL; it does not delete WPF support.

### 8.3 — packaging, CI, and HIL parity

- First release order: Windows x64, Ubuntu/Debian x64, macOS arm64, then macOS x64.
- Keep WiX for Windows; add a Linux package/udev policy and signed/notarized macOS
  `.app`/DMG. Select updates by exact OS and architecture.
- Add Windows/Linux/macOS CI, locked restores, vulnerability gates, SBOM and
  provenance, publish smoke tests, and per-OS BMP HIL. Establish and apply a
  reviewed `.editorconfig`/formatter baseline before making format verification
  a required gate; the current repository has broad pre-existing style drift.

## Sprint 8.4 — approved branding

- Integrate only the new owner-approved brand pack; the retired
  `design_assets/` files are intentionally deleted.
- Apply the design system to Avalonia resources, app/window icons, Windows MSI
  and Burn, Linux desktop icons, macOS ICNS, README, and GitHub social preview.
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
