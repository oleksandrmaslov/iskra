# Builds the Linux and macOS bundles for Iskra.
#
# These are cross-published from Windows. .NET cross-publishes managed code and
# ships Avalonia's native libraries per RID, so the binaries are producible
# here - but they CANNOT BE EXECUTED OR SMOKE-TESTED on this build host. Treat
# every output as unverified until it has been run on the target OS.
#
# WPF is Windows-only and is deliberately not part of these bundles.
#
# What ships per platform:
#   linux-x64   Iskra.Avalonia, Iskra.Cli, a .desktop entry, and the udev rule
#               that grants non-root access to the probe.
#   osx-arm64   Iskra.app bundle (Apple Silicon) + Iskra.Cli
#   osx-x64     Iskra.app bundle (Intel) + Iskra.Cli
#
# Deliberately NOT produced here:
#   * .deb / .rpm - need dpkg-deb / rpmbuild, which do not exist on this host.
#   * .dmg - needs macOS hdiutil.
#   * Code signing and notarization - need a macOS host and an Apple Developer
#     ID. The macOS bundles are unsigned, so Gatekeeper will quarantine them.
#   * A bundled arm-none-eabi-gdb. Unlike the Windows setup EXE there is no
#     pinned toolchain here; the operator installs it from their package
#     manager or Homebrew.
#
# Usage:
#   pwsh ./installer/build-unix-bundles.ps1 -Version 2.1.0

param(
    [string] $Version = "1.0.0",
    [string] $Configuration = "Release",
    [string[]] $Runtimes = @("linux-x64", "osx-arm64", "osx-x64")
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
$dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path -LiteralPath $dotnet)) {
    throw "The repository SDK host was not found at $dotnet"
}

$artifacts = Join-Path $repoRoot "artifacts"
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

Write-Host "[1/3] locked solution restore" -ForegroundColor Cyan
& $dotnet restore Iskra.sln --locked-mode --nologo | Out-Host
if ($LASTEXITCODE -ne 0) { throw "locked solution restore failed (exit $LASTEXITCODE)" }

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

$built = @()

foreach ($runtime in $Runtimes) {
    Write-Host "[2/3] publishing $runtime" -ForegroundColor Cyan
    $isMac = $runtime.StartsWith("osx")
    $outName = "Iskra-$Version-$runtime"
    $outDir = Join-Path $artifacts $outName
    if (Test-Path -LiteralPath $outDir) { Remove-Item -LiteralPath $outDir -Recurse -Force }
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
  <key>CFBundleVersion</key><string>$Version</string>
  <key>CFBundleShortVersionString</key><string>$Version</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleExecutable</key><string>Iskra</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
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
Terminal=false
Categories=Development;Electronics;
"@
        Copy-Item -LiteralPath (Join-Path $repoRoot "docs\iskra.png") -Destination (Join-Path $outDir "iskra.png") -Force

        # Without this rule the probe is root-only and the app reports the
        # port as missing rather than as a permission problem.
        Write-UnixText (Join-Path $outDir "99-black-magic-probe.rules") @"
# Black Magic Probe - grant the console user access to both CDC interfaces.
# Install: sudo cp 99-black-magic-probe.rules /etc/udev/rules.d/
#          sudo udevadm control --reload-rules && sudo udevadm trigger
# Then log out and back in so the new group membership applies.
SUBSYSTEM=="tty", ATTRS{idVendor}=="1d50", ATTRS{idProduct}=="6018", MODE="0666", TAG+="uaccess"
SUBSYSTEM=="usb", ATTRS{idVendor}=="1d50", ATTRS{idProduct}=="6018", MODE="0666", TAG+="uaccess"
"@
    }

    $examples = Join-Path $outDir "examples"
    New-Item -ItemType Directory -Force -Path $examples | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot "examples\catalog.json") -Destination $examples -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot "examples\catalog.json.sig") -Destination $examples -Force

    Remove-Item -LiteralPath $stage -Recurse -Force

    $gitCommit = (& git rev-parse --short=12 HEAD).Trim()
    $gitState = if (@(& git status --porcelain).Count -gt 0) { "dirty" } else { "clean" }
    $builtAt = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")

    $macNotes = @"
FIRST, RESTORE THE EXECUTABLE BIT. This zip was produced on Windows, which does
not carry Unix permissions, so the app will not launch until you run:
  chmod +x Iskra.app/Contents/MacOS/Iskra Iskra.Cli

UNSIGNED BUILD. macOS will also quarantine it on first launch:
  xattr -dr com.apple.quarantine Iskra.app
Signing and notarization need a macOS host and an Apple Developer ID; neither
was available at build time.

Run:  open Iskra.app        (or ./Iskra.app/Contents/MacOS/Iskra from a terminal)

Probe discovery reads /dev/cu.usbmodem*, using the trailing interface digit
(1 = GDB, 3 = UART). The naming convention is unit-tested, but it has never
been run against a probe on real hardware.
"@

    $linuxNotes = @"
FIRST, RESTORE THE EXECUTABLE BIT. This zip was produced on Windows, which does
not carry Unix permissions:
  chmod +x Iskra.Avalonia Iskra.Cli

Run:  ./Iskra.Avalonia

Probe access needs the bundled udev rule, otherwise the port is root-only and
the app simply reports no probe:
  sudo cp 99-black-magic-probe.rules /etc/udev/rules.d/
  sudo udevadm control --reload-rules && sudo udevadm trigger

For a menu entry, install to /opt/iskra and copy iskra.desktop into
/usr/share/applications/ (the Exec path assumes /opt/iskra).
"@

    # Precomputed: PowerShell 5.1 mis-parses a here-string containing a
    # subexpression that itself contains double quotes.
    $platformNotes = if ($isMac) { $macNotes } else { $linuxNotes }
    $includedList = if ($isMac) {
        "  Iskra.app        Avalonia operator UI"
    } else {
        @(
            "  Iskra.Avalonia   Avalonia operator UI"
            "  iskra.desktop    menu entry"
            "  99-black-magic-probe.rules   udev access rule"
        ) -join "`n"
    }

    Write-UnixText (Join-Path $outDir "README.txt") @"
Iskra $Version - $runtime

$platformNotes

NOT VERIFIED. This bundle was cross-published from a Windows host and could not
be executed there. Nothing in it has been launched, and no flash has been
performed on this platform. Treat the first run as the test.

REQUIRES arm-none-eabi-gdb, which is NOT bundled. Unlike the Windows setup EXE
there is no pinned toolchain here. Install it first:
  Debian/Ubuntu   sudo apt install gdb-arm-none-eabi   (or gcc-arm-none-eabi)
  Fedora          sudo dnf install arm-none-eabi-gdb
  macOS           brew install --cask gcc-arm-embedded
Then check the station with:  ./Iskra.Cli --doctor

REMOTE FIRMWARE DOES NOT WORK ON THIS PLATFORM. Downloading firmware from
GitHub needs an encrypted credential store, and only Windows DPAPI is
implemented. The app fails closed rather than writing a token in plaintext, so
any catalog release whose source is a GitHub asset will refuse to flash here.
Local catalogs and sideload directories work normally. If your catalog uses
remote releases, this build can browse and check the station but not flash.

Included:
  Iskra.Cli        command-line flasher and diagnostics
$includedList
  examples/        signed sample catalog

Self-contained, single-file; no .NET runtime required.
Commit:    $gitCommit ($gitState working tree)
Built UTC: $builtAt

Unsigned engineering build. Not a factory release.
"@

    $files = Get-ChildItem -LiteralPath $outDir -Recurse -File
    $lines = foreach ($f in $files) {
        $rel = $f.FullName.Substring($outDir.Length + 1).Replace('\', '/')
        "$((Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $rel"
    }
    [IO.File]::WriteAllLines((Join-Path $outDir "SHA256SUMS.txt"), $lines, [Text.UTF8Encoding]::new($false))

    $zip = Join-Path $artifacts "$outName.zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zip -CompressionLevel Optimal
    $built += [pscustomobject]@{
        Runtime = $runtime
        Folder  = $outDir
        Zip     = $zip
        ZipMB   = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    }
}

Write-Host ""
Write-Host "[3/3] done - UNVERIFIED cross-published bundles" -ForegroundColor Yellow
Write-Host "      They were not executed on this host. First run on the target OS is the test." -ForegroundColor Yellow
$built | Format-Table Runtime, ZipMB, Zip -AutoSize
