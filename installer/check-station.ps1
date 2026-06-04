# Pre-install / station readiness checklist for Iskra factory PCs.
#
# Typical use before installing:
#   powershell.exe -ExecutionPolicy Bypass -File .\Iskra-1.2.3-preinstall-check.ps1
#
# Stricter bench-ready check:
#   powershell.exe -ExecutionPolicy Bypass -File .\Iskra-1.2.3-preinstall-check.ps1 -RequireProbe -RequireInternet

param(
    [int] $MinFreeGB = 3,
    [switch] $RequireProbe,
    [switch] $RequireInternet
)

$ErrorActionPreference = "Stop"
$failures = 0
$warnings = 0

function Write-Check([string] $Status, [string] $Name, [string] $Detail = "") {
    $color = switch ($Status) {
        "PASS" { "Green" }
        "WARN" { "Yellow" }
        "FAIL" { "Red" }
        default { "Gray" }
    }
    $line = "[{0}] {1}" -f $Status, $Name
    if (-not [string]::IsNullOrWhiteSpace($Detail)) {
        $line += " - $Detail"
    }
    Write-Host $line -ForegroundColor $color
}

function Pass([string] $Name, [string] $Detail = "") {
    Write-Check "PASS" $Name $Detail
}

function Warn([string] $Name, [string] $Detail = "") {
    $script:warnings++
    Write-Check "WARN" $Name $Detail
}

function Fail([string] $Name, [string] $Detail = "") {
    $script:failures++
    Write-Check "FAIL" $Name $Detail
}

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Find-Gdb {
    $cmd = Get-Command "arm-none-eabi-gdb.exe" -ErrorAction SilentlyContinue
    if ($null -ne $cmd) { return $cmd.Source }

    $roots = @(
        "${env:ProgramFiles}\Arm\GNU Toolchain mingw-w64-i686-arm-none-eabi",
        "${env:ProgramFiles(x86)}\Arm GNU Toolchain arm-none-eabi",
        "${env:ProgramFiles}\Arm GNU Toolchain arm-none-eabi",
        "${env:ProgramFiles(x86)}\GNU Arm Embedded Toolchain",
        "${env:ProgramFiles}\GNU Arm Embedded Toolchain"
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($root in $roots) {
        $direct = Join-Path $root "bin\arm-none-eabi-gdb.exe"
        if (Test-Path -LiteralPath $direct) { return $direct }

        if (Test-Path -LiteralPath $root) {
            $found = Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
                Sort-Object Name -Descending |
                ForEach-Object {
                    $candidate = Join-Path $_.FullName "bin\arm-none-eabi-gdb.exe"
                    if (Test-Path -LiteralPath $candidate) { $candidate }
                } |
                Select-Object -First 1
            if ($found) { return $found }
        }
    }

    return $null
}

function Find-BmpPorts {
    $ports = @()
    $usbRoot = "HKLM:\SYSTEM\CurrentControlSet\Enum\USB"
    if (-not (Test-Path $usbRoot)) { return $ports }

    $activeComPorts = @{}
    $serialComm = "HKLM:\HARDWARE\DEVICEMAP\SERIALCOMM"
    if (Test-Path $serialComm) {
        $serialProps = Get-ItemProperty -LiteralPath $serialComm -ErrorAction SilentlyContinue
        if ($null -ne $serialProps) {
            foreach ($prop in $serialProps.PSObject.Properties) {
                if ($prop.Value -is [string] -and $prop.Value -like "COM*") {
                    $activeComPorts[$prop.Value.ToUpperInvariant()] = $true
                }
            }
        }
    }

    Get-ChildItem $usbRoot -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -like "VID_1D50&PID_6018*" } |
        ForEach-Object {
            Get-ChildItem $_.PSPath -ErrorAction SilentlyContinue | ForEach-Object {
                $friendly = (Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue).FriendlyName
                $params = Join-Path $_.PSPath "Device Parameters"
                $port = $null
                if (Test-Path -LiteralPath $params) {
                    $port = (Get-ItemProperty -LiteralPath $params -ErrorAction SilentlyContinue).PortName
                }
                if ($port) {
                    if ($activeComPorts.Count -gt 0 -and -not $activeComPorts.ContainsKey($port.ToUpperInvariant())) {
                        return
                    }
                    $ports += [pscustomobject]@{
                        PortName = $port
                        FriendlyName = $friendly
                    }
                }
            }
        }

    return $ports
}

function Test-Url([string] $Url) {
    try {
        $response = Invoke-WebRequest -Uri $Url -Method Head -TimeoutSec 10 -UseBasicParsing
        return [int]$response.StatusCode -ge 200 -and [int]$response.StatusCode -lt 500
    } catch {
        return $false
    }
}

Write-Host ""
Write-Host "Iskra station checklist" -ForegroundColor Cyan
Write-Host "======================="

if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
    $version = [Environment]::OSVersion.Version
    if ($version.Major -ge 10) {
        Pass "Windows version" ([Environment]::OSVersion.VersionString)
    } else {
        Fail "Windows version" "Windows 10/11 is required"
    }
} else {
    Fail "Operating system" "Windows is required"
}

if ([Environment]::Is64BitOperatingSystem) {
    Pass "64-bit Windows"
} else {
    Fail "64-bit Windows" "x64 is required"
}

if (Test-Admin) {
    Pass "Installer elevation" "current PowerShell is elevated"
} else {
    Warn "Installer elevation" "setup EXE will request administrator approval"
}

$systemDrive = $env:SystemDrive.TrimEnd(":")
$drive = Get-PSDrive -Name $systemDrive -ErrorAction SilentlyContinue
if ($null -eq $drive) {
    Fail "Free disk space" "could not inspect $env:SystemDrive"
} else {
    $freeGB = [math]::Round($drive.Free / 1GB, 1)
    if ($freeGB -ge $MinFreeGB) {
        Pass "Free disk space" "$freeGB GB available on $env:SystemDrive"
    } else {
        Fail "Free disk space" "$freeGB GB available; need at least $MinFreeGB GB"
    }
}

$setupExe = Get-ChildItem -LiteralPath $PSScriptRoot -Filter "Iskra-*-setup-x64.exe" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if ($setupExe) {
    Pass "Iskra setup EXE" $setupExe.Name
} else {
    Warn "Iskra setup EXE" "not found next to this checklist script"
}

$gdb = Find-Gdb
if ($gdb) {
    Pass "Arm GNU Toolchain" $gdb
} else {
    Warn "Arm GNU Toolchain" "not installed yet; the Iskra setup EXE installs it"
}

$bmpPorts = @(Find-BmpPorts)
if ($bmpPorts.Count -gt 0) {
    $summary = ($bmpPorts | ForEach-Object { "$($_.PortName) $($_.FriendlyName)" }) -join "; "
    Pass "Black Magic Probe USB" $summary
} elseif ($RequireProbe) {
    Fail "Black Magic Probe USB" "connect BMP before commissioning this station"
} else {
    Warn "Black Magic Probe USB" "not connected now; required before flashing"
}

if ($RequireInternet) {
    if (Test-Url "https://api.github.com") {
        Pass "GitHub network access" "api.github.com reachable"
    } else {
        Fail "GitHub network access" "required for login and private firmware downloads"
    }
} else {
    Warn "GitHub network access" "not checked; required only for login/private firmware downloads"
}

$pendingRebootKeys = @(
    "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending",
    "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"
)
$pendingReboot = $pendingRebootKeys | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($pendingReboot) {
    Warn "Pending reboot" "restart Windows before final station acceptance"
} else {
    Pass "Pending reboot" "none detected"
}

Write-Host ""
if ($failures -eq 0) {
    Write-Host "Result: PASS with $warnings warning(s)." -ForegroundColor Green
    exit 0
}

Write-Host "Result: FAIL with $failures failure(s), $warnings warning(s)." -ForegroundColor Red
exit 1
