# Iskra architecture and security audit — 2026-08-25

## Executive decision

**Release classification: STOP-SHIP for factory production; acceptable as a
labelled engineering release candidate.**

The reviewed code has a strong fail-closed local flash path and now has
functional Windows/Linux/macOS application and packaging paths. The remaining
blockers are not cosmetic: the embedded catalog key is still a development key,
official binaries are not fully signed/notarized, central log ingestion is
mutable and uses shared credentials, board identity is only MCU-family-level,
and the required clean-machine and hardware-in-the-loop matrix has not run.

This document separates code findings fixed in the candidate from owner,
infrastructure, and hardware acceptance work that cannot be truthfully closed by
a Windows-only source audit.

## Scope and method

Reviewed surfaces:

- `Iskra.Core`, `Iskra.Application`, WPF, Avalonia, and CLI boundaries;
- catalog signature, source allowlist, revocation, anti-rollback, and local
  activation;
- firmware acquisition, cache integrity, ELF/HEX parsing, and address ranges;
- GDB construction, process lifecycle, target selection, flash verification,
  probe concurrency, and cancellation;
- SQLite audit writes, optional batch locks, and GitHub log shipping;
- GitHub Device Flow and platform credential storage;
- application update metadata and exact runtime selection;
- Windows, Linux, and macOS builders plus GitHub CI/release workflows;
- dependency lock files, warning gates, automated tests, and local diagnostics.

Method: manual trust-boundary and data-flow review, adversarial misuse cases,
source inspection of process/network/file parsers, dependency audit, warning-as-
error build, automated test execution, cross-RID publication, package-script
static validation, and local station diagnostics. No production secrets were
requested or generated. No deployment, tag, commit, or machine power action was
performed.

## Architecture and trust boundaries

```text
Operator
  | WPF / Avalonia / CLI
  v
Iskra.Application
  | catalog session, readiness, batch policy, flash/history/settings/auth
  v
Iskra.Core
  |-- signed catalog ---> embedded Ed25519 trust root + source allowlist
  |-- firmware bytes ---> SHA-256 + ELF/HEX map validation
  |-- guarded GDB/MI ---> exclusive probe lock ---> BMP ---> target MCU
  |-- local audit ------> SQLite
  |-- remote inputs ----> GitHub catalog/firmware/update APIs
  `-- credentials ------> DPAPI / Secret Service / macOS Keychain
```

Primary trust boundaries:

1. **Release author to station.** Ed25519 catalog signature, compiled source
   allowlist, SHA-256 release digest, revocation list, and `generated_at`
   anti-rollback floor.
2. **Station application to external content.** Bounded HTTP bodies, strict JSON
   and firmware parsing, deterministic cache paths, and hash verification on
   every cache hit.
3. **Application to GDB/BMP/target.** Argument-list process launch, disabled GDB
   init/auto-load/debuginfod/history, normalized endpoints, exclusive
   per-probe lock, one held MI session, target scan/attach gate, load, and
   `compare-sections` verification.
4. **Local user to credentials.** OS-provided encrypted stores with no plaintext
   fallback. Helper secrets use stdin, not command arguments or environment.
5. **Station to audit system.** Local SQLite is authoritative. The current
   GitHub Contents mirror is outside the production trust boundary because it
   is mutable and uses a shared station-app key.

The documented threat model does **not** promise safety after full compromise of
the station's operating-system account. Such an attacker can replace trusted
runtime/toolchain components or interfere with the operator session. Production controls
must therefore also include locked-down station accounts, signed/pinned
binaries, controlled GDB provenance, and re-imaging procedures.

## Findings fixed in this candidate

| ID | Severity | Finding | Resolution |
|---|---:|---|---|
| SEC-01 | High | Catalog bytes could be checked and parsed through separate file reads, leaving a check/use race. | Catalogs are read once into a bounded snapshot; signature verification and JSON parsing use the same bytes. Oversized catalogs/signatures fail closed. |
| SEC-02 | High | Signed catalog fields could be overridden from CLI arguments without a sufficiently explicit lab boundary. | Target/hash/firmware overrides require both manual-flash opt-in and the lab environment gate. Normal signed-catalog operation cannot silently replace signed values. |
| SEC-03 | Critical | A two-process scan-then-flash sequence left a target swap/reselection window and opened firmware before the physical target gate. | One guarded GDB/MI process now holds the probe connection, scans and safely attaches target 1, applies the application target gate, and only then opens/loads firmware. Rejection detaches; every exit path has bounded process-tree termination. |
| SEC-04 | High | Catalog anti-rollback was enforced during remote fetch but not every local activation. | Verified catalogs advance an interprocess-serialized, atomically written activation floor. Older, malformed-state, and implausibly future catalogs fail closed; reopening the current catalog is allowed. |
| SEC-05 | High | Several HTTP/JSON/asset reads could consume unbounded memory or disk. | Update/catalog metadata and error bodies have explicit limits; firmware assets stream to disk with a 64 MiB ceiling and are deleted on failure. |
| SEC-06 | High | ELF acceptance was too permissive for a Cortex-M production flasher. | The parser accepts only little-endian ELF32 ARM `ET_EXEC`/`ET_DYN`, validates program-table bounds, requires `filesz <= memsz`, and proves every PT_LOAD byte is present and addressable. |
| SEC-07 | Critical | A verified physical flash could be shown as PASS even if the mandatory SQLite audit write failed. | WPF/Avalonia workflow and CLI now return `E_AUDIT_WRITE_FAILED`; verified firmware without a durable local record cannot report PASS. |
| SEC-08 | High | Linux/macOS private firmware had no secure credential adapter. | Linux Secret Service and macOS Keychain adapters are wired into CLI/Avalonia. Helpers are fixed absolute paths, time-bounded, receive secrets on stdin, and fail closed without a store. |
| SEC-09 | Medium | App updates could choose an asset for the wrong Unix architecture. | Update lookup requires an exact runtime identifier, including Linux arm64, macOS arm64, and macOS x64. |
| SEC-10 | High | Cross-platform release automation and native package policy were incomplete. | Added a five-runner native CI matrix, locked restores, warning/dependency gates, exact-RID publish smoke tests, deterministic portable archives, Linux `.deb`/udev packaging, macOS `.app`/DMG signing/notarization gates, and locally pinned WiX. |
| SEC-11 | High | Closing Avalonia during an active flash could tear down the process mid-write. | Window close is refused while flashing and a localized warning is shown; invalid settings also block exit instead of being silently discarded. |
| SEC-12 | High | A trusted catalog could omit the absolute flash base, leaving a signed size-only target check, and Intel HEX records could wrap the 32-bit address space. | Signed catalogs now require `flash_origin`; the complete flash window must fit in 32 bits, and HEX records that cross the address-space boundary fail closed. The example catalog was updated and re-signed with the existing development key. |
| SEC-13 | High | Audit configuration could target an in-memory SQLite database, a URI data source, a directory, or a Windows device path and still appear writable. | `AuditDatabasePathPolicy` requires a normalized persistent file path. Settings, CLI, application path creation, and the flash workflow all reject unsafe audit targets before GDB starts with `E_AUDIT_PATH_INVALID`. |
| SEC-14 | High | The unsigned-catalog/manual-flash escape hatch was controlled only at runtime and could be enabled on a normal release binary. | Lab catalog support is compiled out of ordinary Release builds. An explicitly lab-enabled build is required in addition to the runtime environment gate and CLI opt-in. |
| SEC-15 | Medium | Bounded readers still requested buffered HTTP completion first, and one file-size comparison could overflow an `int`. | All bounded HTTP consumers request headers-only completion before reading through explicit ceilings; file length comparisons use `long` arithmetic. |
| SEC-16 | Medium | Sanitized station IDs could collapse distinct values or preserve `.`/`..` path semantics in central log paths. | Normalized log path segments reject traversal names and carry a SHA-256 suffix whenever normalization changes the station ID, preserving a stable injective mapping. |
| SEC-17 | Medium | Unbounded frequency/timeout settings and preflight failures taking a batch reservation could create denial-of-service or poison a batch before a flash attempt. | CLI, catalog, and settings validation cap SWD frequency at 50 MHz and timeout at one hour; preflight and batch-conflict audit rows no longer reserve the batch lock. |
| SEC-18 | High | macOS discovery trusted a filename convention instead of proving USB VID/PID/interface identity. | A bounded, DTD-disabled `ioreg` plist adapter binds each callout endpoint to official BMP VID/PID, interface number, serial/location identity, and rejects unidentified endpoints. |
| SEC-19 | High | Probe exclusion was per-user/endpoint based, so aliases or another OS session could contend for one physical probe. | A machine-wide named mutex is keyed by normalized physical identity. Windows COM aliases, Linux symlinks, and macOS endpoint aliases converge on VID/PID+serial or a stable physical fallback. |
| SEC-20 | High | A crash/cancellation could leave no durable evidence that a physical transaction had begun. | The workflow commits a `STARTED` row before firmware/network/GDB work and transactionally finalizes the same row. Cancellation becomes terminal `E_CANCELLED`; a hard crash intentionally leaves a visible `STARTED` row with an explicit recovery API. |
| SEC-21 | High | ELF range checks did not prove that the sections GDB/BFD chose to load matched the preflight map, and firmware could change before GDB opened it. | Iskra maps allocatable file-backed sections through `PT_LOAD` LMA, compares the exact runtime name/address/size multiset, and gives GDB only a random private, flushed, re-hashed firmware snapshot lease. |
| SEC-22 | High | A future caller could bypass verified-session policy with a raw mutable `Catalog`, and the multi-file cache/floor was not digest-bound transactionally. | `FlashWorkflow` requires an opaque activation permit; parsed collections are deep-frozen; production trust/GDB injection APIs are sealed; cache generations use a digest-bound current pointer and timestamp+digest rollback floor. |
| SEC-23 | High | Corrupt settings, concurrent rotating-token mutations, unbounded process/UI lines, and the missing interval scheduler could silently weaken or exhaust a station. | Unsafe settings are preserved and block startup/flash; login/logout/refresh share a per-user cross-process lock; process/UI/HEX/sysfs input is bounded before allocation; the persisted scheduler runs immediately and at the configured interval without overlap. |
| SEC-24 | High | URLs inside GitHub responses could direct bearer tokens or operator browser/update actions to an attacker-controlled origin. | Authenticated asset requests require exact `https://api.github.com` release-asset paths; Device Flow, catalog assets, and update/browser links require exact GitHub HTTPS origins. Adversarial host/userinfo/scheme cases fail closed. |
| SEC-25 | Medium | Release output lacked a machine-readable component inventory and signed workflow provenance. | A pinned Microsoft SBOM tool generates and validates SPDX 2.2 manifests; the native release matrix emits build and SBOM attestations through a SHA-pinned GitHub action. Official trust still depends on a clean tag and configured signing identities. |

## Open stop-ship findings

| ID | Severity | Required action | Owner/evidence |
|---|---:|---|---|
| OPEN-01 | Critical | Rotate the embedded development catalog key on a clean system; keep the production private key offline/HSM/KMS-backed; re-sign the catalog and rehearse revocation. | Owner/security; new public-key build and signed catalog verification evidence. |
| OPEN-02 | Critical | Deploy reviewer-gated catalog signing and repository governance: protected branches, CODEOWNERS, immutable release process, pinned workflows, least privilege, secret scanning, and no self-approval. | GitHub owner; exported rules/settings and successful gated publication. |
| OPEN-03 | Critical | Replace shared-key mutable GitHub Contents log shipping with per-station authenticated append-only/tamper-evident ingestion and authenticated operator identity. | Architecture/operations; threat model, server tests, key rotation, station isolation, and recovery exercise. |
| OPEN-04 | Critical | Add trustworthy per-product board identity (signed board-ID/UID policy). MCU-family `bmp_match` cannot distinguish products sharing a chip family. | Firmware/catalog/HIL; wrong-board refusal before any write. |
| OPEN-05 | High | Configure Authenticode plus trusted timestamping for WPF/Avalonia/CLI/MSI/Burn. Tagged Windows publication is intentionally blocked until then. | Release owner; signature verification from a clean Windows station. |
| OPEN-06 | High | Configure Developer ID signing and Apple notarization/stapling for both architectures; validate Keychain behavior from the signed app and CLI. | Apple/release owner; Gatekeeper and notarization evidence on clean macOS hosts. |
| OPEN-07 | High | Configure Linux GPG/repository signing and validate `.deb` install/remove/upgrade, udev permissions, desktop entry, Secret Service, and GDB dependency on clean x64/arm64 hosts. | Linux release owner; native package and HIL logs. |
| OPEN-09 | High | Approve and pin Unix GDB/toolchain package versions and hashes. Code now restricts production discovery to administrator/package roots and `--doctor` reports canonical path/hash/version, but the operational allowlist is not signed release policy yet. | Release/operations; controlled package/version/hash policy and retained `--doctor` evidence. |
| OPEN-10 | High | Run clean-machine and HIL acceptance on Windows x64, Linux x64/arm64, and macOS arm64/x64: success, exact GDB load plan, multi-user/alias/reconnect probe contention, wrong target, unplug, timeout, cancel, crash/power-loss recovery, offline, rollback, revocation, secure-store, and update selection. | QA/HIL matrix with retained logs. |
| OPEN-11 | High | Repeat 50 consecutive PASS cycles with one real BMP and known-good board after the guarded GDB change, using the exact signed release candidate. | Factory engineering; 50 durable PASS records and zero parser/process anomalies. |
| OPEN-12 | High | If batch mode is enabled across stations, implement the documented fail-closed shared reservation. Local SQLite locks alone do not prevent cross-station split brain. | Factory architecture; concurrency and offline-failure tests. |

## Residual medium-risk items

- Add parser fuzzing for catalog, GDB output/MI framing, ELF, HEX, and JSONL;
  current adversarial unit cases are useful but not coverage-guided fuzzing.
- Define and rehearse the supervisor policy for classifying durable `STARTED`
  rows after a real station crash/power loss. The recovery API requires an
  explicit cutoff so one process cannot mark another live attempt abandoned.
- SBOM/build attestations exist in CI, but a dirty, untagged local engineering
  build cannot carry trusted provenance. Production evidence must originate
  from the protected tagged workflow with official signing identities.

## Cross-platform acceptance matrix

| Capability | Windows x64 | Linux x64 | Linux arm64 | macOS arm64 | macOS x64 |
|---|---:|---:|---:|---:|---:|
| Compiles/publishes | Yes | Yes, cross-published | Yes, cross-published | Yes, cross-published | Yes, cross-published |
| Native CI definition | Yes | Yes | Yes | Yes | Yes |
| Secure-store implementation | DPAPI CurrentUser | Secret Service | Secret Service | Keychain | Keychain |
| Native package definition | MSI/Burn | `.deb` | `.deb` | `.app`/DMG | `.app`/DMG |
| Official code/package signing | Pending | Pending | Pending | Pending | Pending |
| Clean-machine package test | Pending | Pending | Pending | Pending | Pending |
| Real BMP/board HIL | Prior Windows lab only; rerun pending | Pending | Pending | Pending | Pending |

Cross-publishing proves dependency resolution and native binary generation; it
does not prove that a target-OS desktop, credential service, USB stack, package
manager, Gatekeeper, or physical probe works. Those rows remain explicitly
open.

## Local verification record

The 2.2.1 engineering release was verified locally on 2026-08-27. Exact commands,
artifact sizes, and SHA-256 values are retained in
`docs/RELEASE_EVIDENCE_2.2.1.md`. The following gates were green:

- locked restore with .NET SDK 10.0.301;
- Release solution build with warnings treated as errors;
- Core 600/600, Application 92/92, and headless Avalonia 21/21 test suites
  (713 total, no failures or skips);
- NuGet direct/transitive vulnerability query (zero known vulnerable packages);
- PowerShell parser validation, Bash `-n`, and workflow YAML parse;
- four cross-published Unix archives, the Windows portable ZIP, both Windows
  MSI/Burn installer pairs, and validated SPDX SBOMs; published checksum entries
  were recomputed and matched;
- packaged CLI help and Release lab-gate smoke tests.

All inspected Windows EXE/MSI files report `NotSigned`. No claim of HIL, native
Linux/macOS execution, signing, notarization, or factory approval is made by
this audit.
