# Builds the Linux and macOS bundles for Iskra.
#
# These can be built on any supported host. When they are cross-published, the
# output is still explicitly labelled as not runtime-tested on its target OS.
# GitHub CI runs the same Core/Application/Avalonia test suites and CLI smoke
# test natively on every RID before a tagged build is considered releasable.
#
# WPF is Windows-only and is deliberately not part of these bundles.
#
# What ships per platform:
#   linux-x64 / linux-arm64
#               Iskra.Avalonia, Iskra.Cli, install/uninstall helpers, a desktop
#               entry, and a least-privilege udev rule for probe access.
#   osx-arm64   Iskra.app bundle (Apple Silicon) + Iskra.Cli
#   osx-x64     Iskra.app bundle (Intel) + Iskra.Cli
#
# Deliberately NOT produced here:
#   * .deb / .rpm - native packaging belongs in the release CI runners.
#   * .dmg - needs macOS hdiutil.
#   * Code signing and notarization - need a macOS host and an Apple Developer
#     ID. The macOS bundles are unsigned, so Gatekeeper will quarantine them.
#   * A bundled arm-none-eabi-gdb. Unlike the Windows setup EXE there is no
#     pinned toolchain here; the operator installs it from their package
#     manager or Homebrew.
#
# Usage:
#   pwsh ./installer/build-unix-bundles.ps1 -Version 2.2.1

param(
    [string] $Version = "2.2.1",
    [string] $Configuration = "Release",
    [string[]] $Runtimes = @("linux-x64", "linux-arm64", "osx-arm64", "osx-x64"),
    [switch] $AllowDirty,
    [switch] $RequireTag,
    [switch] $NativePackage
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$dotnet = $null
if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    $localDotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
    if (Test-Path -LiteralPath $localDotnet) { $dotnet = $localDotnet }
}
if ($null -eq $dotnet) {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -ne $dotnetCommand) { $dotnet = $dotnetCommand.Source }
}
if ($null -eq $dotnet) {
    throw "dotnet was not found; install the SDK pinned by global.json"
}

if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
    throw "Version must be SemVer-like (for example 2.2.0 or 2.2.0-rc.1): $Version"
}

$supportedRuntimes = @("linux-x64", "linux-arm64", "osx-arm64", "osx-x64")
foreach ($runtime in $Runtimes) {
    if ($runtime -notin $supportedRuntimes) {
        throw "unsupported runtime '$runtime'; expected one of: $($supportedRuntimes -join ', ')"
    }
}

$artifacts = Join-Path $repoRoot "artifacts"
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

$archiveHelper = Join-Path $PSScriptRoot "New-PortableTarGz.ps1"
if (-not (Test-Path -LiteralPath $archiveHelper)) {
    throw "portable archive helper is missing: $archiveHelper"
}

$gitCommit = (& git rev-parse --verify HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $gitCommit -notmatch '^[0-9a-f]{40}$') {
    throw "could not resolve the release commit"
}
$gitCommitShort = $gitCommit.Substring(0, 12)
$gitTimestamp = [long] ((& git show -s --format=%ct HEAD).Trim())
if ($LASTEXITCODE -ne 0 -or $gitTimestamp -le 0) {
    throw "could not resolve the release commit timestamp"
}
$archiveTimestamp = [DateTimeOffset]::FromUnixTimeSeconds($gitTimestamp)
$builtAt = $archiveTimestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ")
$bundleVersion = ($Version -split '-', 2)[0]
$gitState = if (@(& git status --porcelain).Count -gt 0) { "dirty" } else { "clean" }
if ($gitState -eq "dirty" -and -not $AllowDirty) {
    throw "release builds require a clean working tree; use -AllowDirty only for a labelled engineering build"
}
if ($RequireTag) {
    $expectedTag = "v$Version"
    $headTags = @(& git tag --points-at HEAD)
    if ($expectedTag -notin $headTags) {
        throw "release version $Version is not bound to tag $expectedTag at HEAD"
    }
}

Write-Host "[1/3] locked portable-project restore" -ForegroundColor Cyan
# Do not pass -r here. The committed lock files intentionally contain the full
# RuntimeIdentifiers matrix; narrowing Restore to one RID changes the evaluated
# graph and correctly triggers NU1004 in locked mode. Publish below selects one
# of the already-locked runtime graphs with -r and --no-restore.
foreach ($project in @("src/Iskra.Desktop", "src/Iskra.Cli")) {
    & $dotnet restore $project --locked-mode --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "locked restore failed for $project, exit $LASTEXITCODE"
    }
}

function Publish-Project([string] $Project, [string] $Runtime, [string] $Destination) {
    & $dotnet publish $Project `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        --no-restore `
        -p:PublishSingleFile=true `
        -p:IncludeAllContentForSelfExtract=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:Version=$Version `
        -o $Destination | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $Project ($Runtime) failed (exit $LASTEXITCODE)" }
}

# Written with LF and no BOM: these are consumed by Linux/macOS tooling that
# does not tolerate CRLF in a shebang line or a plist.
function Write-UnixText([string] $Path, [string] $Content) {
    $normalized = $Content -replace "`r`n", "`n"
    [IO.File]::WriteAllText($Path, $normalized, [Text.UTF8Encoding]::new($false))
}

function Remove-GeneratedDirectory([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $full = (Resolve-Path -LiteralPath $Path).Path
    $artifactRoot = (Resolve-Path -LiteralPath $artifacts).Path.TrimEnd('\', '/')
    if (-not $full.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "refusing to remove generated path outside artifacts: $full"
    }
    Remove-Item -LiteralPath $full -Recurse -Force
}

$built = @()

foreach ($runtime in $Runtimes) {
    Write-Host "[2/3] publishing $runtime" -ForegroundColor Cyan
    $isMac = $runtime.StartsWith("osx")
    $outName = "Iskra-$Version-$runtime"
    $outDir = Join-Path $artifacts $outName
    Remove-GeneratedDirectory $outDir
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null

    $stage = Join-Path $outDir ".stage"
    Publish-Project "src/Iskra.Desktop" $runtime (Join-Path $stage "gui")
    Publish-Project "src/Iskra.Cli" $runtime (Join-Path $stage "cli")

    $guiBinary = Join-Path $stage "gui\Iskra.Desktop"
    $cliBinary = Join-Path $stage "cli\Iskra.Cli"
    if (-not (Test-Path -LiteralPath $guiBinary)) { throw "missing published GUI binary for $runtime" }
    if (-not (Test-Path -LiteralPath $cliBinary)) { throw "missing published CLI binary for $runtime" }

    if ($isMac) {
        # Minimal but correct .app layout. Gatekeeper still quarantines it
        # because it is unsigned; the README explains the release-time fix.
        $app = Join-Path $outDir "Iskra.app"
        $macOsDir = Join-Path $app "Contents\MacOS"
        $resourcesDir = Join-Path $app "Contents\Resources"
        New-Item -ItemType Directory -Force -Path $macOsDir, $resourcesDir | Out-Null
        Copy-Item -LiteralPath $guiBinary -Destination (Join-Path $macOsDir "Iskra") -Force
        Copy-Item -LiteralPath (Join-Path $repoRoot "docs\iskra.png") -Destination (Join-Path $resourcesDir "iskra.png") -Force

        Write-UnixText (Join-Path $app "Contents\Info.plist") @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Iskra</string>
  <key>CFBundleDisplayName</key><string>Iskra</string>
  <key>CFBundleIdentifier</key><string>com.oleksandrmaslov.iskra</string>
  <key>CFBundleVersion</key><string>$bundleVersion</string>
  <key>CFBundleShortVersionString</key><string>$bundleVersion</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleExecutable</key><string>Iskra</string>
  <key>CFBundleIconFile</key><string>iskra.png</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>Copyright © 2026 Iskra contributors</string>
</dict>
</plist>
"@
        Copy-Item -LiteralPath $cliBinary -Destination (Join-Path $outDir "Iskra.Cli") -Force
    }
    else {
        Copy-Item -LiteralPath $guiBinary -Destination (Join-Path $outDir "Iskra.Avalonia") -Force
        Copy-Item -LiteralPath $cliBinary -Destination (Join-Path $outDir "Iskra.Cli") -Force

        Write-UnixText (Join-Path $outDir "iskra.desktop") @"
[Desktop Entry]
Type=Application
Name=Iskra
Comment=Black Magic Probe firmware flasher
Exec=/opt/iskra/Iskra.Avalonia
Icon=/opt/iskra/iskra.png
StartupWMClass=Iskra
Terminal=false
Categories=Development;Electronics;
Keywords=firmware;flash;debugger;microcontroller;BMP;
"@
        Copy-Item -LiteralPath (Join-Path $repoRoot "docs\iskra.png") -Destination (Join-Path $outDir "iskra.png") -Force

        # TAG+=uaccess grants an ACL only to the active local session. Keep the
        # device non-world-writable; headless stations should use a dedicated
        # udev group policy rather than changing this to MODE=0666.
        Write-UnixText (Join-Path $outDir "99-black-magic-probe.rules") @"
# Black Magic Probe - grant the console user access to both CDC interfaces.
# Install: sudo cp 99-black-magic-probe.rules /etc/udev/rules.d/
#          sudo udevadm control --reload-rules && sudo udevadm trigger
# Reconnect the probe after installation.
SUBSYSTEM=="tty", ATTRS{idVendor}=="1d50", ATTRS{idProduct}=="6018", MODE="0660", TAG+="uaccess"
SUBSYSTEM=="usb", ATTRS{idVendor}=="1d50", ATTRS{idProduct}=="6018", MODE="0660", TAG+="uaccess"
"@

        Write-UnixText (Join-Path $outDir "install.sh") @'
#!/bin/sh
set -eu

if [ "$(id -u)" -ne 0 ]; then
  echo "Run this installer as root: sudo ./install.sh" >&2
  exit 1
fi

SOURCE_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
install -d -m 0755 /opt/iskra /usr/share/applications /usr/share/icons/hicolor/256x256/apps /etc/udev/rules.d /usr/local/bin
install -m 0755 "$SOURCE_DIR/Iskra.Avalonia" /opt/iskra/Iskra.Avalonia
install -m 0755 "$SOURCE_DIR/Iskra.Cli" /opt/iskra/Iskra.Cli
install -m 0644 "$SOURCE_DIR/iskra.png" /opt/iskra/iskra.png
install -m 0644 "$SOURCE_DIR/iskra.png" /usr/share/icons/hicolor/256x256/apps/iskra.png
install -m 0644 "$SOURCE_DIR/iskra.desktop" /usr/share/applications/iskra.desktop
install -m 0644 "$SOURCE_DIR/99-black-magic-probe.rules" /etc/udev/rules.d/99-black-magic-probe.rules
ln -sfn /opt/iskra/Iskra.Cli /usr/local/bin/iskra-cli

if command -v udevadm >/dev/null 2>&1; then
  udevadm control --reload-rules
  udevadm trigger --subsystem-match=tty --action=add || true
fi
if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database /usr/share/applications || true
fi
echo "Iskra installed. Reconnect the Black Magic Probe, then run: iskra-cli --doctor"
'@

        Write-UnixText (Join-Path $outDir "uninstall.sh") @'
#!/bin/sh
set -eu

if [ "$(id -u)" -ne 0 ]; then
  echo "Run this uninstaller as root: sudo ./uninstall.sh" >&2
  exit 1
fi

rm -f /usr/local/bin/iskra-cli
rm -f /usr/share/applications/iskra.desktop
rm -f /usr/share/icons/hicolor/256x256/apps/iskra.png
rm -f /etc/udev/rules.d/99-black-magic-probe.rules
rm -rf /opt/iskra
if command -v udevadm >/dev/null 2>&1; then
  udevadm control --reload-rules
fi
echo "Iskra application files removed. Operator settings and flash logs were preserved."
'@
    }

    $examples = Join-Path $outDir "examples"
    New-Item -ItemType Directory -Force -Path $examples | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot "examples\catalog.json") -Destination $examples -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot "examples\catalog.json.sig") -Destination $examples -Force

    Remove-GeneratedDirectory $stage

    $macNotes = if ($NativePackage) { @"
This bundle is being assembled by the native macOS release builder. Its final
signature/notarization status is recorded in BUILD-METADATA.json and in the
native checksum manifest; the builder fails closed for unsigned tag releases.

Run:  open Iskra.app        (or ./Iskra.app/Contents/MacOS/Iskra from a terminal)

Probe discovery binds each /dev/cu.usbmodem endpoint to IOKit USB VID/PID,
interface number, serial/location identity, and refuses unidentified endpoints.
Production acceptance still requires real-probe reconnect/contention HIL on
each supported macOS architecture.
"@ } else { @"
UNSIGNED BUILD. Signing, notarization, and DMG creation require a macOS release
runner and an Apple Developer ID. Gatekeeper can therefore quarantine this
engineering archive; do not weaken a production station to run it.

Run:  open Iskra.app        (or ./Iskra.app/Contents/MacOS/Iskra from a terminal)

Probe discovery binds each /dev/cu.usbmodem endpoint to IOKit USB VID/PID,
interface number, serial/location identity, and refuses unidentified endpoints.
The native adapter is fixture-tested but still needs real-probe HIL on each
supported macOS architecture.
"@ }

    $linuxNotes = @"
Install:  sudo ./install.sh
Run without installing:  ./Iskra.Avalonia

Probe access needs the bundled least-privilege udev rule:
  sudo cp 99-black-magic-probe.rules /etc/udev/rules.d/
  sudo udevadm control --reload-rules && sudo udevadm trigger

The installer uses /opt/iskra, adds a menu entry and iskra-cli symlink, and
leaves operator settings and SQLite logs untouched when uninstalling.
"@

    # Precomputed: PowerShell 5.1 mis-parses a here-string containing a
    # subexpression that itself contains double quotes.
    $platformNotes = if ($isMac) { $macNotes } else { $linuxNotes }
    $includedList = if ($isMac) {
        "  Iskra.app        Avalonia operator UI"
    } else {
        @(
            "  Iskra.Avalonia   Avalonia operator UI"
            "  install.sh / uninstall.sh   system integration helpers"
            "  iskra.desktop    menu entry"
            "  99-black-magic-probe.rules   udev access rule"
        ) -join "`n"
    }

    Write-UnixText (Join-Path $outDir "README.txt") @"
Iskra $Version - $runtime

$platformNotes

CROSS-PUBLISHED ENGINEERING BUILD. Native Windows/Linux/macOS CI tests the same
commit, but this archive itself has not flashed hardware. Production acceptance
still requires a clean target-OS station and the documented BMP HIL matrix.

REQUIRES an ARM-capable GDB, which is NOT bundled. Unlike the Windows setup EXE
there is no pinned toolchain here. Install it first:
  Debian/Ubuntu   sudo apt install gdb-multiarch
  Fedora          sudo dnf install arm-none-eabi-gdb
  macOS           brew install --cask gcc-arm-embedded
Then check the station with:  ./Iskra.Cli --doctor

PRIVATE REMOTE FIRMWARE uses the operating system's encrypted credential store:
macOS Keychain or Linux Secret Service. On Linux, install secret-tool (usually
the libsecret-tools/libsecret package) and make sure an unlocked Secret Service
is available in the operator session. If the secure store is absent, Iskra
fails closed; it never writes OAuth tokens to a plaintext file.

Included:
  Iskra.Cli        command-line flasher and diagnostics
$includedList
  examples/        signed sample catalog

Self-contained, single-file; no .NET runtime required.
Commit:    $gitCommitShort ($gitState working tree)
Built UTC: $builtAt

Unsigned engineering build. Not a factory release.
"@

    $metadata = [ordered]@{
        schema_version = 1
        product = "Iskra"
        version = $Version
        runtime = $runtime
        commit = $gitCommit
        working_tree = $gitState
        built_at_utc = $builtAt
        sdk = (& $dotnet --version).Trim()
        archive_format = "pax+gzip"
        hardware_acceptance = "required"
        signed = $false
    } | ConvertTo-Json
    Write-UnixText (Join-Path $outDir "BUILD-METADATA.json") $metadata

    $files = Get-ChildItem -LiteralPath $outDir -Recurse -File |
        Sort-Object { $_.FullName.Substring($outDir.Length + 1).Replace('\', '/') }
    $lines = foreach ($f in $files) {
        $rel = $f.FullName.Substring($outDir.Length + 1).Replace('\', '/')
        "$((Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $rel"
    }
    [IO.File]::WriteAllLines((Join-Path $outDir "SHA256SUMS.txt"), $lines, [Text.UTF8Encoding]::new($false))

    $archive = Join-Path $artifacts "$outName.tar.gz"
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
    $executables = if ($isMac) {
        @("Iskra.app/Contents/MacOS/Iskra", "Iskra.Cli")
    } else {
        @("Iskra.Avalonia", "Iskra.Cli", "install.sh", "uninstall.sh")
    }
    & $archiveHelper `
        -SourceDirectory $outDir `
        -DestinationPath $archive `
        -RootName $outName `
        -ExecutablePaths $executables `
        -Timestamp $archiveTimestamp | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $archive)) {
        throw "portable archive creation failed for $runtime"
    }
    $built += [pscustomobject]@{
        Runtime = $runtime
        Folder  = $outDir
        Archive = $archive
        SizeMB  = [math]::Round((Get-Item $archive).Length / 1MB, 1)
    }
}

$releaseChecksums = Join-Path $artifacts "Iskra-$Version-portable-SHA256SUMS.txt"
$releaseChecksumLines = foreach ($item in $built) {
    "$((Get-FileHash -LiteralPath $item.Archive -Algorithm SHA256).Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($item.Archive))"
}
[IO.File]::WriteAllLines($releaseChecksums, $releaseChecksumLines, [Text.UTF8Encoding]::new($false))

Write-Host ""
$buildLabel = if ($NativePackage) { "native package staging" } else { "UNVERIFIED cross-published bundles" }
Write-Host "[3/3] done - $buildLabel" -ForegroundColor Yellow
Write-Host "      Target-OS hardware-in-the-loop acceptance remains required." -ForegroundColor Yellow
$built | Format-Table Runtime, SizeMB, Archive -AutoSize
Write-Host "Checksums: $releaseChecksums"
