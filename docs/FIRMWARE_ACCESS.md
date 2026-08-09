# Firmware access model

Goal: an approved person can flash production firmware **without being given the
firmware source**.

## Why this needs three repositories

GitHub read access is repository-wide. There is no "releases only" permission —
`contents: read` grants the source, the full git history, and the releases
together. So the separation cannot be a permission setting; it has to be a
separate repository.

| Repository | Visibility | Holds | Who can read |
|---|---|---|---|
| `iskra-catalog` | **public** | signed `catalog.json` + `.sig` | everyone |
| `iskra-firmware` | **private** | built `.elf` / `.hex` as release assets, nothing else | approved accounts |
| `<product>-firmware` | **private** | source, history, CI | you only |

The catalog is public on purpose. It carries no secrets, and its integrity comes
from the Ed25519 signature plus the per-artefact SHA-256 — not from being hidden.
That is also why a station needs no credentials to read it.

The source repositories are **never referenced by the catalog**. Nothing an
operator's token can reach points at them.

## Wiring it up

**1. Create the distribution repo.** `Energy-for-Ukraine/iskra-firmware`, private,
empty. No source, no history — only releases.

**2. Publish artefacts there from CI.** Each `<product>-firmware` release
workflow uploads its built `.elf`/`.hex` plus `target.json` to a release in
`iskra-firmware`, tagged `v<version>`. Asset naming is unchanged:
`<product-id>_v<X.Y.Z>_<part-number>.<elf|hex>`.

**3. Generate the catalog against the distribution repo:**

```bash
Iskra.Cli --generate-catalog \
  --from-targets <dir> \
  --out catalog.json \
  --owner oleksandrmaslov \
  --dist-repo Energy-for-Ukraine/iskra-firmware \
  --strict-tag-match
```

Without `--dist-repo`, every `elf_source.repo` falls back to the historical
`<owner>/<product-id>-firmware` convention — which points at the source repos and
is exactly what this model avoids. The generator refuses a value that is not
`owner/repo`, because a malformed one would silently redirect every station's
firmware download.

The command prints a confirmation line so a CI log shows which repo was baked in:

```
· firmware served from Energy-for-Ukraine/iskra-firmware (source repos not referenced)
```

**4. Install the `iskra-flasher` App on `iskra-firmware` only.** Remove it from
the source repos. Permissions stay `contents: read`, `metadata: read`. A user
token is the intersection of the App's permissions and that user's own access,
so this caps everyone at read-only on the artefact repo even if their GitHub
account has broader rights elsewhere.

## Approving and revoking a person

**Approve:** add their GitHub account as a **Read** collaborator on
`iskra-firmware`. Nothing else. They then run Iskra → Settings → GitHub sign-in →
Device Flow, and authorise with their own account.

**Revoke:** remove the collaborator. Their next firmware download fails. No other
operator is affected, and no key has to be rotated.

This per-person revocation is the reason to keep Device Flow rather than move to
a GitHub App installation token: an installation token is one machine identity
shared by everyone, so revoking one person means rotating the key for all of
them.

Each operator signs in as **themselves**. Never share one account, and never sign
a station in as the org owner — the token would then be capped only by the App's
permissions, not by a narrow personal grant.

## Test plan

Use a fresh account (for example `energyforukraine`) that has no other access to
your org, so the test proves the grant rather than inheriting privileges.

1. **Before approving** — sign in on a station with the new account and try to
   flash a remote release. Expected: `E_NO_REPO_ACCESS`, with the hint telling the
   operator to ask for access. If you instead see `E_FW_DOWNLOAD_FAILED`
   ("check the network"), the build predates this change.
2. **Approve** — add the account as a Read collaborator on `iskra-firmware`.
3. **Flash again.** Expected: the download succeeds and the flash proceeds.
   `Iskra.Cli --whoami` should print the new account's login, confirming which
   identity the station is using.
4. **Confirm the source stays hidden** — from that account, open
   `github.com/oleksandrmaslov/ci-clop-firmware`. Expected: 404.
5. **Revoke** — remove the collaborator, then flash again. Expected:
   `E_NO_REPO_ACCESS` once the cached token is next used for a download.

Step 4 is the one that actually proves the goal. Steps 1 and 5 prove revocation
is real and is reported honestly.

## What the errors mean

| Code | Meaning | Operator action |
|---|---|---|
| `E_NO_REPO_ACCESS` | 401/403/404 from the release lookup. This account is not approved, or the release tag is gone. | Ask the maintainer for access |
| `E_NOT_SIGNED_IN` | No stored credentials on this station | Settings → sign in |
| `E_AUTH_EXPIRED` | Refresh token past its ~6 month life | Sign in again |
| `E_ASSET_NOT_FOUND` | Repo and release are visible, but the named artefact is missing | Publishing problem — engineer |
| `E_FW_DOWNLOAD_FAILED` | Genuine transport failure | Check the network |

GitHub answers **404 rather than 403** for a private repository the caller cannot
see, so that it does not leak whether the repository exists. `E_NO_REPO_ACCESS`
therefore cannot distinguish "not approved" from "tag deleted" — both are a
question for the maintainer, and neither is a network fault.

## Residual risks

- **The token is decryptable on the station.** `TokenStore` uses DPAPI
  `LocalMachine`, so any local user or admin on that PC can read it. It is capped
  at read-only on the artefact repo, but treat a stolen station as a leaked
  operator credential and revoke that person.
- **No per-station revocation.** GitHub records the Device Flow grant per user,
  not per device. Revoking affects that person on every station they signed into.
  Per-station identity needs a token broker; see Sprint 9.
- **Confidentiality of the binary is limited anyway.** Unless MCU readout
  protection is enabled, anyone holding a shipped device can extract the firmware
  from it. This model protects the *source*, which readout protection cannot.
