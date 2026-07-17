param(
    [Parameter(Mandatory = $true)]
    [string] $Version,
    [string] $AvaloniaVersion = "",
    [string] $Runtime = "win-x64",
    [string] $Configuration = "Release",
    [string] $OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path -LiteralPath $dotnet)) {
    throw "The repository SDK host was not found at $dotnet"
}

if ([string]::IsNullOrWhiteSpace($AvaloniaVersion)) {
    $AvaloniaVersion = "$Version-alpha.1"
}

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
$staging = Join-Path $output ".publish"
New-Item -ItemType Directory -Path $staging -Force | Out-Null

Write-Host "Restoring committed package locks..." -ForegroundColor Cyan
& $dotnet restore Iskra.sln --locked-mode --nologo | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "Locked solution restore failed (exit $LASTEXITCODE)"
}

function Publish-SingleFile(
    [string] $Project,
    [string] $ProductVersion,
    [string] $StageName,
    [string] $PublishedExecutable,
    [string] $BundleExecutable
) {
    $stage = Join-Path $staging $StageName
    New-Item -ItemType Directory -Path $stage -Force | Out-Null

    & $dotnet publish $Project `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        --no-restore `
        -p:PublishSingleFile=true `
        -p:IncludeAllContentForSelfExtract=true `
        -p:PublishTrimmed=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:Version=$ProductVersion `
        -o $stage | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $Project (exit $LASTEXITCODE)"
    }

    $publishedPath = Join-Path $stage $PublishedExecutable
    if (-not (Test-Path -LiteralPath $publishedPath)) {
        throw "Publish completed but $PublishedExecutable is missing from $stage"
    }

    Copy-Item -LiteralPath $publishedPath -Destination (Join-Path $output $BundleExecutable) -Force
}

Write-Host "Publishing supported WPF $Version..." -ForegroundColor Cyan
Publish-SingleFile `
    "src/Iskra.Wpf/Iskra.Wpf.csproj" `
    $Version `
    "wpf" `
    "Iskra.exe" `
    "Iskra.exe"

Write-Host "Publishing CLI $Version..." -ForegroundColor Cyan
Publish-SingleFile `
    "src/Iskra.Cli/Iskra.Cli.csproj" `
    $Version `
    "cli" `
    "Iskra.Cli.exe" `
    "Iskra.Cli.exe"

Write-Host "Publishing read-only Avalonia alpha $AvaloniaVersion..." -ForegroundColor Cyan
Publish-SingleFile `
    "src/Iskra.Desktop/Iskra.Desktop.csproj" `
    $AvaloniaVersion `
    "avalonia" `
    "Iskra.Desktop.exe" `
    "Iskra.Avalonia.Alpha.exe"

Remove-Item -LiteralPath $staging -Recurse -Force

$examples = Join-Path $output "examples"
New-Item -ItemType Directory -Path $examples -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot "examples\catalog.json") -Destination $examples -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "examples\catalog.json.sig") -Destination $examples -Force

$sdkVersion = (& $dotnet --version).Trim()
$gitCommit = (& git rev-parse --short=12 HEAD).Trim()
$gitState = if (@(& git status --porcelain).Count -gt 0) { "dirty" } else { "clean" }
$builtAt = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
$manifest = @"
Iskra Windows executable bundle

WPF:       Iskra.exe $Version (supported Windows variant)
CLI:       Iskra.Cli.exe $Version
Avalonia:  Iskra.Avalonia.Alpha.exe $AvaloniaVersion (alpha, read-only; flashing disabled)
Runtime:   $Runtime, self-contained, single-file
.NET SDK:  $sdkVersion
Commit:    $gitCommit ($gitState working tree)
Built UTC: $builtAt

This is a local unsigned engineering release bundle. It is not an
Authenticode-signed factory release and does not claim Sprint 9 acceptance.
"@
[IO.File]::WriteAllText(
    (Join-Path $output "BUNDLE-MANIFEST.txt"),
    $manifest.TrimStart(),
    [Text.UTF8Encoding]::new($false))

$checksumFiles = @(
    "Iskra.exe",
    "Iskra.Cli.exe",
    "Iskra.Avalonia.Alpha.exe",
    "examples\catalog.json",
    "examples\catalog.json.sig",
    "BUNDLE-MANIFEST.txt"
)
$checksumLines = foreach ($name in $checksumFiles) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $output $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($name.Replace('\', '/'))"
}
[IO.File]::WriteAllLines(
    (Join-Path $output "SHA256SUMS.txt"),
    $checksumLines,
    [Text.UTF8Encoding]::new($false))

$zipPath = "$output.zip"
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $output "*") -DestinationPath $zipPath -CompressionLevel Optimal
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(
    "$zipPath.sha256",
    "$zipHash  $([IO.Path]::GetFileName($zipPath))`n",
    [Text.UTF8Encoding]::new($false))

Write-Host "Windows executable bundle is ready:" -ForegroundColor Green
Write-Host "  $output"
Write-Host "  $zipPath"
Get-ChildItem -LiteralPath $output -File |
    Select-Object Name, @{Name="SizeMB"; Expression={ [Math]::Round($_.Length / 1MB, 1) }} |
    Format-Table -AutoSize
