# Builds a release MSI for Iskra.
#
# Steps:
#   1. Publish the WPF app as a single-file, self-contained Windows-x64 exe.
#   2. Publish the CLI as a single-file, self-contained Windows-x64 exe.
#   3. Add the WixUI extension if not already present.
#   4. Run wix to compile installer/Product.wxs into a .msi.
#
# Outputs:
#   publish/win-x64/Iskra.exe            (the WPF app)
#   publish/cli-win-x64/Iskra.Cli.exe    (the CLI)
#   installer/out/Iskra-<ver>-x64.msi
#
# Requires:
#   * .NET 8 SDK on PATH (or LOCALAPPDATA install — script handles both)
#   * `wix` global dotnet tool (install once: `dotnet tool install --global wix`)
#
# Usage:
#   pwsh ./installer/build-installer.ps1
#   pwsh ./installer/build-installer.ps1 -Version 1.2.3

param(
    [string] $Version = "1.0.0",
    [string] $Configuration = "Release",
    [string] $Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

# Ensure dotnet + wix tool are on PATH for this session.
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH;$env:USERPROFILE\.dotnet\tools"

Write-Host "[1/4] dotnet publish WPF (single-file, self-contained, $Runtime)" -ForegroundColor Cyan
$publishDir = Join-Path $repoRoot "publish\$Runtime"
dotnet publish src/Iskra.Wpf `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeAllContentForSelfExtract=true `
    -o $publishDir | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet publish WPF failed (exit $LASTEXITCODE)" }

if (-not (Test-Path (Join-Path $publishDir "Iskra.exe"))) {
    throw "publish completed but Iskra.exe not at $publishDir"
}

Write-Host "[2/4] dotnet publish CLI (single-file, self-contained, $Runtime)" -ForegroundColor Cyan
$cliPublishDir = Join-Path $repoRoot "publish\cli-$Runtime"
dotnet publish src/Iskra.Cli `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeAllContentForSelfExtract=true `
    -o $cliPublishDir | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet publish CLI failed (exit $LASTEXITCODE)" }

if (-not (Test-Path (Join-Path $cliPublishDir "Iskra.Cli.exe"))) {
    throw "publish completed but Iskra.Cli.exe not at $cliPublishDir"
}

Write-Host "[3/4] wix extension add WixToolset.UI.wixext/5.0.2 (idempotent)" -ForegroundColor Cyan
& wix extension list 2>&1 | Out-String | Set-Variable -Name extList
if ($extList -notmatch "WixToolset\.UI\.wixext") {
    # Pin the extension to the matching WiX v5 line. v7-line extensions
    # don't unpack into the v5 layout (warning WIX6101).
    wix extension add --global "WixToolset.UI.wixext/5.0.2" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "wix extension add failed (exit $LASTEXITCODE)" }
} else {
    Write-Host "  (already installed)"
}

Write-Host "[4/4] wix build -> installer/out/Iskra-$Version-x64.msi" -ForegroundColor Cyan
$outDir = Join-Path $PSScriptRoot "out"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$msiPath = Join-Path $outDir "Iskra-$Version-x64.msi"

wix build `
    (Join-Path $PSScriptRoot "Product.wxs") `
    -d "AppVersion=$Version" `
    -d "PublishDir=$publishDir" `
    -d "CliPublishDir=$cliPublishDir" `
    -d "SolutionDir=$repoRoot" `
    -ext WixToolset.UI.wixext `
    -arch x64 `
    -out $msiPath | Out-Host
if ($LASTEXITCODE -ne 0) { throw "wix build failed (exit $LASTEXITCODE)" }

Write-Host ""
Write-Host "[OK] Built $msiPath" -ForegroundColor Green
Get-Item $msiPath | Select-Object FullName, Length, LastWriteTime
