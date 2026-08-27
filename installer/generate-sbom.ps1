param(
    [Parameter(Mandatory = $true)]
    [string] $Version,

    [Parameter(Mandatory = $true)]
    [string] $BuildDropPath,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))

if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
    throw "Version must be SemVer-like (for example 2.2.0 or 2.2.0-rc.1): $Version"
}

$drop = if ([IO.Path]::IsPathRooted($BuildDropPath)) {
    [IO.Path]::GetFullPath($BuildDropPath)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $BuildDropPath))
}
if (-not (Test-Path -LiteralPath $drop -PathType Container)) {
    throw "SBOM build drop does not exist: $drop"
}

$output = if ([IO.Path]::IsPathRooted($OutputPath)) {
    [IO.Path]::GetFullPath($OutputPath)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputPath))
}
$outputDirectory = Split-Path -Parent $output
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

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

$tempRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ("iskra-sbom-" + [Guid]::NewGuid().ToString("N"))))
New-Item -ItemType Directory -Path $tempRoot | Out-Null
try {
    & $dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw "repository tool restore failed" }

    & $dotnet tool run sbom-tool generate `
        -b $drop `
        -bc $repoRoot `
        -m $tempRoot `
        -pn Iskra `
        -pv $Version `
        -ps "Oleksandr Maslov" `
        -nsb "https://github.com/oleksandrmaslov/iskra" `
        -mi SPDX:2.2 `
        -V Warning
    if ($LASTEXITCODE -ne 0) { throw "SBOM generation failed" }

    $manifest = Join-Path $tempRoot "_manifest\spdx_2.2\manifest.spdx.json"
    if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) {
        throw "SBOM tool did not create the expected SPDX manifest: $manifest"
    }

    $validation = Join-Path $tempRoot "validation.json"
    & $dotnet tool run sbom-tool validate `
        -b $drop `
        -m (Join-Path $tempRoot "_manifest") `
        -o $validation `
        -mi SPDX:2.2 `
        -V Warning
    if ($LASTEXITCODE -ne 0) { throw "SBOM validation failed" }

    Copy-Item -LiteralPath $manifest -Destination $output -Force
    Write-Host "SPDX SBOM: $output"
}
finally {
    $systemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($tempRoot.StartsWith($systemTemp, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $tempRoot).StartsWith("iskra-sbom-", [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
