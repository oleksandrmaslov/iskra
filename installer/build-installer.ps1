# Builds a release installer for Iskra.
#
# Steps:
#   1. Publish the WPF app as a single-file, self-contained Windows-x64 exe.
#   2. Publish the CLI as a single-file, self-contained Windows-x64 exe.
#   3. Add required WiX extensions if not already present.
#   4. Run wix to compile installer/Product.wxs into an app .msi.
#   5. Remove large publish/bin intermediates unless -KeepPublishOutput is set.
#   6. Download/cache the pinned Arm GNU Toolchain MSI if needed.
#   7. Run wix to compile installer/Bundle.wxs into a single setup .exe
#      that checks prerequisites, chains the Arm toolchain MSI, and the Iskra MSI.
#
# Outputs:
#   installer/out/Iskra-<ver>-x64.msi
#   installer/out/Iskra-<ver>-setup-x64.exe
#   installer/out/Iskra-<ver>-preinstall-check.ps1
#
# Requires:
#   * .NET 8 SDK on PATH (or LOCALAPPDATA install; script handles both)
#   * `wix` global dotnet tool (install once: `dotnet tool install --global wix`)
#   * curl.exe (built into supported Windows 10/11 images)
#
# Usage:
#   pwsh ./installer/build-installer.ps1
#   pwsh ./installer/build-installer.ps1 -Version 1.2.3
#   pwsh ./installer/build-installer.ps1 -KeepPublishOutput
#   pwsh ./installer/build-installer.ps1 -ArmToolchainInstaller C:\deps\arm-gnu-toolchain-15.2.rel1-mingw-w64-i686-arm-none-eabi.msi

param(
    [string] $Version = "1.0.0",
    [string] $Configuration = "Release",
    [string] $Runtime = "win-x64",
    [string] $ArmToolchainVersion = "15.2.rel1",
    [string] $ArmToolchainFileName = "arm-gnu-toolchain-15.2.rel1-mingw-w64-i686-arm-none-eabi.msi",
    [string] $ArmToolchainUrl = "https://developer.arm.com/-/media/Files/downloads/gnu/15.2.rel1/binrel/arm-gnu-toolchain-15.2.rel1-mingw-w64-i686-arm-none-eabi.msi",
    [string] $ArmToolchainSha256 = "6606feaf791fdbe83f8c6cfbb7db6429f778fb3444ea21b80a7c4d28f84f5dc8",
    [string] $ArmToolchainInstaller = "",
    [switch] $KeepPublishOutput
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

# Ensure dotnet + wix tool are on PATH for this session.
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH;$env:USERPROFILE\.dotnet\tools"

function Get-Sha256([string] $Path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

function Test-ExpectedHash([string] $Path, [string] $ExpectedSha256) {
    if ([string]::IsNullOrWhiteSpace($ExpectedSha256)) { return $true }
    return (Get-Sha256 $Path) -eq $ExpectedSha256.ToLowerInvariant()
}

function Add-WixExtension([string] $ExtensionId) {
    & wix extension list --global 2>&1 | Out-String | Set-Variable -Name extList
    if ($LASTEXITCODE -ne 0) { throw "wix extension list failed (exit $LASTEXITCODE)" }

    if ($extList -notmatch [regex]::Escape($ExtensionId)) {
        wix extension add --global "$ExtensionId/5.0.2" | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "wix extension add $ExtensionId failed (exit $LASTEXITCODE)" }
    } else {
        Write-Host "  ($ExtensionId already installed)"
    }
}

function Invoke-CurlDownload([string] $Url, [string] $Destination) {
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($null -eq $curl) {
        throw "curl.exe not found; download $Url manually to $Destination"
    }

    & $curl.Source -L --fail --retry 3 --retry-delay 2 --output $Destination $Url | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "download failed: $Url (exit $LASTEXITCODE)" }
}

function Resolve-ArmToolchainInstaller {
    if ([string]::IsNullOrWhiteSpace($ArmToolchainInstaller)) {
        $depsDir = Join-Path $PSScriptRoot "deps"
        New-Item -ItemType Directory -Force -Path $depsDir | Out-Null
        $ArmToolchainInstaller = Join-Path $depsDir $ArmToolchainFileName
    }

    if (Test-Path -LiteralPath $ArmToolchainInstaller) {
        if (Test-ExpectedHash $ArmToolchainInstaller $ArmToolchainSha256) {
            Write-Host "  using cached $ArmToolchainFileName"
            return (Resolve-Path -LiteralPath $ArmToolchainInstaller).Path
        }

        Write-Host "  cached toolchain MSI hash mismatch; re-downloading" -ForegroundColor Yellow
        Remove-Item -LiteralPath $ArmToolchainInstaller -Force
    }

    if ([string]::IsNullOrWhiteSpace($ArmToolchainUrl)) {
        throw "Arm toolchain MSI missing and ArmToolchainUrl is empty: $ArmToolchainInstaller"
    }

    $tmp = "$ArmToolchainInstaller.tmp"
    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    Invoke-CurlDownload $ArmToolchainUrl $tmp

    if (-not (Test-ExpectedHash $tmp $ArmToolchainSha256)) {
        $actual = Get-Sha256 $tmp
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        throw "Arm toolchain MSI SHA-256 mismatch. Expected $ArmToolchainSha256, got $actual"
    }

    Move-Item -LiteralPath $tmp -Destination $ArmToolchainInstaller -Force
    return (Resolve-Path -LiteralPath $ArmToolchainInstaller).Path
}

function Remove-GeneratedDirectory([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }

    $full = (Resolve-Path -LiteralPath $Path).Path
    $root = (Resolve-Path -LiteralPath $repoRoot).Path.TrimEnd('\')
    if (-not $full.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "refusing to remove path outside repo: $full"
    }

    Remove-Item -LiteralPath $full -Recurse -Force
}

function Remove-PublishIntermediates {
    if ($KeepPublishOutput) {
        Write-Host "  keeping publish/bin intermediates"
        return
    }

    Write-Host "  removing publish/bin intermediates before bundling"
    Remove-GeneratedDirectory $publishDir
    Remove-GeneratedDirectory $cliPublishDir
    Remove-GeneratedDirectory (Join-Path $repoRoot "src\Iskra.Wpf\bin")
    Remove-GeneratedDirectory (Join-Path $repoRoot "src\Iskra.Cli\bin")
    Remove-GeneratedDirectory (Join-Path $repoRoot "src\Iskra.Core\bin")
}

Write-Host "[1/7] dotnet publish WPF (single-file, self-contained, $Runtime)" -ForegroundColor Cyan
$publishDir = Join-Path $repoRoot "publish\$Runtime"
dotnet publish src/Iskra.Wpf `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:Version=$Version `
    -o $publishDir | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet publish WPF failed (exit $LASTEXITCODE)" }

if (-not (Test-Path (Join-Path $publishDir "Iskra.exe"))) {
    throw "publish completed but Iskra.exe not at $publishDir"
}

Write-Host "[2/7] dotnet publish CLI (single-file, self-contained, $Runtime)" -ForegroundColor Cyan
$cliPublishDir = Join-Path $repoRoot "publish\cli-$Runtime"
dotnet publish src/Iskra.Cli `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:Version=$Version `
    -o $cliPublishDir | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet publish CLI failed (exit $LASTEXITCODE)" }

if (-not (Test-Path (Join-Path $cliPublishDir "Iskra.Cli.exe"))) {
    throw "publish completed but Iskra.Cli.exe not at $cliPublishDir"
}

Write-Host "[3/7] WiX extensions (idempotent)" -ForegroundColor Cyan
# Pin extensions to the matching WiX v5 line. v7-line extensions do not unpack
# into the v5 layout (warning WIX6101).
Add-WixExtension "WixToolset.UI.wixext"
Add-WixExtension "WixToolset.BootstrapperApplications.wixext"
Add-WixExtension "WixToolset.Util.wixext"

Write-Host "[4/7] wix build MSI -> installer/out/Iskra-$Version-x64.msi" -ForegroundColor Cyan
$outDir = Join-Path $PSScriptRoot "out"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$msiPath = Join-Path $outDir "Iskra-$Version-x64.msi"
$preinstallCheckPath = Join-Path $outDir "Iskra-$Version-preinstall-check.ps1"
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "check-station.ps1") -Destination $preinstallCheckPath -Force

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

Write-Host "[5/7] trim intermediate publish output" -ForegroundColor Cyan
Remove-PublishIntermediates

Write-Host "[6/7] Arm GNU Toolchain $ArmToolchainVersion MSI" -ForegroundColor Cyan
$armToolchainMsi = Resolve-ArmToolchainInstaller

Write-Host "[7/7] wix build bundle -> installer/out/Iskra-$Version-setup-x64.exe" -ForegroundColor Cyan
$bundlePath = Join-Path $outDir "Iskra-$Version-setup-x64.exe"

wix build `
    (Join-Path $PSScriptRoot "Bundle.wxs") `
    -d "AppVersion=$Version" `
    -d "IskraMsi=$msiPath" `
    -d "ArmToolchainMsi=$armToolchainMsi" `
    -ext WixToolset.BootstrapperApplications.wixext `
    -ext WixToolset.Util.wixext `
    -arch x64 `
    -out $bundlePath | Out-Host
if ($LASTEXITCODE -ne 0) { throw "wix bundle build failed (exit $LASTEXITCODE)" }

Write-Host ""
Write-Host "[OK] Built installer bundle and app MSI" -ForegroundColor Green
Get-Item $bundlePath, $msiPath, $preinstallCheckPath | Select-Object FullName, Length, LastWriteTime
