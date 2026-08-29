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

    # Component Detector must not scan artifacts/release: when several SBOMs
    # are generated in sequence it would otherwise treat earlier manifests as
    # components of later packages. Stage only dependency/source manifests and
    # restored NuGet asset graphs in an isolated component root.
    $componentRoot = Join-Path $tempRoot "components"
    New-Item -ItemType Directory -Path $componentRoot | Out-Null
    $componentFiles = @()
    foreach ($name in @("global.json", "Iskra.sln", "nuget.config", "Directory.Build.props", "Directory.Build.targets")) {
        $candidate = Join-Path $repoRoot $name
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $componentFiles += Get-Item -LiteralPath $candidate }
    }
    $toolManifest = Join-Path $repoRoot ".config\dotnet-tools.json"
    if (Test-Path -LiteralPath $toolManifest -PathType Leaf) { $componentFiles += Get-Item -LiteralPath $toolManifest }
    foreach ($treeName in @("src", "tests")) {
        $tree = Join-Path $repoRoot $treeName
        $componentFiles += Get-ChildItem -LiteralPath $tree -File -Recurse | Where-Object {
            $_.Name -eq "packages.lock.json" -or
            $_.Name -eq "project.assets.json" -or
            $_.Extension -eq ".csproj"
        }
    }
    foreach ($file in $componentFiles) {
        $relative = [IO.Path]::GetRelativePath($repoRoot, $file.FullName)
        $destination = Join-Path $componentRoot $relative
        $destinationDirectory = Split-Path -Parent $destination
        New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }

    & $dotnet tool run sbom-tool generate `
        -b $drop `
        -bc $componentRoot `
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
