# Builds the Iskra Avalonia installer.
#
# This is the cross-platform frontend packaged to install BESIDE the production
# WPF station, not over it: separate UpgradeCode, separate install folder,
# separate Start Menu entry. Installing or removing it cannot disturb a WPF
# station that is currently flashing production.
#
# Steps:
#   1. Restore the committed package locks without narrowing the RID matrix.
#   2. Publish the Avalonia app as a single-file, self-contained Windows-x64 exe.
#   3. Publish the CLI the same way, so this product is independently usable
#      for --doctor and --login on a station that never got the WPF install.
#   4. Add required WiX extensions if not already present.
#   5. Run wix to compile installer/Product.Avalonia.wxs into an app .msi.
#   6. Remove large publish/bin intermediates unless -KeepPublishOutput is set.
#   7. Download/cache the pinned Arm GNU Toolchain MSI if needed.
#   8. Run wix to compile installer/Bundle.Avalonia.wxs into a single setup .exe
#      that checks prerequisites, chains the Arm toolchain MSI, then Iskra Avalonia.
#
# Outputs:
#   installer/out/Iskra-Avalonia-<ver>-x64.msi
#   installer/out/Iskra-Avalonia-<ver>-setup-x64.exe
#   installer/out/Iskra-Avalonia-<ver>-SHA256SUMS.txt
#
# Requires:
#   * .NET SDK 10.0.301 on PATH (or LOCALAPPDATA install; `global.json` pins it)
#   * `wix` global dotnet tool (install once: `dotnet tool install --global wix`)
#   * curl.exe (built into supported Windows 10/11 images)
#
# Usage:
#   pwsh ./installer/build-avalonia-installer.ps1
#   pwsh ./installer/build-avalonia-installer.ps1 -Version 2.1.0

param(
    [string] $Version = "1.0.0",
    [string] $Configuration = "Release",
    [string] $Runtime = "win-x64",
    [string] $ArmToolchainInstaller = "",
    [switch] $KeepPublishOutput
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

# Toolchain version/URL/hash come from the shared pin file so the two setup
# EXEs can never ship different compilers under the same claim.
. (Join-Path $PSScriptRoot "arm-toolchain.pins.ps1")

$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH;$env:USERPROFILE\.dotnet\tools"
$dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path -LiteralPath $dotnet)) {
    throw "The repository SDK host was not found at $dotnet"
}

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

Write-Host "[1/8] locked solution restore" -ForegroundColor Cyan
& $dotnet restore Iskra.sln --locked-mode --nologo | Out-Host
if ($LASTEXITCODE -ne 0) { throw "locked solution restore failed (exit $LASTEXITCODE)" }

Write-Host "[2/8] dotnet publish Avalonia (single-file, self-contained, $Runtime)" -ForegroundColor Cyan
$publishDir = Join-Path $repoRoot "publish\avalonia-$Runtime"
& $dotnet publish src/Iskra.Desktop `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    --no-restore `
    -p:PublishSingleFile=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:Version=$Version `
    -o $publishDir | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet publish Avalonia failed (exit $LASTEXITCODE)" }

# The project emits Iskra.Desktop.exe; the shipped product name is Iskra.Avalonia.exe.
$publishedExe = Join-Path $publishDir "Iskra.Desktop.exe"
if (-not (Test-Path -LiteralPath $publishedExe)) {
    throw "publish completed but Iskra.Desktop.exe not at $publishDir"
}
$brandedExe = Join-Path $publishDir "Iskra.Avalonia.exe"
Move-Item -LiteralPath $publishedExe -Destination $brandedExe -Force

Write-Host "[3/8] dotnet publish CLI (single-file, self-contained, $Runtime)" -ForegroundColor Cyan
$cliPublishDir = Join-Path $repoRoot "publish\avalonia-cli-$Runtime"
& $dotnet publish src/Iskra.Cli `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    --no-restore `
    -p:PublishSingleFile=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:Version=$Version `
    -o $cliPublishDir | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet publish CLI failed (exit $LASTEXITCODE)" }

if (-not (Test-Path (Join-Path $cliPublishDir "Iskra.Cli.exe"))) {
    throw "publish completed but Iskra.Cli.exe not at $cliPublishDir"
}

Write-Host "[4/8] WiX extensions (idempotent)" -ForegroundColor Cyan
Add-WixExtension "WixToolset.UI.wixext"
Add-WixExtension "WixToolset.BootstrapperApplications.wixext"
Add-WixExtension "WixToolset.Util.wixext"

Write-Host "[5/8] wix build MSI -> installer/out/Iskra-Avalonia-$Version-x64.msi" -ForegroundColor Cyan
$outDir = Join-Path $PSScriptRoot "out"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$msiPath = Join-Path $outDir "Iskra-Avalonia-$Version-x64.msi"

wix build `
    (Join-Path $PSScriptRoot "Product.Avalonia.wxs") `
    -d "AppVersion=$Version" `
    -d "AvaloniaPublishDir=$publishDir" `
    -d "CliPublishDir=$cliPublishDir" `
    -d "SolutionDir=$repoRoot" `
    -ext WixToolset.UI.wixext `
    -arch x64 `
    -out $msiPath | Out-Host
if ($LASTEXITCODE -ne 0) { throw "wix build failed (exit $LASTEXITCODE)" }

Write-Host "[6/8] trim intermediate publish output" -ForegroundColor Cyan
if ($KeepPublishOutput) {
    Write-Host "  keeping publish/bin intermediates"
} else {
    Write-Host "  removing publish/bin intermediates before bundling"
    Remove-GeneratedDirectory $publishDir
    Remove-GeneratedDirectory $cliPublishDir
    Remove-GeneratedDirectory (Join-Path $repoRoot "src\Iskra.Desktop\bin")
    Remove-GeneratedDirectory (Join-Path $repoRoot "src\Iskra.Cli\bin")
}

Write-Host "[7/8] Arm GNU Toolchain $ArmToolchainVersion MSI" -ForegroundColor Cyan
$armToolchainMsi = Resolve-ArmToolchainInstaller

Write-Host "[8/8] wix build bundle -> installer/out/Iskra-Avalonia-$Version-setup-x64.exe" -ForegroundColor Cyan
$bundlePath = Join-Path $outDir "Iskra-Avalonia-$Version-setup-x64.exe"

wix build `
    (Join-Path $PSScriptRoot "Bundle.Avalonia.wxs") `
    -d "AppVersion=$Version" `
    -d "IskraMsi=$msiPath" `
    -d "ArmToolchainMsi=$armToolchainMsi" `
    -d "SolutionDir=$repoRoot" `
    -ext WixToolset.BootstrapperApplications.wixext `
    -ext WixToolset.Util.wixext `
    -arch x64 `
    -out $bundlePath | Out-Host
if ($LASTEXITCODE -ne 0) { throw "wix bundle build failed (exit $LASTEXITCODE)" }

$checksumPath = Join-Path $outDir "Iskra-Avalonia-$Version-SHA256SUMS.txt"
$checksumLines = foreach ($path in @($bundlePath, $msiPath)) {
    "$(Get-Sha256 $path)  $([IO.Path]::GetFileName($path))"
}
[IO.File]::WriteAllLines(
    $checksumPath,
    $checksumLines,
    [Text.UTF8Encoding]::new($false))

Write-Host ""
Write-Host "[OK] Built Iskra Avalonia setup EXE and MSI" -ForegroundColor Green
Write-Host "     Installs beside the WPF product; it does not upgrade or remove it." -ForegroundColor Green
Get-Item $bundlePath, $msiPath, $checksumPath |
    Select-Object FullName, Length, LastWriteTime
