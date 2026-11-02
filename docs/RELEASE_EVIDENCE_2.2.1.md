# Iskra 2.2.1 engineering release evidence

Date: 2026-08-28  
Classification: **engineering/lab release; STOP-SHIP for factory production**  
Source baseline: `58cecb14c383056aaa76121cbb9a153fe440814e` with the reviewed
2.2.1 security-closure changes present in the working tree. The tree is
intentionally uncommitted and untagged; nothing was pushed or deployed.

## Verification summary

| Gate | Result |
|---|---|
| SDK | Pinned .NET SDK 10.0.301 |
| Restore | Locked dependency graph used for all release builds |
| Build | Release, `--no-restore -warnaserror`: 0 warnings, 0 errors |
| Core tests | 605 passed, 0 failed, 0 skipped |
| Application tests | 93 passed, 0 failed, 0 skipped |
| Desktop tests | 21 passed, 0 failed, 0 skipped |
| Total tests | 719 passed, 0 failed, 0 skipped |
| NuGet audit | 0 known vulnerable direct/transitive packages |
| Static packaging validation | 11 PowerShell scripts, 2 Bash scripts, 6 workflow YAML files, 2 WiX sources, and 1 entitlements plist parsed |
| Internal bundle manifests | 5/5 manifests, 44/44 entries recomputed and matched |
| Published checksums | 16/16 entries recomputed and matched |
| Packaged CLI smoke | `--help` exit 0 |
| Release lab gate | Unsigned-catalog manual/lab mode rejected with exit 2 even with the runtime environment variable set |
| Windows signatures | WPF, CLI, Avalonia, both MSIs, and both setup EXEs: `NotSigned` |
| SPDX SBOMs | Five SPDX 2.2 JSON documents parsed and package/file inventories validated by the release generator |

`actionlint` was not installed on this workstation. The workflow files passed a
YAML parse; native runner execution and GitHub Actions semantic validation are
not claimed by this local record.

## Artifacts and SHA-256

| Artifact | Bytes | SHA-256 |
|---|---:|---|
| `Iskra-2.2.1-win-x64.zip` | 156,041,267 | `2e75a77307e7de05f4e5bff792c14cf5b83cf97fdd50aa29e1ec6ea657bfd77c` |
| `Iskra-2.2.1-setup-x64.exe` | 313,771,654 | `23097bc91c01ffe759e6c51d9c67abfcbaff30ecca1ee79760b5bc06b16ab4b9` |
| `Iskra-2.2.1-x64.msi` | 90,501,120 | `140b45c079f2bfa167b79794d37c43873db096746bd9d01d243caab6ea96d2f0` |
| `Iskra-2.2.1-preinstall-check.ps1` | 7,867 | `0ab62519c566f7665b4b9d057f77ed04f712322ce2bbe32e640a675be85a67f0` |
| `Iskra-Avalonia-2.2.1-setup-x64.exe` | 300,548,074 | `b5784cb27c2d88b26e5f1defc3253710d5d2580a73c66aa755065061eca2c239` |
| `Iskra-Avalonia-2.2.1-x64.msi` | 77,201,408 | `9a4fd7054d8115a2556cce962d7fc5976225bb6103ec5bff902dc763941e6b37` |
| `Iskra-2.2.1-linux-x64.tar.gz` | 86,575,370 | `81a23266e6e5039908675dba8b5f402ed8467dc3bd85ee4fa7da8f215281303c` |
| `Iskra-2.2.1-linux-arm64.tar.gz` | 82,512,468 | `66e7bd187323efc4a01a0c10c2ea32062b79944ab4b07fc058afd3fa7a9ff681` |
| `Iskra-2.2.1-osx-arm64.tar.gz` | 86,259,948 | `b3b3fd9c496b3cb531effe12c4c51c7d114c10a1aa478fcc63bfa6e571013eb9` |
| `Iskra-2.2.1-osx-x64.tar.gz` | 90,406,339 | `0b44d482488bedb2b54d841ba5fa397d83def5776ab9aa121119e196e3239c8e` |
| `Iskra-2.2.1-win-x64.spdx.json` | 106,798 | `5b61e17d188cd6033c6a02cc379c243eed1c140dc86278de14c14c2d00e568a2` |
| `Iskra-2.2.1-linux-x64.spdx.json` | 110,172 | `57015f262f30ba7c3437e5b7f6d30b6d07fbc0fe3a1d3cf0a148b6a589ad91f8` |
| `Iskra-2.2.1-linux-arm64.spdx.json` | 110,172 | `87ef48dabc72af62e6c94a72c7ba4e8a45c31357b2517e0ab513c4b5bdc5f9fd` |
| `Iskra-2.2.1-osx-arm64.spdx.json` | 108,300 | `a69e6d95a6974ac400a4f53cb1dbe2293d9c39f363df690fb54b6055864de4df` |
| `Iskra-2.2.1-osx-x64.spdx.json` | 108,300 | `8f570b959a4f45f6df8689683bf5b26cb0baa529e4f31b49159c6bee08a7d9ec` |
| `nuget-vulnerability-audit-2.2.1.json` | 983 | `d7c4ddf56a533a19cec3de6660394bfcff06f6cac18cb4e92d08d504b0ec5139` |

The authoritative consolidated manifest is
`artifacts/Iskra-2.2.1-RELEASE-SHA256SUMS.txt`. Portable Linux and macOS
executables/scripts retain executable archive modes; both macOS bundles include
an `.app` layout with `Contents/Info.plist`.

## What this evidence does not establish

- No Black Magic Probe was connected for this final run. There is no renewed
  50-PASS row or wrong-target, unplug, timeout, reconnect, contention, or
  power-loss HIL evidence for the exact 2.2.1 candidate.
- Linux and macOS binaries were cross-published on Windows. Native
  clean-machine desktop, secure-store, USB, GDB, package-manager, Gatekeeper,
  and physical-probe behavior were not executed locally.
- Windows artifacts are not Authenticode signed. Linux package/repository
  signing and macOS Developer ID signing/notarization were not performed.
- The production catalog key is not rotated. The central audit still uses a
  shared, mutable GitHub Contents write path rather than per-station
  authenticated append-only ingestion. Trustworthy board identity remains
  open.
- A dirty, untagged local build does not carry protected-source provenance.
- Deleting the legacy machine-wide GitHub token file cannot revoke a copied
  token. Stations used with 2.2.0 or earlier must revoke that GitHub Device Flow
  authorization and sign in again.

## Release decision

These files are suitable for controlled engineering evaluation. They must not
be represented as factory-production artifacts until every stop-ship item in
`docs/ARCHITECTURE_SECURITY_AUDIT_2026-08-25.md` has objective evidence and the
candidate is rebuilt from a clean protected tag, signed, and tested on each
supported operating system and the production probe/board hardware.
