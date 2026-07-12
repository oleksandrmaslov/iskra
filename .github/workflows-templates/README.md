# Iskra workflow templates

These files are deployment templates, not active workflows in this repository.
Copying or changing a template here does not update the satellite repositories.

| File | Destination | Purpose |
|---|---|---|
| `notify-iskra-catalog.yml` | each approved firmware repo | Dispatch a release notification without evaluating release metadata as shell code |
| `regenerate-catalog.yml` | `oleksandrmaslov/iskra-catalog` | Independently verify allowlisted firmware, build an unsigned catalog, then require review before signing |
| `firmware-repos.txt.example` | `iskra-catalog/firmware-repos.txt` | Explicit firmware repository allowlist |
| `rebuild-logs-db.yml` | `oleksandrmaslov/iskra-logs` | Rebuild the current lab SQLite mirror from station JSONL |
| `build_logs_db.py` | `iskra-logs/.github/scripts/` | Deterministic JSONL ingest helper |

## Catalog signing deployment

The current embedded key is a development key. Do not place it into a new
factory environment. Generate the production key on a clean workstation,
embed only its public key in Iskra, and keep its private key in offline,
HSM/KMS, or equivalent controlled custody.

In `iskra-catalog`:

1. Copy `firmware-repos.txt.example` to tracked `firmware-repos.txt` and
   list only reviewed `*-firmware` repositories, one name per line.
2. Create a GitHub environment named `signing`.
3. Configure required reviewers and disallow self-review.
4. Store `CATALOG_PRIV_KEY` as an environment secret, not a repository secret.
5. If allowlisted firmware repositories are private, create a fine-grained
   `ISKRA_BOT_TOKEN` with read-only Contents access to only those repos.
6. Copy `regenerate-catalog.yml` to `.github/workflows/`.
7. Review the pinned `ISKRA_APP_REF` and every action SHA before deployment.
   After this repository's hardening changes are released, update the immutable
   ref to that reviewed commit; never use `main`.
8. Protect `main`, require PR review/status checks, add CODEOWNERS, restrict
   Actions, and enable dependency/security scanning.

The workflow downloads both `target.json` and the exact ELF/HEX release asset,
computes SHA-256 independently, refuses a changed digest under an existing
product/version, uploads the unsigned catalog plus review evidence, and exposes
the signing key only after the environment approval.

## Firmware notification deployment

For every repository listed in `firmware-repos.txt`:

1. Copy `notify-iskra-catalog.yml` to `.github/workflows/`.
2. Create a fine-grained token restricted to `iskra-catalog` with the minimum
   permission GitHub requires for `repository_dispatch`.
3. Store it as `ISKRA_CATALOG_DISPATCH_TOKEN` in that firmware repository.
4. Protect release creation and the branch that builds release assets.

The notification only wakes the catalog workflow. It does not bypass the
allowlist, byte verification, immutable-version check, or signing approval.

## Catalog smoke test

1. Run `workflow_dispatch` in `iskra-catalog`.
2. Inspect the `unsigned-catalog` artifact, `catalog-review.txt`,
   `catalog.sha256`, and `verified-assets.tsv`.
3. Approve the `signing` environment only when the product/version/digest set
   is expected.
4. Confirm the release contains exactly `catalog.json` and
   `catalog.json.sig`.
5. Confirm Iskra accepts the signed catalog, rejects a modified copy, rejects a
   rollback/equal timestamp, and leaves its previous cache unchanged on error.

## Current lab log mirror

Copy `rebuild-logs-db.yml` and `build_logs_db.py` into `iskra-logs`, then
run `workflow_dispatch` once against an empty `stations/` tree and once after
`Iskra.Cli --ship-logs-now`.

The existing log design is lab-only: stations share an App credential with
repository-wide Contents write access, station identity is self-asserted, and
history can be rewritten. Before factory rollout, replace it with per-station
authentication and server-stamped append-only/tamper-evident ingestion as
required by `ROADMAP.md` Sprint 9. Do not distribute the shared PEM broadly as
if it were a production audit credential.

## Deployment record

For every template rollout, record the source Iskra commit, destination
repository commit, environment/ruleset review, secret rotation date, and smoke
test result. The source files here are not evidence that deployment happened.
