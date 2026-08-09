# One-shot backfill: copy already-published firmware artefacts into the
# distribution repo.
#
# WHY THIS IS NEEDED ONCE
# publish-to-iskra-firmware.yml only fires on NEW releases. Regenerating the
# catalog with --dist-repo repoints every existing elf_source at the
# distribution repo, so without this backfill every version already in the
# catalog would stop flashing. Run this BEFORE regenerating the catalog.
#
# WHAT IT COPIES
# Exactly the releases the signed catalog references - nothing else. The
# catalog is the authority on which (product, version) pairs are approved, so a
# stale or unreferenced source release is never propagated.
#
# SAFETY
#   * Every artefact's SHA-256 is verified against the catalog BEFORE upload.
#     A mismatch aborts that release rather than publishing bytes stations
#     would later refuse.
#   * An existing distribution tag is left alone, never overwritten.
#   * Dry run is the default. Pass -Execute to actually publish.
#
# Usage:
#   pwsh ./scripts/backfill-iskra-firmware.ps1                 # dry run
#   pwsh ./scripts/backfill-iskra-firmware.ps1 -Execute

param(
    [string] $Catalog = "$env:LOCALAPPDATA\Iskra\catalog\latest.json",
    [string] $Owner = 'oleksandrmaslov',
    [string] $DistRepo = 'oleksandrmaslov/iskra-firmware',
    [switch] $Execute
)

$ErrorActionPreference = 'Continue'

if (-not (Test-Path -LiteralPath $Catalog)) {
    throw "catalog not found: $Catalog"
}
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "gh CLI is required"
}

$cat = Get-Content -LiteralPath $Catalog -Raw | ConvertFrom-Json
$work = Join-Path ([IO.Path]::GetTempPath()) ("iskra-backfill-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null

$planned = 0; $done = 0; $skipped = 0; $failed = 0

foreach ($product in $cat.products) {
    $productId = $product.product_id
    $sourceRepo = "$Owner/$productId-firmware"

    foreach ($rel in $product.releases) {
        $version = $rel.version
        $asset = $rel.elf_filename
        $expectedSha = $rel.elf_sha256.ToLowerInvariant()
        $sourceTag = "v$version"
        $distTag = "$productId-v$version"
        $planned++

        Write-Host ""
        Write-Host "[$productId v$version] $sourceRepo@$sourceTag -> $DistRepo@$distTag" -ForegroundColor Cyan

        gh release view $distTag --repo $DistRepo *> $null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  already published - leaving untouched" -ForegroundColor DarkGray
            $skipped++
            continue
        }

        $stage = Join-Path $work $distTag
        New-Item -ItemType Directory -Force -Path $stage | Out-Null

        gh release download $sourceTag --repo $sourceRepo --dir $stage --clobber *> $null
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  FAILED to download $sourceRepo@$sourceTag" -ForegroundColor Red
            $failed++
            continue
        }

        $assetPath = Join-Path $stage $asset
        if (-not (Test-Path -LiteralPath $assetPath)) {
            # This is the stale-asset case: the release exists but carries a
            # different version's artefact. Publishing it would put bytes in the
            # distribution repo that no catalog entry describes.
            Write-Host "  SKIP - $sourceTag does not contain $asset" -ForegroundColor Yellow
            $skipped++
            continue
        }

        $actualSha = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualSha -ne $expectedSha) {
            Write-Host "  ABORT - SHA-256 mismatch" -ForegroundColor Red
            Write-Host "    catalog : $expectedSha"
            Write-Host "    artefact: $actualSha"
            $failed++
            continue
        }
        Write-Host "  sha256 verified against catalog: $($actualSha.Substring(0,16))..." -ForegroundColor Green

        $targetJson = Join-Path $stage 'target.json'
        if (-not (Test-Path -LiteralPath $targetJson)) {
            Write-Host "  SKIP - $sourceTag has no target.json" -ForegroundColor Yellow
            $skipped++
            continue
        }

        $upload = @($assetPath, $targetJson)
        if (-not $Execute) {
            Write-Host "  DRY RUN - would publish: " -NoNewline
            Write-Host (($upload | ForEach-Object { Split-Path $_ -Leaf }) -join ', ')
            continue
        }

        $notes = "Built artefacts for $sourceRepo@$sourceTag. Source is not published here."
        gh release create $distTag --repo $DistRepo --title "$productId $sourceTag" --notes $notes @upload *> $null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  published $distTag" -ForegroundColor Green
            $done++
        } else {
            Write-Host "  FAILED to create $distTag" -ForegroundColor Red
            $failed++
        }
    }
}

Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host ("-" * 60)
if ($Execute) {
    Write-Host "planned=$planned published=$done skipped=$skipped failed=$failed"
} else {
    Write-Host "DRY RUN - planned=$planned skipped=$skipped failed=$failed (nothing was written)"
    Write-Host "Re-run with -Execute to publish."
}
if ($failed -gt 0) { exit 1 }
