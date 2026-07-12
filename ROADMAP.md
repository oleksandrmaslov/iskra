# Iskra roadmap

This is the canonical forward plan. `AGENTS.md` and `CLAUDE.md` retain the
historical sprint handoff; new goals and acceptance gates live here.

## Current position

- Windows WPF remains the shipping operator UI until its replacement reaches
  feature parity and passes hardware-in-the-loop acceptance.
- `Iskra.Core` remains the target-agnostic flashing and trust engine.
- The cross-platform target is Windows, Linux, and macOS with a native Avalonia
  desktop UI. Migration happens beside WPF, not as a flag-day rewrite.
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
- Preserved the intentionally deleted `design_assets/` tree. Branding integration
  waits for the replacement pack defined below; no placeholder logo was added.

## Production blockers carried forward

1. Repeat the 50-consecutive-PASS bench run after the two-phase GDB changes.
2. Sprint 6.5: cross-station batch locking. The shared lock must include product,
   version, firmware SHA-256, target descriptor, station identity, and an explicit
   offline policy. Recommended production policy: fail closed; supervisor recovery
   is a separately audited action.
3. Sprint 7: trustworthy board identity is now a production gate, not optional
   polish. `bmp_match` identifies only an MCU family and cannot distinguish two
   products built on the same chip. Add a signed catalog board-ID/UID policy and
   read it before any flash write.
4. Add real ELF/HEX load-range validation against catalog-declared flash/RAM
   address ranges, not only `flash_kb` and file-format checks.

## Sprint 8 — cross-platform application

### 8.0 — platform-neutral application layer

- Extract startup, catalog, flash, history, settings, authentication, update,
  and cloud-sync orchestration from `MainWindow.xaml.cs` into testable services
  and view models.
- Keep Ukrainian operator text in the application/UI layer; Core error codes and
  diagnostics remain English/ASCII.
- Move from .NET 8 to .NET 10 LTS before production rollout (the current .NET 8
  support window ends in November 2026). Pin the SDK and package locks.

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
sysfs discovery, and a generic `net8.0` CLI with private-token features gated
until secure-store adapters exist.

### 8.2 — Avalonia operator UI redesign

- Add `src/Iskra.Desktop` beside `src/Iskra.Wpf` and use MVVM/commands.
- Port the four operator tabs and Device Flow dialog without weakening the
  single-action factory flow, giant PASS/FAIL state, hotkey safety, or Ukrainian UI.
- Centralize color, spacing, typography, focus, high-contrast, and semantic
  status resources. Add Avalonia headless UI tests.
- Retire WPF only after Windows behavior parity, packaged-app acceptance, and HIL.

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
- Digest-based local and cross-station batch reservations are fail-closed and
  concurrency-tested.
- Board identity and firmware address-range checks pass on every supported OS.
- Parser/catalog fuzzing, rollback/crash recovery, clean-machine installation,
  offline behavior, wrong-board refusal, and full HIL matrix pass.
- Renewed 50-PASS production run completes with signed release artifacts.

## Owner decisions before Sprint 8.2

Recommended defaults are in parentheses:

1. Platform order: Windows x64, Ubuntu/Debian x64, macOS arm64/x64.
2. Private GitHub firmware on Linux/macOS v1: defer and use public signed assets
   until Keychain/libsecret support is complete.
3. Station account model: one locked-down service/operator account per station.
4. GDB distribution: pinned OS prerequisite initially; bundle only after signing
   and license/provenance review.
5. Brand lockup: `ISKRA`, `ІСКРА`, or both; light+dark themes are recommended.
