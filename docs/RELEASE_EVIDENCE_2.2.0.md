# Iskra 2.2.0 engineering release evidence

Date: 2026-08-26  
Classification: **engineering release; STOP-SHIP for factory production**  
Source baseline: `8df6d703305dfa982711e50e7e0c989abffc5082` with the reviewed 2.2.0
working-tree changes present. The tree is intentionally uncommitted and
untagged; nothing was pushed or deployed.

## Verification summary

| Gate | Result |
|---|---|
| SDK | Pinned .NET SDK 10.0.301 |
| Restore | `dotnet restore Iskra.sln --locked-mode` passed |
| Build | Release, `--no-restore -warnaserror`: 0 warnings, 0 errors |
| Core tests | 527 passed, 0 failed, 0 skipped |
| Application tests | 78 passed, 0 failed, 0 skipped |
| Desktop tests | 21 passed, 0 failed, 0 skipped |
| Total tests | 626 passed, 0 failed, 0 skipped |
| NuGet audit | 0 known vulnerable direct/transitive packages |
| Static packaging validation | 7 PowerShell scripts, 2 Bash scripts, 2 workflow YAML files, and 1 entitlements plist parsed |
| Published checksums | 16/16 entries recomputed and matched |
| Packaged CLI smoke | `--help` exit 0 |
| Release lab gate | Manual/lab mode rejected with exit 2 even with the runtime environment variable set |
| Windows signatures | WPF, CLI, Avalonia, both MSIs, and both setup EXEs: `NotSigned` |

`actionlint` was not installed on this workstation. The workflow files passed a
YAML parse; native runner execution and GitHub Actions semantic validation are
not claimed by this local record.

## Artifacts and SHA-256

| Artifact | Bytes | SHA-256 |
|---|---:|---|
| `artifacts/Iskra-2.2.0-win-x64.zip` | 155,922,835 | `4ff297515c0f08903de9ab24ee7e56661bbe6314934470020e35104b0b8399dc` |
| `installer/out/Iskra-2.2.0-x64.msi` | 90,431,488 | `8711b5baf619c0225a29436c4ca27ffd59a70306b913f4b9e1fe6cdcd25d2429` |
| `installer/out/Iskra-2.2.0-setup-x64.exe` | 313,707,756 | `ec73084ff6a848edb2a990fc283dd7a5b386e9dd72df14fb58c4c3ea709342fd` |
| `installer/out/Iskra-Avalonia-2.2.0-x64.msi` | 77,115,392 | `4dc5608f812fff32b4241fc6d7674c613098f485eb4e196d960b165aa011bd92` |
| `installer/out/Iskra-Avalonia-2.2.0-setup-x64.exe` | 300,466,592 | `79cf5ee3752decc917cec5f697afef160f254712ae5c0cd33e1eee77b15f8928` |
| `artifacts/Iskra-2.2.0-linux-x64.tar.gz` | 86,487,650 | `3473d6a7d237ebe06992d0fbe5fd645e094a98d399ad799fe134ce6fb8713d41` |
| `artifacts/Iskra-2.2.0-linux-arm64.tar.gz` | 82,433,902 | `0901cc5103e625882bc5f0ba47ae07304f60023b1b47faa964114fece6a05190` |
| `artifacts/Iskra-2.2.0-osx-arm64.tar.gz` | 86,174,740 | `e63e507aea18a086e6fb49c233322a2c9a70cba63c65e692b94984a1f28a6703` |
| `artifacts/Iskra-2.2.0-osx-x64.tar.gz` | 90,328,635 | `de4d6d97cba44b1b3fc200bb6572f891a8b9a2709b80ef6a48360a1f6135e2b3` |

The portable Linux executables/scripts and macOS CLI/app executables carry
executable archive modes. Both macOS archives contain an `.app` with
`Contents/Info.plist`. A consolidated, reverified manifest for all ten primary
outputs is at `artifacts/Iskra-2.2.0-RELEASE-SHA256SUMS.txt`.

## What this evidence does not establish

- No Black Magic Probe was connected for this final run, so no 50-PASS bench
  row or wrong-target/unplug/timeout/power-loss HIL evidence exists for 2.2.0.
- Linux and macOS binaries were cross-published on Windows. Their native CI
  definitions exist, but clean-machine desktop, secure-store, USB, GDB, package
  manager, Gatekeeper, and physical-probe behavior were not executed locally.
- The Windows artifacts are not Authenticode signed. Native Linux package/repo
  signing and macOS Developer ID signing/notarization were not performed.
- Production catalog-key rotation, repository governance, authenticated
  append-only central logs, and trustworthy per-product board identity remain
  open. See the audit for the complete stop-ship and residual-risk list.

## Release decision

The files above are suitable for controlled engineering evaluation. They must
not be represented as factory-production artifacts until every stop-ship item in
`docs/ARCHITECTURE_SECURITY_AUDIT_2026-08-25.md` has objective evidence and the
candidate is rebuilt, signed, and tested from a clean tagged source tree.
