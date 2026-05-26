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
   1058 (Ukrainian). `installer/Bundle.wxs` builds the factory setup EXE
   with `WixToolset.BootstrapperApplications.wixext/5.0.2`.
   `installer/build-installer.ps1` runs `dotnet publish` (single-file,
   self-contained, win-x64), builds `installer/out/Iskra-<ver>-x64.msi`,
   downloads/caches Arm GNU Toolchain 15.2.rel1, verifies SHA-256, then
   builds `installer/out/Iskra-<ver>-setup-x64.exe`. The setup EXE chains
   the Arm toolchain MSI first (`EULA=1`) and then Iskra. Per-machine
   scope, installs to `C:\Program Files\Iskra\`, Start Menu shortcut,
   examples/catalog.json + .sig bundled.

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

### Sprint 3.5 — `firmware-catalog` repo + auto-discovery

Catalog production becomes automatic so new products / releases appear
without anyone editing `catalog.json` by hand. **Trust root stays
Ed25519-signed** — we automate the *production*, not the *trust*.

- New repo: `oleksandrmaslov/firmware-catalog` (public).
- GitHub Actions workflow listens for `release.published` webhooks across
  `*-firmware` repos under the account. On fire: reads the new release's
  `target.json` sidecar, regenerates `catalog.json`, signs it with the
  private key (GitHub Actions secret), publishes `catalog.json` +
  `catalog.json.sig` as the latest release of `firmware-catalog`.
- App polls `firmware-catalog`'s latest release on startup (anonymous;
  catalog repo is public), downloads if newer, verifies signature,
  hot-swaps. Operator sees "New firmware available for `ci-clop`
  (v1.0.1)" banner; batch lock prevents accidental mid-batch swap.
- Replaces ad-hoc manual signing for catalog releases. Sprint 3 work
  still needed first because firmware downloads need GitHub auth.

### Beyond Sprint 3.5

| Sprint | Deliverable |
|---|---|
| 2.5 | Sideload-from-folder (`--sideload-dir`) — synthesises a catalog from `<id>_v<ver>_<part>.elf` + sidecar files |
| 2.6 | Per-product flasher overrides — optional `frequency_hz` / `power_mode` / `connect_reset` / `timeout_s` in catalog `target` block; override global Settings at flash time |
| 5 | Cloud DB mirror — keep local SQLite writes (offline-safe), batch-push to a central DB (likely Postgres/Supabase) when network is up. Schema mirrors `flash_attempts` + adds `station_id` index |
| 6 | Auto-pick product by board ID — needs firmware cooperation (write a board-ID byte to a known flash offset OR use chip UID + a per-product mapping table). Reads via `monitor read_mem`; matches against catalog before flashing |

### Polish backlog (fold in opportunistically)

- **Two-phase gdb (scan-then-flash)** — today a target mismatch is detected
  AFTER `load` because both happen in one gdb invocation. For factory safety
  the scan should be a separate gdb call that aborts before touching flash if
  the target doesn't match.
- **Probe serial capture** — read USB serial from registry to populate
  `probe_serial` column (currently always NULL).
- **Auto-retry on `E_PROBE_BUSY`** — BMP occasionally fumbles a re-enumerate;
  one silent retry would smooth that out.
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
  exe). Firmware integrity = SHA-256 from the signed catalog.
- **GitHub auth:** GitHub App + Device Flow. Refresh token in Windows DPAPI,
  `LocalMachine` scope. No PATs.
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

A separate `firmware-catalog` repo (TBD) will hold the signed `catalog.json`
asset (an aggregation of per-product `target.json` files) and is the single
source of truth for what operators can flash.

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
