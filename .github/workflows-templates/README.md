# Iskra — CI workflow templates

These YAML files **are not active in this repo**. They live here as templates
to copy into the satellite repos that drive auto-discovery and the cloud log
mirror:

| File | Goes into | Purpose |
|---|---|---|
| `notify-iskra-catalog.yml` | every `*-firmware` repo, at `.github/workflows/` | On `release.published`, fires a `repository_dispatch` event at `iskra-catalog`. |
| `regenerate-catalog.yml` | `oleksandrmaslov/iskra-catalog`, at `.github/workflows/` | Walks every `*-firmware` repo, collects `target.json` files, runs `Iskra.Cli --generate-catalog`, signs the result, publishes as a new release of `iskra-catalog`. |
| `rebuild-logs-db.yml` | `oleksandrmaslov/iskra-logs`, at `.github/workflows/` | Walks `stations/<id>/*.jsonl` files pushed by every station, rebuilds a queryable SQLite `logs.db` at the repo root, commits if changed. |
| `build_logs_db.py` | `oleksandrmaslov/iskra-logs`, at `.github/scripts/` | Helper called by `rebuild-logs-db.yml`. Idempotent: re-running on the same JSONL produces the same `logs.db`. |

## One-time setup

### 1. Create the `iskra-catalog` repo

- New **public** repo: `oleksandrmaslov/iskra-catalog`
- README it however you like; the repo's job is to host signed catalog releases.

### 2. Move the dev signing key into the CI secret

Encode the existing Ed25519 private key (it's already base64 on disk) and store it as a repo secret in `iskra-catalog`:

```powershell
$priv = Get-Content "C:\Users\IMT - Teilnehmer\.claude\projects\c--Users-Alexandr-flashlight-app\keys\catalog-key.priv" -Raw
gh secret set CATALOG_PRIV_KEY --repo oleksandrmaslov/iskra-catalog --body $priv
```

(Requires `gh` CLI authenticated; the secret value is what's already inside the `.priv` file — a 44-character base64 string.)

### 3. Drop `regenerate-catalog.yml` into iskra-catalog

```powershell
# from this repo:
cd c:\Users\Alexandr\iskra-app
# copy the workflow into a working clone of iskra-catalog:
Copy-Item .github\workflows-templates\regenerate-catalog.yml `
          c:\Users\Alexandr\iskra-catalog\.github\workflows\regenerate-catalog.yml
```

Commit + push to `iskra-catalog`. Test the workflow with **Actions tab → Run workflow** (the `workflow_dispatch` trigger is there for exactly this purpose).

If firmware repos are private, add one more secret to `iskra-catalog` before
the test run. Create a fine-grained PAT with:

- **Repository access:** every private `*-firmware` repo to include, starting with `ci-clop-firmware`
- **Repository permissions → Contents:** Read-only
- **Expiry:** 1 year

Store it in `iskra-catalog`:

```powershell
$token = Read-Host -AsSecureString "Paste read-only firmware PAT"
$plain = [System.Net.NetworkCredential]::new("", $token).Password
gh secret set ISKRA_BOT_TOKEN --repo oleksandrmaslov/iskra-catalog --body $plain
```

### 4. Wire the firmware repos to notify the catalog

For each `*-firmware` repo (starting with `ci-clop-firmware`):

```powershell
Copy-Item .github\workflows-templates\notify-iskra-catalog.yml `
          c:\Users\Alexandr\ci-clop-firmware\.github\workflows\notify-iskra-catalog.yml
```

Create a fine-grained PAT (https://github.com/settings/personal-access-tokens/new) with:
- **Resource owner:** `oleksandrmaslov`
- **Repository access:** `iskra-catalog` only
- **Repository permissions → Contents:** Read and write
- **Expiry:** 1 year

Store as `ISKRA_CATALOG_DISPATCH_TOKEN` secret in the firmware repo:

```powershell
$token = Read-Host -AsSecureString "Paste PAT"
$plain = [System.Net.NetworkCredential]::new("", $token).Password
gh secret set ISKRA_CATALOG_DISPATCH_TOKEN --repo oleksandrmaslov/ci-clop-firmware --body $plain
```

Commit + push the workflow file to the firmware repo. The next `gh release create` in that repo will automatically trigger the catalog regenerate.

## Sprint 5 — iskra-logs setup

### 1. Create the `iskra-logs` repo

- New **private** repo: `oleksandrmaslov/iskra-logs`
- No README needed; the workflow creates `logs.db` on first run.

### 2. Register a write-only GitHub App for stations

On https://github.com/settings/apps/new:

- **Name:** `Iskra Log Shipper` (or similar)
- **Homepage URL:** anything
- **Webhook:** uncheck "Active"
- **Repository permissions:**
  - Contents: **Read and write**
  - Metadata: Read-only (mandatory)
  - everything else: No access
- **Where can this GitHub App be installed?** "Only on this account"

Save. Note the **App ID** (numeric). Generate a private key (.pem) and
download it — this is the single most sensitive artifact of Sprint 5.

Install the app on **iskra-logs only** (Settings → Install App → Only select
repositories). Note the **Installation ID** (numeric, appears in the
installation URL: `/settings/installations/<id>`).

### 3. Bake the app config into the binary

Edit `src/Iskra.Core/GitHubAppConfig.cs`:

```csharp
public const string LogShipperAppId          = "<app-id>";
public const string LogShipperInstallationId = "<installation-id>";
```

Commit + cut a new app release. Until this is done, every station's log
shipper stays dormant.

### 4. Distribute the .pem to factory stations

The MSI is the right place. Until the MSI is updated to include the .pem
deployment step, manually copy:

```
%PROGRAMDATA%\Iskra\station-app.pem
```

on each station. Make sure ACLs are tight — this file lets anyone
who reads it push to iskra-logs. Worst-case blast radius from a
compromised .pem is "write garbage into iskra-logs"; firmware and catalog
trust roots are untouched.

### 5. Drop the workflow + script into iskra-logs

```powershell
# Workflow:
Copy-Item .github\workflows-templates\rebuild-logs-db.yml `
          c:\Users\Alexandr\iskra-logs\.github\workflows\rebuild-logs-db.yml

# Helper script:
New-Item -ItemType Directory c:\Users\Alexandr\iskra-logs\.github\scripts -Force
Copy-Item .github\workflows-templates\build_logs_db.py `
          c:\Users\Alexandr\iskra-logs\.github\scripts\build_logs_db.py
```

Commit + push to iskra-logs. Confirm with **Actions tab → Run workflow**
that it succeeds against an empty `stations/` (produces an empty `logs.db`).

### 6. First end-to-end smoke

On a station with the new app installed:

```powershell
Iskra.Cli --ship-logs-now
```

This should report `Вивантажено: N рядк(ів) → 1 новий файл`. Within ~30s
the nightly Action's `push` trigger fires (because `stations/**/*.jsonl`
changed), and a fresh `logs.db` lands in the repo root.

Query from your laptop:

```powershell
git clone https://github.com/oleksandrmaslov/iskra-logs
cd iskra-logs
sqlite3 logs.db "SELECT product_id, COUNT(*), SUM(result='PASS') FROM flash_attempts GROUP BY product_id;"
```

## End-to-end smoke test

1. In `ci-clop-firmware`, run the release walkthrough from CLAUDE.md (steps 5–7).
2. Within ~30 seconds, watch the Actions tab of `iskra-catalog` — `regenerate catalog` job should appear and complete.
3. https://github.com/oleksandrmaslov/iskra-catalog/releases shows a new release `catalog-<timestamp>` with `catalog.json` and `catalog.json.sig` assets.
4. Once chunks 3–5 are done, the WPF app on the lab box picks up the new catalog automatically and offers v1.0.0 (or whatever was just released) in the version dropdown.
