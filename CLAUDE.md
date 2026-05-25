# CLAUDE.md — FlashlightApp

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

## Status snapshot (2026-05-25)

### Done

- Architecture proposal complete (catalog, GitHub auth, state machine, error
  codes, security, MVP plan, UI screens).
- Sibling repo cloned at `C:\Users\Alexandr\flashlight_app\`.
- .NET 8 SDK installed **per-user** at
  `C:\Users\IMT - Teilnehmer\AppData\Local\Microsoft\dotnet\` (not on system PATH).
- Solution scaffolded — `FlashlightApp.sln` with three projects, builds clean.
- CLI stub (`FlashlightApp.Cli`) parses the planned option surface; does NOT
  flash yet.
- Initial commit `5e26828` pushed to `origin/main`.
- Empty `tests/FlashlightApp.Core.Tests/` ready for content.

### Sprint 1 — done in code; bench acceptance is the only remaining gate

All Sprint 1 chunks landed: `GdbProcess`, `FlashStateMachine`, `SqliteLogStore`,
CLI wire-up, xUnit suite, BMP auto-detect, gdb auto-detect, ELF pre-flight,
`--dry-run`, `--list-probes`. **First real HIL pass confirmed on PY32F002Ax5
board with the lab BMP.** Parser regexes verified against verbatim BMP gdb
output (see `FlashStateMachineTests.Real_bmp_output_passes_classification`).

Open Sprint 1 item is bench-time only, not code:

- **50 PASS in a row** on the bench with one BMP + the pocket-light PY32 board.
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
`~/.claude/projects/c--Users-Alexandr-flashlight-app/keys/catalog-key.{pub,priv}`.
The public key matching it is embedded in
[`CatalogTrust.EmbeddedPublicKeyBase64`](src/FlashlightApp.Core/CatalogTrust.cs).
The private key is **never** committed.

Re-sign `examples/catalog.json` after edits:

```powershell
$priv = "C:\Users\IMT - Teilnehmer\.claude\projects\c--Users-Alexandr-flashlight-app\keys\catalog-key.priv"
dotnet run --project src/FlashlightApp.Cli -- --sign-catalog examples/catalog.json --private-key $priv
```

Rotate to a production key before factory deployment:

1. Generate new keypair on a secured workstation:
   `dotnet run --project src/FlashlightApp.Cli -- --gen-keypair <secure-dir>`
2. Update `CatalogTrust.EmbeddedPublicKeyBase64` with the printed base64.
3. Re-sign the production `catalog.json` with the new private key.
4. Store the production private key in an HSM / vault; never on the lab box.

### Sprint 4 — WPF UI ✅ DONE

User picked **Sprint 4 as MVP first, then Sprint 3**.

1. ✅ **MVP single-screen WPF** — `src/FlashlightApp.Wpf/`. Status strip,
   operator + batch + product form, giant PASS/FAIL banner, FLASH button,
   gdb output panel. Wires to Core.
2. ✅ **History + Settings tabs + persistence** — `MainWindow` is a
   `TabControl` with four tabs (Прошивка / Історія / Каталог / Налаштування).
   Settings persisted to `%LOCALAPPDATA%\FlashlightApp\settings.json` via
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
6. ✅ **WiX installer** — `installer/Product.wxs` (WiX 5; UI extension
   pinned to 5.0.2). MSI codepage 1251 + language 1058 (Ukrainian).
   `installer/build-installer.ps1` runs `dotnet publish` (single-file,
   self-contained, win-x64) then `wix build` → `installer/out/FlashlightApp-<ver>-x64.msi`.
   Per-machine scope, installs to `C:\Program Files\FlashlightApp\`,
   Start Menu shortcut, examples/catalog.json + .sig bundled.
   **Does NOT yet chain the ARM toolchain MSI** — operator installs that
   separately. Bundle/chain support is a follow-up (needs an Arm GNU
   Toolchain MSI URL or local file).

### Beyond Sprint 4

| Sprint | Deliverable |
|---|---|
| 3 | GitHub App + Device Flow auth, refresh token in DPAPI LocalMachine, private firmware download |
| 2.5 | Sideload-from-folder (`--sideload-dir`) — synthesises a catalog from `<id>_v<ver>_<part>.elf` + sidecar files |

### Polish backlog (fold in opportunistically)

- **Two-phase gdb (scan-then-flash)** — today a target mismatch is detected
  AFTER `load` because both happen in one gdb invocation. For factory safety
  the scan should be a separate gdb call that aborts before touching flash if
  the target doesn't match.
- **Probe serial capture** — read USB serial from registry to populate
  `probe_serial` column (currently always NULL).
- **Auto-retry on `E_PROBE_BUSY`** — BMP occasionally fumbles a re-enumerate;
  one silent retry would smooth that out.

---

## Design decisions locked in (don't relitigate without reason)

- **Stack:** .NET 8 + WPF, single-file self-contained `.exe`. (User said "use
  any language" but committed to .NET — switching now costs days.)
- **gdb shipping:** chained full ARM toolchain MSI inside our installer
  (NOT vendoring just `gdb.exe`). App detects existing install at the
  standard ARM path; prompts to re-run installer if missing.
- **Target support:** any MCU that Black Magic Probe's `swdp_scan` recognises.
  Code in `FlashlightApp.Core` MUST stay target-agnostic — no PY32 strings
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
- **MVP bench target:** `pocket-light` product, PY32F002Ax5 board. This is
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
operator hint in the UI (table lives in `FlashlightApp.Core/ErrorHints.cs`
once written).

---

## Repository layout

```
FlashlightApp.sln
src/
  FlashlightApp.Core/        Class library — services, state machine, models
    FlashOptions.cs
  FlashlightApp.Cli/         Console flasher (Sprint 1)
    Program.cs               (current stub: arg parsing only)
tests/
  FlashlightApp.Core.Tests/  xUnit (empty)
```

WPF project lands in Sprint 4 as `src/FlashlightApp.Wpf/`.

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
dotnet run --project src/FlashlightApp.Cli -- --help
```

The current CLI stub accepts the full Sprint 1 option surface and just
echoes parsed values — it does not invoke gdb yet.

---

## Coordination with the firmware repo

The app flashes firmware released from any product repo (`pocket-light-firmware`
is the first) via GitHub Releases. Release asset naming convention:

```
<product-id>_v<X.Y.Z>_<part-number>.elf
<product-id>_v<X.Y.Z>_<part-number>.elf.sha256
target.json                              # one per release
```

Examples:
- `pocket-light_v1.0.0_PY32F002Ax5.elf`
- `headlamp_v2.1.0_STM32F103C8.elf`

`target.json` (uploaded as a release asset) declares the target stack so the
catalog generator and the app can verify firmware ↔ hardware pairing:

```json
{
  "product_id":   "pocket-light",
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

- Owns this app repo (`oleksandrmaslov/flashlight_app`) and the firmware repo
  (`oleksandrmaslov/pocket-light-firmware`).
- Direct, action-oriented; prefers to be shown options and pick quickly.
- Comfortable in C# / .NET though primary domain is embedded C.
- Building for non-developer factory operators — keep that audience in mind
  for UX choices (giant PASS/FAIL band, single primary action, etc.).
- Comfortable letting the assistant proceed once a decision is made; doesn't
  need permission to re-ask.

---

## Memory file (firmware repo)

The firmware-side Claude has a project memory at
`~/.claude/projects/c--Users-Alexandr-py32f0-template-project/memory/flashlight_app_project.md`
pointing at this repo. Keep both in sync: if you change a decision here,
update that file too.
