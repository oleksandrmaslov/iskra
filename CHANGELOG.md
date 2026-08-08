# Changelog

All notable changes to Iskra are documented here.

## [Unreleased]

### Added

- Added firmware load-address validation. `FirmwareImage` reads the real load
  map from an ELF's PT_LOAD program headers (physical address and file size, the
  pair `gdb load` actually writes) or from Intel HEX data records including
  extended segment and linear addressing. `FirmwareRangeCheck` then refuses any
  image that cannot belong to the catalog-declared target: `E_FW_TOO_LARGE` when
  it exceeds `flash_kb`, `E_FW_ADDRESS_RANGE` when a segment falls outside a
  declared memory window. This closes a named production blocker — BMP's
  `bmp_match` identifies only an MCU family, so a build for a larger sibling
  part or a different memory map previously reached gdb looking valid.
- Added optional `flash_origin`, `ram_origin`, and `ram_kb` to the catalog target
  descriptor and the `target.json` sidecar, accepted as hex strings or numbers.
  Catalogs signed before this field existed remain valid and fall back to
  size-only checking. Corresponding `--flash-origin`, `--ram-origin`, and
  `--ram-kb` CLI flags, populated automatically in catalog mode.
- Added `tests/Iskra.Desktop.Tests`, an Avalonia headless view-model suite
  covering flash gating, banner state, catalog selection, settings validation
  and auto-save, hotkey mapping, and language switching.
- Added `AuthWorkflow` and `CloudLogWorkflow` to `Iskra.Application`, completing
  the Sprint 8.0 extraction. Credential classification and cloud log
  status/shipping are now defined once and rendered by WPF, Avalonia, and
  `--doctor`, instead of each carrying its own copy.
- Added per-release detail to the Avalonia catalog browser: default, GitHub, and
  revoked badges, firmware kind, release date, artefact name, full SHA-256, and
  the revocation reason. Added the startup background catalog fetch, which
  raises a reload notice rather than swapping the catalog under a station that
  may be mid-batch.
- Extended `Iskra.Cli --doctor` with the runtime identifier and framework, GDB
  provenance from the toolchain's own `--version` banner, shared credential
  classification, and cloud-log readiness including the pending row count.
- Added an `.editorconfig` describing the house style. Every rule is at
  `suggestion` severity and no format gate was added: the repository still has
  pre-existing drift, so a formatting sweep must land first. `CA1416` is kept at
  warning because it is what stops a Windows-only credential call from silently
  shipping in the portable CLI or the cross-platform frontend.
- Added a dedicated Iskra Avalonia installer: `installer/Product.Avalonia.wxs`,
  `installer/Bundle.Avalonia.wxs`, and `installer/build-avalonia-installer.ps1`
  produce a setup EXE that carries the same SHA-256-pinned Arm GNU Toolchain as
  the WPF setup, so a bare station needs no other download. It installs
  **beside** the production WPF station, never over it: distinct UpgradeCode,
  distinct ProductCode, its own `C:\Program Files\Iskra Avalonia\` folder, its
  own Start Menu entry, and its own registry key path. The shared Arm toolchain
  is marked permanent by both bundles, so removing either product never strips
  the compiler from a station still flashing production.
- Added `installer/arm-toolchain.pins.ps1` as the single source of truth for the
  bundled toolchain version, filename, URL, and SHA-256. Both installer builders
  dot-source it, so the two setup EXEs cannot ship different compilers under the
  same claim.
- Added a full-screen mode to the Avalonia app: a header toggle, F11, and Escape
  to leave. Full screen is the intended factory-floor mode — only the PASS/FAIL
  band and the FLASH button, with no desktop behind them.

### Changed

- Reduced the Avalonia default window to 1040x720 with a 760x520 minimum, and
  clamped it on open to the working area of the screen it actually lands on. The
  previous 1120x820 default pushed its own title bar off the top of a 1080p
  screen at 125% scaling, leaving the window impossible to move or resize.
- Gave buttons on the dark header and status strip an explicit light-on-dark
  style. The BMP "Check again" button was rendering Fluent's dark text on the
  dark strip and was effectively unreadable.

### Fixed

- Fixed a dispatcher-queued progress report being able to repaint over the final
  PASS/FAIL band. `Progress<T>` posts to the UI thread, so a late "Flashing…"
  could overwrite the verdict an operator relies on. Fixed in both Avalonia and
  WPF; found by the new headless tests.

### Notes

- **Sprint 6.5 (cross-station batch lock) deferred by owner decision.** Batch
  mode is off in production, so cross-station locking is not a blocker and
  building it now would mean guessing at factory network topology. The gap, the
  trigger to build it, and the decided fail-closed policy are recorded in
  `ROADMAP.md` so the eventual implementer does not have to re-litigate them.

## [2.0.0] - 2026-08-07

Iskra 2.0.0 turns the Avalonia frontend from a read-only preview into a working
cross-platform operator application, and gives the Windows installer its real
branding. WPF remains the supported Windows station application; Avalonia does
not replace it until it has hardware-in-the-loop acceptance.

### Added

- Added real flashing to the Avalonia Flash tab. It executes the shared
  `FlashWorkflow` — the same transaction WPF uses — so catalog and revocation
  gates, batch reservation, local or remote firmware acquisition, preflight and
  SHA-256 verification, two-phase GDB execution, and durable SQLite logging are
  not reimplemented in the frontend.
- Added the operator-facing Flash surface to Avalonia: a giant PASS/FAIL band in
  the same palette WPF uses, a full-width FLASH button honoring
  `AppSettings.FlashHotkey` (`None`/`Space`/`Enter`/`F2`/`F5`) window-wide, and a
  live dark gdb console that streams output and auto-scrolls. Space is
  suppressed while an editable text box has focus so operators can still type.
- Added an editable Avalonia Settings tab covering catalog path and signature
  enforcement, catalog auto-update, gdb path, SWD frequency, power mode, connect
  under reset, timeout, log path, station ID, batch mode, flash hotkey, and the
  cloud-log toggle, interval, and key path. All values round-trip through the
  shared `SettingsWorkflow`, so validation and atomic persistence are identical
  to WPF. Settings auto-save when leaving the tab or closing the window.
- Added a GitHub Device Flow window to Avalonia with verification URL, large
  user code, copy and open-in-browser helpers, and background polling, plus
  sign-in/sign-out/refresh and live token status on the Settings tab.
- Added catalog update checks, application update checks, and manual cloud-log
  upload to the Avalonia Settings tab, and a cloud-sync indicator to the status
  strip.
- Added CSV export (all rows and current batch) and a batch pass/fail summary to
  the Avalonia History tab, backed by the shared `HistoryWorkflow`.
- Added Windows installer branding: `installer/assets/banner.bmp` (493x58) and
  `dialog.bmp` (493x312) bound through `WixUIBannerBmp` / `WixUIDialogBmp`,
  `iskra.ico` for `ARPPRODUCTICON`, the Start Menu shortcut, and the Burn bundle
  icon, and a 64x64 `logo.png` as the bootstrapper logo. Windows Installer
  bitmap controls render DIB/BMP only, so the PNG sources in `docs/` are
  converted rather than embedded directly.

### Changed

- Renamed the Avalonia bundle output from `Iskra.Avalonia.Alpha.exe` to
  `Iskra.Avalonia.exe` and dropped the `-alpha.1` version suffix; it now carries
  the same version as WPF and the CLI. The bundle manifest states that flashing
  is enabled and that hardware-in-the-loop acceptance is still outstanding.
- Replaced the Avalonia alpha badge and its "flashing disabled" copy with a
  "HIL acceptance pending" badge, and updated the Ukrainian, English, and German
  readiness and history text that claimed the frontend was read-only.

### Security

- The Avalonia remote-firmware adapter reuses the Windows DPAPI token store and
  fails closed on Linux and macOS, where no encrypted `ITokenStore` exists yet.
  No plaintext token fallback was added. Local and sideload catalogs continue to
  work on every platform.
- The Windows MSI and setup EXE still install only `Iskra.exe` and
  `Iskra.Cli.exe`. `Iskra.Avalonia.exe` ships in the standalone zip only, so a
  frontend without a bench HIL pass is not placed on factory stations.

### Notes

- This release does not close the outstanding acceptance gates: the 50-PASS
  bench run, hardware-in-the-loop acceptance of the Avalonia frontend, a live
  `--login` against the registered GitHub App, the first signed
  `ci-clop-firmware` release, production catalog signing-key rotation, and the
  GitHub repository governance settings all remain owner actions. The audit
  classification stays lab-ready, not factory-production-ready.

## [1.4.0] - 2026-07-17

Iskra 1.4.0 begins the Sprint 8 cross-platform work while keeping WPF as the
supported Windows operator application. It introduces a shared application
layer, Ukrainian/English/German presentation across all operator surfaces, and
an explicitly limited Avalonia alpha beside the established WPF application.

### Added

- Added `Iskra.Application`, a UI-neutral layer for fail-closed catalog
  selection, exactly-one-probe readiness, optional batch policy, flash
  transactions, read-only history/export, settings validation, and atomic
  settings persistence.
- Added the shared `FlashWorkflow`, covering catalog and revocation gates,
  batch reservation, local or remote firmware acquisition, preflight and
  SHA-256 verification, two-phase GDB execution, and durable attempt logging.
- Added `HistoryWorkflow`, `SettingsWorkflow`, and shared application-path
  policy so WPF and Avalonia can reuse tested behavior without depending on a
  UI toolkit.
- Added `Iskra.Desktop`, a .NET 10/Avalonia 12.1.0 desktop alpha with the four
  operator tabs, station readiness, catalog status, and real read-only recent
  history. Its title and header identify it as an alpha, and flashing is
  disabled.
- Added persisted Ukrainian (`uk`), English (`en`), and German (`de`)
  presentation to WPF and Avalonia, plus `--lang uk|en|de` for the CLI.
  Technical diagnostics, hashes, logs, error codes, CLI flags, and raw GDB
  output remain language-neutral.
- Added initial portable infrastructure: a UI-neutral token-store interface,
  generic .NET CLI target, Unix GDB endpoints, Linux sysfs probe discovery, and
  fail-closed diagnostics where a secure non-Windows credential adapter is not
  yet available.
- Added committed NuGet lock files and repository-wide locked-restore support.
- Added a Windows executable-bundle builder that produces:
  - `Iskra.exe` 1.4.0 — supported WPF application.
  - `Iskra.Cli.exe` 1.4.0.
  - `Iskra.Avalonia.Alpha.exe` 1.4.0-alpha.1 — read-only alpha.
  - Bundle metadata, SHA-256 manifests, and a ZIP archive.
- Added focused Application-layer tests for catalog sessions, readiness, batch
  policy, localization, flash workflow, history, and settings.

### Changed

- Retargeted the repository to .NET 10 and pinned SDK `10.0.301` in
  `global.json`.
- Rewired the supported WPF flash path to the shared `FlashWorkflow` while
  retaining the Windows DPAPI/GitHub firmware adapter and existing operator
  behavior.
- Rewired WPF history, CSV export, batch-lock lookup, and settings persistence
  to the shared Application services.
- WPF settings now save automatically when leaving the Settings tab or closing
  the window. Dirty, saved, and save-error states are visible to the operator.
- Probe discovery now has an explicit refresh action. Zero or multiple Black
  Magic Probes block flashing; exactly one probe is required.
- Batch mode is now disabled by default and must be explicitly enabled. When
  disabled, stale or hidden batch input is ignored, attempt rows use a blank
  batch ID, and no batch reservation is created.
- Batch reservations now bind the complete product, version, firmware digest,
  and target identity instead of relying on a partial identity.
- The CLI now uses shared localized operator messages while retaining stable
  English flags, error codes, and technical output.
- Hardened catalog workflow templates with explicit firmware-repository
  approval, independent release-byte hashing, immutable published versions,
  pinned Action/revision references, and reviewer-gated signing.
- Hardened the WiX release path around locked restores. The full setup EXE
  installs the supported WPF app and CLI and embeds the SHA-256-pinned Arm GNU
  Toolchain 15.2.rel1 prerequisite when a supported GDB is not already present.

### Security

- Signed catalogs remain required by default. Unsigned catalog or manual
  flashing paths now additionally require the explicit lab-only
  `ISKRA_LAB_ALLOW_UNSIGNED_CATALOG=1` environment gate.
- Added strict response and cached-file size limits for remote catalog
  metadata, catalog bodies, signatures, and tags.
- Hardened remote-catalog rollback handling and fail-closed cache reads.
- Hardened firmware-cache path validation, temporary-file staging, hash
  verification, and cancellation cleanup.
- Hardened GDB launch, bounded diagnostic capture, cancellation, verification,
  and full process-tree termination.
- Added atomic, full-digest SQLite batch reservations and migration coverage.

### Fixed

- Prevented the Avalonia alpha language selector from overwriting newer WPF
  settings by reloading the latest settings immediately before its narrow,
  atomic language update.
- Read-only history inspection no longer creates a missing SQLite database.
- Safety-refused and revoked attempts no longer establish a batch reservation.
- Settings validation and normalization are now consistent across frontends,
  including positive numeric fields, official catalog-source locking, and
  clearing stale batch values when batches are disabled.

### Removed

- Removed the retired `design_assets/` pack. No placeholder branding was
  substituted; replacement assets must satisfy
  `docs/BRANDING_ASSET_REQUIREMENTS.md` and receive owner approval.

### Upgrade notes

- Source builds now require the repository-pinned .NET SDK `10.0.301`.
- WPF remains the supported Windows variant. Avalonia 1.4.0-alpha.1 is a
  read-only engineering preview and must not be used for production flashing.
- Existing and fresh settings retain Ukrainian as the compatibility default.
- Operators who require batch locking must explicitly enable batch mode after
  upgrading.
- Use the setup EXE for a new Windows station; the app-only MSI expects a
  supported `arm-none-eabi-gdb.exe` to already be installed.

### Validation and remaining gates

- Locked restore, Release build with warnings treated as errors, all 456 tests,
  checksum verification, three-language CLI smokes, and WPF/Avalonia startup
  smokes passed for the Windows engineering artifacts.
- This release remains lab-ready rather than factory-production-ready. The
  renewed 50-consecutive-PASS hardware run, production key custody, repository
  governance, trusted board identity, firmware address-range validation,
  append-only per-station logging, code signing, and final Sprint 9 acceptance
  remain open.

[Unreleased]: https://github.com/oleksandrmaslov/iskra/compare/v1.4.0...HEAD
[1.4.0]: https://github.com/oleksandrmaslov/iskra/compare/v1.3.0...v1.4.0
