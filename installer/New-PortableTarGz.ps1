<#
.SYNOPSIS
Creates a gzip-compressed PAX archive with deterministic metadata and explicit
Unix modes, even when the build is running on Windows.

.DESCRIPTION
Compress-Archive and Windows tar both record regular files as mode 0666. That
makes a cross-published Iskra bundle fail on first launch until the operator
runs chmod. This helper writes the archive through System.Formats.Tar and marks
only the declared launchers executable. Entries are sorted, owned by uid/gid 0,
and share one caller-supplied timestamp so repeated builds from the same inputs
produce stable tar payloads.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $SourceDirectory,

    [Parameter(Mandatory)]
    [string] $DestinationPath,

    [Parameter(Mandatory)]
    [string] $RootName,

    [string[]] $ExecutablePaths = @(),

    # Native shells cannot reliably bind multiple values to a PowerShell
    # array parameter through `pwsh -File`. This semicolon-delimited companion
    # keeps the shell contract unambiguous for the fixed, generated paths used
    # by the native builders.
    [string] $ExecutablePathList = "",

    [DateTimeOffset] $Timestamp = [DateTimeOffset]::UnixEpoch
)

$ErrorActionPreference = "Stop"

$source = (Resolve-Path -LiteralPath $SourceDirectory).Path.TrimEnd('\', '/')
if (-not [IO.Directory]::Exists($source)) {
    throw "archive source is not a directory: $SourceDirectory"
}
if ([string]::IsNullOrWhiteSpace($RootName) -or $RootName.Contains('/') -or $RootName.Contains('\')) {
    throw "RootName must be one portable path segment"
}

$destination = [IO.Path]::GetFullPath($DestinationPath)
$destinationDirectory = [IO.Path]::GetDirectoryName($destination)
if ([string]::IsNullOrWhiteSpace($destinationDirectory)) {
    throw "DestinationPath must have a parent directory"
}
[void] [IO.Directory]::CreateDirectory($destinationDirectory)

$allExecutablePaths = @($ExecutablePaths)
if (-not [string]::IsNullOrWhiteSpace($ExecutablePathList)) {
    $allExecutablePaths += @($ExecutablePathList.Split(';', [StringSplitOptions]::RemoveEmptyEntries))
}

$executable = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($path in $allExecutablePaths) {
    if ([string]::IsNullOrWhiteSpace($path)) { continue }
    $normalized = $path.TrimStart('.', '/', '\').Replace('\', '/')
    if ($normalized.Contains('../') -or $normalized -eq '..') {
        throw "executable path escapes the archive root: $path"
    }
    [void] $executable.Add($normalized)
}

$directoryMode = [System.IO.UnixFileMode] 493 # 0755
$regularMode = [System.IO.UnixFileMode] 420   # 0644
$executableMode = [System.IO.UnixFileMode] 493

function Set-PortableMetadata([System.Formats.Tar.TarEntry] $Entry, [System.IO.UnixFileMode] $Mode) {
    $Entry.Mode = $Mode
    $Entry.Uid = 0
    $Entry.Gid = 0
    $Entry.ModificationTime = $Timestamp
    if ($Entry -is [System.Formats.Tar.PosixTarEntry]) {
        $Entry.UserName = "root"
        $Entry.GroupName = "root"
    }
}

$fileStream = $null
$gzipStream = $null
$writer = $null
try {
    $fileStream = [IO.File]::Open($destination, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $gzipStream = [IO.Compression.GZipStream]::new(
        $fileStream,
        [IO.Compression.CompressionLevel]::Optimal,
        $false)
    $writer = [System.Formats.Tar.TarWriter]::new(
        $gzipStream,
        [System.Formats.Tar.TarEntryFormat]::Pax,
        $false)

    $rootEntry = [System.Formats.Tar.PaxTarEntry]::new(
        [System.Formats.Tar.TarEntryType]::Directory,
        "$RootName/")
    Set-PortableMetadata $rootEntry $directoryMode
    $writer.WriteEntry($rootEntry)

    $directories = [IO.Directory]::EnumerateDirectories($source, '*', [IO.SearchOption]::AllDirectories) |
        Sort-Object { [IO.Path]::GetRelativePath($source, $_).Replace('\', '/') }
    foreach ($directory in $directories) {
        $relative = [IO.Path]::GetRelativePath($source, $directory).Replace('\', '/')
        $entry = [System.Formats.Tar.PaxTarEntry]::new(
            [System.Formats.Tar.TarEntryType]::Directory,
            "$RootName/$relative/")
        Set-PortableMetadata $entry $directoryMode
        $writer.WriteEntry($entry)
    }

    $files = [IO.Directory]::EnumerateFiles($source, '*', [IO.SearchOption]::AllDirectories) |
        Sort-Object { [IO.Path]::GetRelativePath($source, $_).Replace('\', '/') }
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($source, $file).Replace('\', '/')
        $entry = [System.Formats.Tar.PaxTarEntry]::new(
            [System.Formats.Tar.TarEntryType]::RegularFile,
            "$RootName/$relative")
        Set-PortableMetadata $entry $(if ($executable.Contains($relative)) { $executableMode } else { $regularMode })

        $input = [IO.File]::Open($file, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            $entry.DataStream = $input
            $writer.WriteEntry($entry)
        }
        finally {
            $entry.DataStream = $null
            $input.Dispose()
        }
    }
}
finally {
    if ($null -ne $writer) { $writer.Dispose() }
    elseif ($null -ne $gzipStream) { $gzipStream.Dispose() }
    elseif ($null -ne $fileStream) { $fileStream.Dispose() }
}

Get-Item -LiteralPath $destination
