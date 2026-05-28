# CLAUDE.md — Iskra

Session handoff. Read this fully at the start of any new Claude session in this repo.

---

## Goal

Production Windows flashing tool for any ARM Cortex-M target supported by
Black Magic Probe (PY32, STM32, NXP, GD32, etc.). Drives the probe via
`arm-none-eabi-gdb`. Designed to mass-flash ~500 devices per batch from
non-developer factory operators. Firmware is fetched from private GitHub
repos; each release carries the target stack metadata (BMP target match
string + flash size + display part number) so the app can verify the right
firmware is paired with the right hardware. Every attempt is logged to SQLite.

**UI language: Ukrainian only** (operator-facing strings in WPF and CLI).
Log payloads, error codes (`E_*`), and developer-only diagnostics stay
ASCII / English.

The full architecture proposal lives in the firmware repo's prior session
transcript. This file is the *condensed* working copy of those decisions.

---

## Status snapshot (2026-05-26)

### Done

- Architecture proposal complete (catalog, GitHub auth, state machine, error
  codes, security, MVP plan, UI screens).
- Sibling repo cloned at `C:\Users\Alexandr\iskra\`.
- .NET 8 SDK installed **per-user** at
  `C:\Users\IMT - Teilnehmer\AppData\Local\Microsoft\dotnet\` (not on system PATH).
- Solution scaffolded — `Iskra.sln` with three projects, builds clean.
- CLI stub (`Iskra.Cli`) parses the planned option surface; does NOT
  flash yet.
- Initial commit `5e26828` pushed to `origin/main`.
- Empty `tests/Iskra.Core.Tests/` ready for content.

### Sprint 1 — done in code; bench acceptance is the only remaining gate

All Sprint 1 chunks landed: `GdbProcess`, `FlashStateMachine`, `SqliteLogStore`,
CLI wire-up, xUnit suite, BMP auto-detect, gdb auto-detect, ELF pre-flight,
`--dry-run`, `--list-probes`. **First real HIL pass confirmed on PY32F002Ax5
board with the lab BMP.** Parser regexes verified against verbatim BMP gdb
output (see `FlashStateMachineTests.Real_bmp_output_passes_classification`).

Open Sprint 1 item is bench-time only, not code:

- **50 PASS in a row** on the bench with one BMP + the ci-clop PY32 board.
  This is the production-safety acceptance test. Run when convenient; failures
  will surface parser gaps the fixture tests can't predict.

### Sprint 2 — Catalog + integrity + signature (chunks 1–4 done, chunk 5 deferred)

1. ✅ **Lock-in + catalog data model** — `Catalog` / `Product` /
   `FirmwareRelease` / `TargetDescriptor` records; `CatalogJson` parser with
   validation; real-BMP fixture test; CLAUDE.md `bmp_match` corrected to
   `"PY32Fxxx"`.
2. ✅ **CLI catalog resolution** — `--catalog <path> --product <id>` resolves
   `--target` / `--flash-kb` / `--firmware-version` / `--firmware-sha256`
   / `--elf` from the catalog. Explicit CLI args override.
3. ✅ **SHA-256 firmware integrity** — `FirmwareIntegrity` compares ELF
   SHA-256 against catalog entry before flashing; mismatch synthesises a
   `E_FW_HASH_MISMATCH` outcome, logs it, gdb never spawns. Exit 1.
4. ✅ **Ed25519 catalog signature** — BouncyCastle dep; two-file format
   (`catalog.json` + `catalog.json.sig`, base64); dev public key embedded
   in `CatalogTrust.EmbeddedPublicKeyBase64`. CLI flag
   `--require-signed-catalog` rejects unsigned / bad-signature catalogs.
5. ⏸️ **Sideload-from-folder** (deferred — minor convenience, easy to add
   later) — `--sideload-dir <path>` would scan a directory for
   `<product-id>_v<X.Y.Z>_<part-number>.elf` + `target.json` sidecars and
   synthesise an in-memory catalog. Use case: ad-hoc test ELFs before a
   release is added to the signed catalog.

### Dev catalog signing workflow

The dev keypair lives **outside** the repo at
`C:\Users\IMT - Teilnehmer\.claude\projects\c--Users-Alexandr-flashlight-app\keys\catalog-key.{pub,priv}`
(the path still uses the pre-rename `flashlight-app` slug — moving the
keys is a separate cleanup; embedded public key still matches).
The public key is embedded in
[`CatalogTrust.EmbeddedPublicKeyBase64`](src/Iskra.Core/CatalogTrust.cs).
The private key is **never** committed.

Re-sign `examples/catalog.json` after edits:

```powershell
$priv = "C:\Users\IMT - Teilnehmer\.claude\projects\c--Users-Alexandr-flashlight-app\keys\catalog-key.priv"
dotnet run --project src/Iskra.Cli -- --sign-catalog examples/catalog.json --private-key $priv
```

Rotate to a production key before factory deployment:

1. Generate new keypair on a secured workstation:
   `dotnet run --project src/Iskra.Cli -- --gen-keypair <secure-dir>`
2. Update `CatalogTrust.EmbeddedPublicKeyBase64` with the printed base64.
3. Re-sign the production `catalog.json` with the new private key.
4. Store the production private key in an HSM / vault; never on the lab box.

### Sprint 4 — WPF UI ✅ DONE

User picked **Sprint 4 as MVP first, then Sprint 3**.

1. ✅ **MVP single-screen WPF** — `src/Iskra.Wpf/`. Status strip,
   operator + batch + product form, giant PASS/FAIL banner, FLASH button,
   gdb output panel. Wires to Core.
2. ✅ **History + Settings tabs + persistence** — `MainWindow` is a
   `TabControl` with four tabs (Прошивка / Історія / Каталог / Налаштування).
   Settings persisted to `%LOCALAPPDATA%\Iskra\settings.json` via
   `AppSettings` + atomic `.tmp` rename. Flash logic reads frequency,
   power, connect-reset, timeout, gdb path, db path from settings.
3. ✅ **Catalog browser tab** — read-only view of the parsed catalog:
   each product as a card showing target descriptor (BMP match, part number,
   flash KB) and all releases (version, ELF filename, SHA-256, release
   date). Header shows trust status + count. "Перезавантажити" button.
4. ✅ **Batch locking** — first PASS/FAIL row in a batch determines the
   product+version lock. Subsequent flash attempts with a different
   product or version fail with `E_BATCH_LOCKED` (Ukrainian hint
   suggests changing the batch ID). Refused attempts are themselves
   excluded from lock determination via the SQL filter in
   `SqliteLogStore.GetBatchLock`. Live UI hint on the Flash tab shows
   the current lock state as the operator types the batch ID.
5. ✅ **CSV export** — `SqliteLogStore.ExportCsv` streams the full
   `flash_attempts` table (or one batch's slice) to a UTF-8 CSV.
   RFC 4180 escaping via `CsvWriter`. Two buttons on the History tab:
   "Експорт CSV (партія)" and "Експорт CSV (все)" + SaveFileDialog.
6. ✅ **WiX installer** — `installer/Product.wxs` builds the app MSI
   (WiX 5; UI extension pinned to 5.0.2). MSI codepage 1251 + language
   1058 (Ukrainian). The MSI has launch conditions for MSI-native x64
   Windows compatibility and existing `arm-none-eabi-gdb.exe`; use the setup
   EXE on fresh PCs.
   `installer/Bundle.wxs` builds the factory setup EXE with
   `WixToolset.BootstrapperApplications.wixext/5.0.2` and
   `WixToolset.Util.wixext/5.0.2` prerequisite searches.
   `installer/build-installer.ps1` runs `dotnet publish` (single-file,
   self-contained, win-x64), builds `installer/out/Iskra-<ver>-x64.msi`,
   downloads/caches Arm GNU Toolchain 15.2.rel1, verifies SHA-256, then
   builds `installer/out/Iskra-<ver>-setup-x64.exe`. The setup EXE checks
   Windows 10/11 x64, skips the bundled Arm MSI when supported GDB already
   exists, otherwise chains the Arm toolchain MSI first (`EULA=1`) and then
   Iskra. Per-machine
   scope, installs to `C:\Program Files\Iskra\`, Start Menu shortcut,
   examples/catalog.json + .sig bundled. Build also copies
   `installer/check-station.ps1` to
   `installer/out/Iskra-<ver>-preinstall-check.ps1` and installs the same
   script into `C:\Program Files\Iskra\`. `Iskra.Cli --doctor` is the
   post-install readiness check: gdb, BMP COM port, catalog JSON/signature,
   writable `%LOCALAPPDATA%\Iskra` / `%PROGRAMDATA%\Iskra`, and GitHub auth
   state.

### Sprint 3 — done in code; live `--login` against the registered GitHub App is the only remaining gate

All six chunks landed. `GitHubAppConfig.ClientId` is set to the real GitHub
App. Refresh-token rotation, DPAPI LocalMachine encryption, atomic cache
writes, and the "re-hash on every cache hit" policy mean a compromised
GitHub release cannot push bad firmware unless the attacker also forges
the Ed25519 catalog signature.

1. ✅ **Catalog schema bump for remote ELF refs** — `GitHubReleaseRef`
   record + optional `elf_source` on `FirmwareRelease`; `IsRemote` helper.
2. ✅ **GitHub Device Flow client** — [`Iskra.Core/GitHubAuth.cs`](src/Iskra.Core/GitHubAuth.cs).
   `RequestDeviceCodeAsync` / `PollForTokenAsync` / `RefreshTokenAsync`.
   Honors `slow_down` interval bump; surfaces `access_denied` /
   `expired_token` as typed `GitHubAuthException.ErrorCode`.
3. ✅ **DPAPI LocalMachine token store** — [`Iskra.Core/TokenStore.cs`](src/Iskra.Core/TokenStore.cs).
   `%PROGRAMDATA%\Iskra\auth.bin`; atomic `.tmp` → `Move`; corrupted blob
   throws `TokenStoreException` (caller deletes + re-auths).
4. ✅ **Firmware downloader + cache** — [`Iskra.Core/FirmwareCache.cs`](src/Iskra.Core/FirmwareCache.cs)
   at `%LOCALAPPDATA%\Iskra\firmware-cache\<owner>_<repo>\<tag>\<asset>`.
   Re-hashes on every cache hit (catches catalog SHA updates AND on-disk
   tampering). Hash mismatch on download deletes the tmp and throws.
   Composed with [`AccessTokenProvider`](src/Iskra.Core/AccessTokenProvider.cs)
   which loads stored tokens, refreshes-and-persists if stale (5 min
   skew), deletes the blob if refresh is rejected.
5. ✅ **CLI wiring** — `--login`, `--logout`, `--whoami` (the last calls
   `GET /user` to show the GitHub login). `Iskra.Cli.csproj` bumped to
   `net8.0-windows` since DPAPI is Windows-only. Exit code `5 = GitHub
   auth / firmware download error`. Resolver no longer blocks on remote
   releases — CLI calls `FirmwareCache.GetOrDownloadAsync` and injects
   `--elf <cache-path>` before `FlashOptions.Parse`.
6. ✅ **WPF auth UI** — Settings tab "Авторизація GitHub" section with
   status line, Увійти / Вийти / Оновити buttons. New [`DeviceFlowDialog`](src/Iskra.Wpf/DeviceFlowDialog.xaml)
   modal shows verification URL + huge user code + Copy / Open-in-browser
   helpers, polls in background, closes on success. Flash tab has a
   collapsed-by-default yellow hint banner that appears when the selected
   release is remote AND we're not signed in. `FlashButton_Click` calls
   `DownloadRemoteFirmwareAsync` before integrity check; maps
   `NotSignedInException` / `RefreshTokenExpiredException` /
   `GitHubAssetNotFoundException` / general download failure to new
   error codes `E_NOT_SIGNED_IN` / `E_AUTH_EXPIRED` / `E_ASSET_NOT_FOUND`
   / `E_FW_DOWNLOAD_FAILED` (each with Ukrainian hint in `ErrorHints.cs`).

Open Sprint 3 item is end-to-end only, not code:

- **Real `--login` against the registered GitHub App** — confirms the
  Client ID works, the consent screen renders, the token round-trips
  through DPAPI cleanly, and `--whoami` shows the right login.
- **First signed release of `ci-clop-firmware`** — once that release
  is cut, replace the placeholder `0000…0001` SHA-256 in
  [examples/catalog.json:21](examples/catalog.json#L21) with the real
  hash and re-sign the catalog. That unblocks a real remote-download
  flash on the bench.

### Sprint 3.5 — done in code; live end-to-end with the registered repos is the only remaining gate

Catalog production is now automatic. **Trust root stays Ed25519-signed** —
we automate the *production*, not the *trust*. Renamed the catalog repo
from the planned `firmware-catalog` to `iskra-catalog`, and switched the
trigger from polling to GitHub's `repository_dispatch` events fired by
each `*-firmware` repo on release publish.

1. ✅ **Catalog generator** — [`CatalogGenerator`](src/Iskra.Core/CatalogGenerator.cs)
   + [`TargetSidecar`](src/Iskra.Core/TargetSidecar.cs). Walks a tree of
   `target.json` sidecars, aggregates by `(product_id, version)`, derives
   `elf_filename` / `elf_source` from the firmware-side naming convention
   (`<product>_v<X.Y.Z>_<part>.elf` from `<owner>/<product>-firmware`).
   Picks `default_release` as the highest semver per product (prereleases
   rank below their non-prerelease counterpart). CLI flag:
   `Iskra.Cli --generate-catalog --from-targets <dir> --out <path> [--owner <owner>]`.
2. ✅ **GitHub Actions workflows** — staged in
   [.github/workflows-templates/](.github/workflows-templates/). Two YAMLs +
   a setup README. `notify-iskra-catalog.yml` goes into each `*-firmware`
   repo and POSTs `repository_dispatch` at iskra-catalog on every release
   publish. `regenerate-catalog.yml` lives in iskra-catalog, lists all
   `*-firmware` repos via `gh repo list`, downloads each release's
   `target.json`, runs `Iskra.Cli --generate-catalog`, signs with
   `CATALOG_PRIV_KEY` secret, publishes a `catalog-<timestamp>` release
   with `catalog.json` + `catalog.json.sig` assets.
3. ✅ **RemoteCatalogClient** — [`Iskra.Core/RemoteCatalogClient.cs`](src/Iskra.Core/RemoteCatalogClient.cs).
   Anonymous GET on `https://api.github.com/repos/<owner>/iskra-catalog/releases/latest`,
   downloads `catalog.json` + `.sig` assets via `browser_download_url`,
   verifies signature against the embedded public key, atomically commits
   to `%LOCALAPPDATA%\Iskra\catalog\latest.json` (+ `.sig` + `.tag`).
   Refuses to write the cache if anything fails (network / bad signature /
   parse error). Returns a `RemoteCatalogStatus` enum so callers can show
   precise UX per failure mode.
4. ✅ **Startup integration** — WPF `LoadCatalog` prepends the cached
   remote catalog path to the candidates list when `CatalogAutoUpdate`
   is on AND `CatalogPath` setting is empty. On every startup an
   `_ = BackgroundFetchCatalogAsync()` is fired off; if the tag changes,
   the status strip shows "Каталог: оновлено до catalog-XYZ — натисніть
   Перезавантажити". `AppSettings` gained `CatalogAutoUpdate` (default
   true), `CatalogOwner` (default `oleksandrmaslov`), `CatalogRepo`
   (default `iskra-catalog`).
5. ✅ **WPF "Перевірити оновлення"** — Settings tab gained a "Каталог
   GitHub (авто-оновлення)" section: checkbox + owner/repo text fields +
   current cached tag/status + a button that calls `FetchAsync` and maps
   each `RemoteCatalogStatus` to a Ukrainian status line (green for
   `Updated` / `AlreadyUpToDate`, amber for `NoRelease`, red for
   `BadSignature` / `AssetsMissing` / `ParseError`).
6. ✅ **CLI bundled into MSI** — `installer/Product.wxs` ships both
   `Iskra.exe` (WPF) and `Iskra.Cli.exe` to `C:\Program Files\Iskra\`.
   `installer/build-installer.ps1` publishes both as single-file
   self-contained exes before the MSI build.

Open Sprint 3.5 items are deployment-only, not code:

- **Create the `oleksandrmaslov/iskra-catalog` public repo.**
- **Add `CATALOG_PRIV_KEY` secret to iskra-catalog** with the base64 of
  the existing dev `catalog-key.priv`.
- **Drop `regenerate-catalog.yml` into iskra-catalog** (from
  [.github/workflows-templates/](.github/workflows-templates/)).
- **Per firmware repo:** drop in `notify-iskra-catalog.yml` and set the
  `ISKRA_CATALOG_DISPATCH_TOKEN` PAT secret.
- **First end-to-end run:** cut a real release in `ci-clop-firmware` and
  watch iskra-catalog Actions regenerate the signed catalog within ~30 s.

### Sprint 6 — planned: production release governance + anti-tamper hardening

Current code already has the core trust boundary: Ed25519-signed catalogs,
embedded public key verification, SHA-256 firmware checks, GitHub release
downloads, and cache re-hashing. The remaining risk is production governance:
today WPF settings still expose editable `CatalogOwner` / `CatalogRepo`
fields for development convenience. In production, stations must not trust an
operator-entered GitHub username/repo, and they must not require Oleksandr to
sign in to his personal GitHub account on each factory PC.

Planned Sprint 6 deliverables:

1. **Production catalog lock / allowlist** — add a production-mode policy that
   accepts only the official catalog source, initially
   `oleksandrmaslov/iskra-catalog`. The WPF owner/repo text fields stay
   available only in dev/lab mode; release builds either hide or disable them.
   `RemoteCatalogClient` should reject non-allowlisted catalog sources before
   any network request.
2. **Production signing-key custody** — rotate from the dev key to a
   production Ed25519 keypair. Embed only the production public key in the
   release app. Keep the private key outside this repo and outside normal
   factory PCs: preferred storage is offline media, HSM, or a tightly
   controlled vault. GitHub Actions may build draft catalogs, but only
   Oleksandr or a protected approval environment can produce the production
   signature.
3. **No maintainer GitHub login on stations** — factory PCs must not use
   Oleksandr's personal GitHub login or PAT. Preferred deployment is public
   read-only catalog/firmware assets with trust enforced by signature +
   SHA-256. If private assets are required, use a read-only GitHub App,
   machine/service account, or backend download proxy — never the maintainer's
   personal credentials.
4. **Anti-rollback catalog policy** — extend the catalog/cache metadata with a
   monotonic catalog sequence or signed `published_at`/epoch field. Store the
   newest accepted value locally and reject older signed catalogs by default.
   Provide a deliberate engineering override for lab recovery only.
5. **Release revocation** — add a signed `revoked_releases` /
   `disabled_releases` section to the catalog so a bad firmware version can be
   blocked even if it was previously signed and cached.
6. **GitHub repository governance** — protect `main` on app, catalog, and
   firmware repos: no force-push, required PR, required status checks, signed
   commits/tags where practical, CODEOWNERS with Oleksandr as required owner,
   GitHub Actions minimum permissions, and protected environments/manual
   approval for signing or publishing production catalog releases.
7. **Security tests** — add tests for wrong catalog repo, unsigned catalog,
   bad signature, hash mismatch, rollback attempt, revoked release, tampered
   cache file, and production-mode owner/repo override attempts.
8. **Operational recovery** — document key rotation and incident response:
   if GitHub is compromised, re-publish a clean signed catalog; if the signing
   key is compromised, rotate the embedded public key and ship a new app
   version. A compromised GitHub release alone must not be enough to make a
   station flash bad firmware.

### Beyond Sprint 3.5

| Sprint | Deliverable |
|---|---|
| 2.5 | Sideload-from-folder (`--sideload-dir`) — synthesises a catalog from `<id>_v<ver>_<part>.elf` + sidecar files |
| 2.6 | Per-product flasher overrides — optional `frequency_hz` / `power_mode` / `connect_reset` / `timeout_s` in catalog `target` block; override global Settings at flash time |
| 5 | Cloud DB mirror — keep local SQLite writes (offline-safe), batch-push to a central DB (likely Postgres/Supabase) when network is up. Schema mirrors `flash_attempts` + adds `station_id` index |
| 6 | Production release governance + anti-tamper hardening — lock catalog source, protect signing keys, no maintainer GitHub login on stations, anti-rollback, release revocation, repo protections |
| 7 | Auto-pick product by board ID — needs firmware cooperation (write a board-ID byte to a known flash offset OR use chip UID + a per-product mapping table). Reads via `monitor read_mem`; matches against catalog before flashing |

### Polish backlog (fold in opportunistically)

- ✅ **Two-phase gdb (scan-then-flash)** — `FlashStateMachine.RunAsync` now
  runs `gdb.RunScanAsync` (preamble → `swdp_scan` → quit; no ELF, no
  `attach`, no `load`, no `compare-sections`) first; on probe error, no
  targets, or `E_TARGET_MISMATCH` the flash phase is never spawned.
  Only a clean scan proceeds to the canonical attach/load/verify
  invocation. Scan phase has its own timeout (min(8s, total) ceiling).
  Duration reported to logs is scan+flash wall-clock. Bench acceptance
  (50-PASS row) still needs to be re-run since the gdb sequence changed.
- **Probe serial capture** — read USB serial from registry to populate
  `probe_serial` column (currently always NULL).
- ✅ **Auto-retry on `E_PROBE_BUSY`** — `FlashStateMachine.RunAsync` retries
  the scan phase once (configurable via `probeBusyRetries`, default 1)
  with a 500 ms backoff when scan classification yields `E_PROBE_BUSY`.
  Only `E_PROBE_BUSY` triggers retry — every other failure bails immediately.
- ✅ **Strict tag/version match in catalog generator** —
  `CatalogGenerator.ReadTargetsTree(rootDir, strictTagMatch: true)` and CLI
  flag `--strict-tag-match` refuse to build when a release's tag directory
  disagrees with the sidecar's `version` field. The iskra-catalog workflow
  template passes this flag so stale assets (e.g. v1.0.1 release carrying
  leftover v1.0.0 ELF + sidecar) fail loudly in CI instead of silently
  shadowing the release in the published catalog.
- ✅ **Configurable flash hotkey** — `AppSettings.FlashHotkey` enum
  (`None`/`Space`/`Enter`/`F2`/`F5`, default `Enter`). Window-level
  `PreviewKeyDown` fires the FLASH button when the Flash tab is active
  and the button isn't mid-flash. Space is suppressed while a TextBox has
  focus (so the operator can still type spaces); Enter and F-keys are
  captured everywhere (which is also what barcode scanners want — the
  scanner emits Enter as line terminator so "scan batch → flash" is one
  swipe). The FLASH button has a tooltip and a smaller subtitle line
  reflecting the configured key.
- **`.hex` firmware support** — alongside ELF. Two viable paths: (a) let gdb
  load Intel HEX directly via its BFD support (works for some targets but
  flaky for bare-metal where ELF entry/section info is needed), or (b)
  convert hex → elf at flash time via `arm-none-eabi-objcopy -I ihex -O
  elf32-little`. Catalog schema would gain a `firmware_kind: "elf" | "hex"`
  field per release; SHA-256 is computed over the on-disk firmware bytes
  regardless of kind.

---

## Design decisions locked in (don't relitigate without reason)

- **Stack:** .NET 8 + WPF, single-file self-contained `.exe`. (User said "use
  any language" but committed to .NET — switching now costs days.)
- **gdb shipping:** chained full ARM toolchain MSI inside our installer
  (NOT vendoring just `gdb.exe`). App detects existing install at the
  standard ARM path; prompts to re-run installer if missing.
- **Target support:** any MCU that Black Magic Probe's `swdp_scan` recognises.
  Code in `Iskra.Core` MUST stay target-agnostic — no PY32 strings
  in the state machine, gdb wrapper, or log writer. The catalog entry per
  product declares the target stack; the app verifies a match at flash time.
- **Catalog target descriptor** (per product entry, Sprint 2):
  - `bmp_match` — substring expected in `monitor swdp_scan` output. BMP
    reports at **family** granularity, not part number — e.g. all PY32F0xx
    chips come back as `"PY32Fxxx M0+"`, so use `"PY32Fxxx"`. Other examples:
    `"STM32F103"`, `"STM32F4"`, `"GD32F30"`. Match is case-insensitive
    substring. Used by the state machine to fail fast with `E_TARGET_MISMATCH`
    if a wrong-family board is plugged in.
  - `flash_kb` — flash size in KB. Drives the timeout budget and a
    sanity check against the elf section sizes.
  - `part_number` — display string shown to operators (`"PY32F002Ax5"`).
    NOT used for verification — BMP can't tell variants apart within a family.
- **UI language:** Ukrainian only. No i18n framework; strings hardcoded in
  WPF and CLI. Error codes (`E_*`) stay English / ASCII; the UI maps each
  to a Ukrainian hint line.
- **MVP bench target:** `ci-clop` product, PY32F002Ax5 board. This is
  the *acceptance test* for Sprint 1, NOT a hardcoded assumption in code.
- **Operator identity:** free-text dropdown at app start, stored per-station.
- **Trust root:** signed `catalog.json` (Ed25519, public key embedded in
  exe). Firmware integrity = SHA-256 from the signed catalog. GitHub owner,
  repo name, release tag, and asset name are metadata, not trust roots.
  Production builds should lock/allowlist the catalog source and rely on the
  embedded public key to decide what can be flashed.
- **GitHub auth:** GitHub App + Device Flow. Refresh token in Windows DPAPI,
  `LocalMachine` scope. No PATs. Factory stations must not use Oleksandr's
  personal GitHub login; prefer public read-only release assets plus catalog
  signature/SHA-256, or a read-only GitHub App/service path if private access
  is required.
- **Logging:** SQLite per station; CSV export per batch.
- **Production safety:** batches lock the firmware version. The app NEVER
  auto-updates firmware silently during a batch.
- **Hardware-in-the-loop testing** is available — use it from day one of
  Sprint 1 implementation. Don't build to a mock.

### Exact gdb invocation the state machine must reproduce

Sourced verbatim from the firmware repo's `rules.mk:135-137`:

```
arm-none-eabi-gdb.exe -nx --batch
  -ex "set confirm off"
  -ex "set pagination off"
  -ex "target extended-remote \\.\COM30"
  [-ex "monitor tpwr enable"]            # when --power probe
  -ex "monitor frequency 1000000"
  [-ex "monitor connect_rst enable"]     # when --connect-reset
  -ex "monitor swdp_scan"
  -ex "attach 1"
  -ex "load"
  -ex "compare-sections"
  -ex "kill"
  -ex "quit"
  "<path-to-elf>"
```

Wall-clock timeout: scaled to flash size, floor 15 s. For Sprint 1 use a
flat 15 s (PY32F002A flash is 32 KB; anything slower means BMP/USB trouble).
For larger MCUs the budget becomes `max(15s, 5s + flash_kb * 0.4s)` —
revisit during Sprint 2 once the catalog `flash_kb` field is wired.
Failure code on timeout: `E_TIMEOUT`.

### SQLite schema

```sql
CREATE TABLE flash_attempts (
  id              INTEGER PRIMARY KEY AUTOINCREMENT,
  ts_utc          TEXT    NOT NULL,
  operator        TEXT    NOT NULL,
  station_id      TEXT    NOT NULL,
  batch_id        TEXT    NOT NULL,
  product_id      TEXT    NOT NULL,
  firmware_version TEXT   NOT NULL,
  firmware_sha256 TEXT    NOT NULL,
  target_bmp_match TEXT   NOT NULL,   -- expected from catalog (e.g. "PY32F002A")
  target_detected TEXT,                -- raw swdp_scan match line we picked
  target_flash_kb INTEGER NOT NULL,    -- from catalog
  com_port        TEXT    NOT NULL,
  probe_serial    TEXT,
  power_mode      TEXT    NOT NULL,
  connect_rst     INTEGER NOT NULL,
  bmp_frequency_hz INTEGER NOT NULL,
  result          TEXT    NOT NULL,
  error_code      TEXT,
  error_message   TEXT,
  duration_ms     INTEGER NOT NULL,
  gdb_tail        TEXT
);
```

### Error code taxonomy

`E_PROBE_NOT_FOUND`, `E_PROBE_BUSY`, `E_SCAN_NO_TARGET`, `E_TARGET_MISMATCH`,
`E_ATTACH_FAILED`, `E_LOAD_FAILED`, `E_VERIFY_MISMATCH`, `E_TIMEOUT`,
`E_GDB_CRASHED`, `E_FW_HASH_MISMATCH`. Each maps to a one-line **Ukrainian**
operator hint in the UI (table lives in `Iskra.Core/ErrorHints.cs`
once written).

---

## Repository layout

```
Iskra.sln
src/
  Iskra.Core/        Class library — services, state machine, models
    FlashOptions.cs
  Iskra.Cli/         Console flasher (Sprint 1)
    Program.cs               (current stub: arg parsing only)
tests/
  Iskra.Core.Tests/  xUnit (empty)
```

WPF project lands in Sprint 4 as `src/Iskra.Wpf/`.

---

## Environment quirks (this lab machine)

- Windows user is `IMT - Teilnehmer`, but project files live under
  `C:\Users\Alexandr\` — two different profiles on one box.
- `dotnet` is **not** on the system PATH. At the start of any PowerShell
  tool call that needs `dotnet`, prepend:
  ```powershell
  $env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
  ```
- Git config locally has `user.email = oleksandr.tidal@gmail.com`. The
  intended GitHub author email is `oleksandrmaslov08@gmail.com`. Either:
  - one-time fix: `git config user.email "oleksandrmaslov08@gmail.com"`
  - or pass `--author="Oleksandr Maslov <oleksandrmaslov08@gmail.com>"` per commit.
- No `winget` available.
- Firmware repo is sibling: `c:\Users\Alexandr\py32f0-template-project\`.
- BMP + target board: available for hardware-in-the-loop from any session
  that needs to do real flashing (confirm with the user at session start).

---

## Build / run / test

```powershell
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
dotnet build
dotnet test
dotnet run --project src/Iskra.Cli -- --help
```

The current CLI stub accepts the full Sprint 1 option surface and just
echoes parsed values — it does not invoke gdb yet.

---

## Coordination with the firmware repo

The app flashes firmware released from any product repo (`ci-clop-firmware`
is the first) via GitHub Releases. Release asset naming convention:

```
<product-id>_v<X.Y.Z>_<part-number>.elf
<product-id>_v<X.Y.Z>_<part-number>.elf.sha256
target.json                              # one per release
```

Examples:
- `ci-clop_v1.0.0_PY32F002Ax5.elf`
- `headlamp_v2.1.0_STM32F103C8.elf`

`target.json` (uploaded as a release asset) declares the target stack so the
catalog generator and the app can verify firmware ↔ hardware pairing:

```json
{
  "product_id":   "ci-clop",
  "version":      "1.0.0",
  "part_number":  "PY32F002Ax5",
  "bmp_match":    "PY32F002A",
  "flash_kb":     32,
  "elf_sha256":   "<hex>"
}
```

If you change the firmware build artefact, rename releases, or omit
`target.json`, the catalog parser here breaks. Coordinate before renaming.

The production catalog repo is planned as `oleksandrmaslov/iskra-catalog`.
It holds the signed `catalog.json` asset (an aggregation of per-product
`target.json` files) and is the single source of truth for what operators can
flash. Production trust still comes from the Ed25519 signature, not from the
repo name alone.

---

## How to be a useful assistant on this codebase

### What's been working — keep doing

- **Recommend one option clearly.** Don't just list trade-offs. The user
  wants direction; they will push back if they disagree.
- **Show trade-off tables** when there are 3+ options. Compact and scannable.
- **Verify before claiming done** — build, run, parse output. Not "should work."
- **Ask before visible/destructive actions:** pushing to GitHub, installing
  toolchains, force operations. Local edits and builds can proceed freely.
- **Batch decisions** via `AskUserQuestion` (2–4 questions in one tool call).
- **Save quirks to `~/.claude/memory`** when discovered (this machine's PATH,
  user email mismatch, lab-vs-personal profile split — all caught here, all
  worth remembering).

### What to do less of

- Don't pre-write 1000-word design docs without being asked. Concise
  architecture + sprint plan beats exhaustive proposal.
- Don't pad with "let me summarise what we just did." The diff is the summary.
- Don't pile on "any more questions?" rounds. Ask only what's blocking.
- Don't skip `Read` before `Write` on files generated by `dotnet new` (or any
  template that creates a starter file). It will error and waste a turn.
- TodoWrite for short linear setup sequences is overkill; for the multi-day
  Sprint 1 implementation, use it.

### User profile (build context, not gossip)

- Owns this app repo (`oleksandrmaslov/iskra`) and the firmware repo
  (`oleksandrmaslov/ci-clop-firmware`).
- Direct, action-oriented; prefers to be shown options and pick quickly.
- Comfortable in C# / .NET though primary domain is embedded C.
- Building for non-developer factory operators — keep that audience in mind
  for UX choices (giant PASS/FAIL band, single primary action, etc.).
- Comfortable letting the assistant proceed once a decision is made; doesn't
  need permission to re-ask.

---

## Memory file (firmware repo)

The firmware-side Claude has a project memory at
`~/.claude/projects/c--Users-Alexandr-py32f0-template-project/memory/iskra_project.md`
pointing at this repo. Keep both in sync: if you change a decision here,
update that file too.
