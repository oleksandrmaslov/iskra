param(
    [string] $Version = "1.0.0",
    [string] $Runtime = "win-x64",
    [string] $Configuration = "Release",
    [string] $OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts\Iskra-$Version-$Runtime"
} elseif (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot $OutputDirectory
}

$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts")).TrimEnd('\')
$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if (-not $output.StartsWith($artifactsRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must stay inside $artifactsRoot"
}

if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}
New-Item -ItemType Directory -Path $output -Force | Out-Null

function Publish-SingleFile([string] $Project) {
    dotnet publish $Project `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeAllContentForSelfExtract=true `
        -p:PublishTrimmed=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:RestorePackagesWithLockFile=true `
        -p:RestoreLockedMode=false `
        -p:NuGetLockFilePath=obj\publish-lock\packages.lock.json `
        -p:Version=$Version `
        -o $output | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $Project (exit $LASTEXITCODE)"
    }
}

Write-Host "Publishing Iskra WPF..." -ForegroundColor Cyan
Publish-SingleFile "src/Iskra.Wpf/Iskra.Wpf.csproj"

Write-Host "Publishing Iskra Avalonia preview..." -ForegroundColor Cyan
Publish-SingleFile "src/Iskra.Desktop/Iskra.Desktop.csproj"

Write-Host "Publishing Iskra CLI..." -ForegroundColor Cyan
Publish-SingleFile "src/Iskra.Cli/Iskra.Cli.csproj"

# Native Avalonia dependencies can emit large symbol files even when the app
# itself is published without debug symbols. They are not required to run the
# operator binaries and would nearly double the handoff size.
Get-ChildItem -LiteralPath $output -Filter "*.pdb" -File |
    Remove-Item -Force

$requiredExecutables = @("Iskra.exe", "Iskra.Desktop.exe", "Iskra.Cli.exe")
foreach ($name in $requiredExecutables) {
    $path = Join-Path $output $name
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Publish completed but $name is missing from $output"
    }
}

$examples = Join-Path $output "examples"
New-Item -ItemType Directory -Path $examples -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot "examples\catalog.json") -Destination $examples -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "examples\catalog.json.sig") -Destination $examples -Force

$checksumLines = foreach ($name in $requiredExecutables) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $output $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $name"
}
[IO.File]::WriteAllLines(
    (Join-Path $output "SHA256SUMS.txt"),
    $checksumLines,
    [Text.UTF8Encoding]::new($false))

Write-Host "Localized executables are ready:" -ForegroundColor Green
Write-Host "  $output"
Get-ChildItem -LiteralPath $output -File |
    Select-Object Name, @{Name="SizeMB"; Expression={ [Math]::Round($_.Length / 1MB, 1) }} |
    Format-Table -AutoSize
