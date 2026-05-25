# CLAUDE.md — FlashlightApp

Session handoff. Read this fully at the start of any new Claude session in this repo.

---

## Goal

Production Windows flashing tool for PY32F0xx-based flashlight firmware.
Drives a Black Magic Probe via `arm-none-eabi-gdb`. Designed to mass-flash
~500 devices per batch from non-developer factory operators, with signed
firmware fetched from private GitHub repos and every attempt logged to SQLite.

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

### Not yet done (Sprint 1, in order — one commit per chunk)

1. **`GdbProcess` wrapper** in `FlashlightApp.Core` — spawns
   `arm-none-eabi-gdb`, parses stdout line-by-line into events.
2. **`FlashStateMachine`** — 7 states (IDLE → PREPARING → PROBE_CHECK →
   ATTACH → LOADING → VERIFYING → FINALIZING → PASS/FAIL → LOGGED → IDLE),
   emits typed outcomes with `error_code`.
3. **`SqliteLogStore`** — single-row append per attempt; schema below.
4. **CLI glue** — `Program.cs` runs the state machine, prints PASS/FAIL,
   writes the log row.
5. **xUnit tests** — `FlashOptions.Parse` cases + state-machine transitions
   driven by fixture gdb output.

**Sprint 1 done = 50 PASS in a row on the bench with the BMP + a real target.**

### Beyond Sprint 1

| Sprint | Deliverable |
|---|---|
| 2 | Signed `catalog.json` + local cache (SHA-256 verify, sideload-from-folder, atomic downloads) |
| 3 | GitHub App + Device Flow auth, refresh token in DPAPI LocalMachine, private firmware download |
| 4 | WPF UI (5 screens: Home / Flash / History / Catalog / Settings), batch locking, CSV export, WiX installer chaining ARM toolchain MSI |

---

## Design decisions locked in (don't relitigate without reason)

- **Stack:** .NET 8 + WPF, single-file self-contained `.exe`. (User said "use
  any language" but committed to .NET — switching now costs days.)
- **gdb shipping:** chained full ARM toolchain MSI inside our installer
  (NOT vendoring just `gdb.exe`). App detects existing install at the
  standard ARM path; prompts to re-run installer if missing.
- **MVP product list:** one — `pocket-light` (PY32F002Ax5). Catalog schema
  supports many; we exercise one.
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

Wall-clock timeout: 15 seconds (PY32F002A flash is 32 KB; anything slower
means BMP/USB trouble — fail with `E_TIMEOUT`).

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

`E_PROBE_NOT_FOUND`, `E_PROBE_BUSY`, `E_SCAN_NO_TARGET`, `E_ATTACH_FAILED`,
`E_LOAD_FAILED`, `E_VERIFY_MISMATCH`, `E_TIMEOUT`, `E_GDB_CRASHED`,
`E_FW_HASH_MISMATCH`. Each maps to a one-line operator hint in the UI.

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

The app flashes firmware released from `pocket-light-firmware.git` via
GitHub Releases. Release asset naming must be strict:

```
pocket-light_v<X.Y.Z>_PY32F002Ax5.elf
pocket-light_v<X.Y.Z>_PY32F002Ax5.elf.sha256
```

If you change the firmware build artefact or rename releases, the catalog
parser here breaks. Coordinate before renaming.

A separate `firmware-catalog` repo (TBD) will hold the signed `catalog.json`
asset and is the single source of truth for what operators can flash.

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
