using System.Security.Cryptography;

namespace Iskra.Core;

public enum FirmwareSnapshotStatus
{
    NotFound,
    ReadFailed,
    TooLarge,
    HashMismatch,
    InvalidFormat,
}

public sealed class FirmwareSnapshotException : Exception
{
    public FirmwareSnapshotException(
        FirmwareSnapshotStatus status,
        string message,
        string? computedSha256 = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Status = status;
        ComputedSha256 = computedSha256;
    }

    public FirmwareSnapshotStatus Status { get; }
    public string? ComputedSha256 { get; }
}

/// <summary>
/// A private, byte-for-byte snapshot held read-only for the complete guarded
/// GDB transaction. The SHA-256 and ELF/HEX load map are computed from this
/// snapshot, and only this path is handed to GDB. On Windows the open handle's
/// FileShare.Read mode denies replacement/writes; on Unix the private 0700
/// directory plus a 0400 file provides the equivalent station-account boundary.
/// </summary>
public sealed class VerifiedFirmwareSnapshot : IDisposable
{
    private readonly FileStream _lease;
    private readonly string _snapshotDirectory;
    private bool _disposed;

    private VerifiedFirmwareSnapshot(
        string originalPath,
        string snapshotPath,
        string snapshotDirectory,
        string computedSha256,
        FirmwareImageResult image,
        FileStream lease)
    {
        OriginalPath = originalPath;
        SnapshotPath = snapshotPath;
        _snapshotDirectory = snapshotDirectory;
        ComputedSha256 = computedSha256;
        Image = image;
        _lease = lease;
    }

    public string OriginalPath { get; }
    public string SnapshotPath { get; }
    public string ComputedSha256 { get; }
    public FirmwareImageResult Image { get; }

    public static async Task<VerifiedFirmwareSnapshot> CreateAsync(
        string sourcePath,
        string? expectedSha256,
        FirmwareKind kind,
        CancellationToken cancellationToken = default,
        string? stagingRoot = null)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new FirmwareSnapshotException(
                FirmwareSnapshotStatus.NotFound,
                "firmware file not found");
        if (expectedSha256 is not null && !FirmwareIntegrity.IsValidSha256Hex(expectedSha256))
            throw new ArgumentException("Expected firmware SHA-256 is invalid.", nameof(expectedSha256));

        var root = stagingRoot ?? System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "Iskra",
            "verified-firmware");
        var snapshotDirectory = System.IO.Path.Combine(root, Guid.NewGuid().ToString("N"));
        var extension = kind == FirmwareKind.Hex ? ".hex" : ".elf";
        var snapshotPath = System.IO.Path.Combine(snapshotDirectory, "firmware" + extension);
        FileStream? destination = null;
        FileStream? lease = null;

        try
        {
            Directory.CreateDirectory(snapshotDirectory);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    snapshotDirectory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            await using var source = OpenSource(sourcePath);
            if (source.Length > FirmwareImage.MaxFirmwareFileBytes)
                throw new FirmwareSnapshotException(
                    FirmwareSnapshotStatus.TooLarge,
                    $"firmware exceeds the {FirmwareImage.MaxFirmwareFileBytes}-byte safety limit");

            var destinationOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.Read,
                BufferSize = 64 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough,
            };
            if (!OperatingSystem.IsWindows())
            {
                destinationOptions.UnixCreateMode =
                    UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            destination = new FileStream(snapshotPath, destinationOptions);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long copied = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                copied = checked(copied + read);
                if (copied > FirmwareImage.MaxFirmwareFileBytes)
                    throw new FirmwareSnapshotException(
                        FirmwareSnapshotStatus.TooLarge,
                        $"firmware exceeds the {FirmwareImage.MaxFirmwareFileBytes}-byte safety limit");
                hash.AppendData(buffer.AsSpan(0, read));
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            destination.Flush(flushToDisk: true);

            var computed = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (expectedSha256 is not null
                && !FirmwareIntegrity.HashesMatch(computed, expectedSha256))
            {
                throw new FirmwareSnapshotException(
                    FirmwareSnapshotStatus.HashMismatch,
                    $"computed {computed}, expected {expectedSha256.ToLowerInvariant()}",
                    computed);
            }

            if (OperatingSystem.IsWindows())
                File.SetAttributes(snapshotPath, FileAttributes.ReadOnly);
            else
                File.SetUnixFileMode(snapshotPath, UnixFileMode.UserRead);

            // Reopen under the final read-only sharing contract, then hash one
            // more time. This detects any replacement in the close/reopen gap;
            // once this handle exists, Windows denies writers and Unix has the
            // private 0700 directory plus the 0400 file.
            destination.Dispose();
            destination = null;
            lease = new FileStream(snapshotPath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 64 * 1024,
                Options = FileOptions.SequentialScan,
            });
            var lockedComputed = Convert.ToHexString(SHA256.HashData(lease)).ToLowerInvariant();
            lease.Position = 0;
            if (!FirmwareIntegrity.HashesMatch(lockedComputed, computed))
            {
                throw new FirmwareSnapshotException(
                    FirmwareSnapshotStatus.HashMismatch,
                    "verified staging snapshot changed before its read-only lease was acquired",
                    lockedComputed);
            }

            var image = FirmwareImage.Read(snapshotPath, kind);
            if (!image.IsOk)
            {
                var status = image.Status is FirmwareImageStatus.IoError or FirmwareImageStatus.NotFound
                    ? FirmwareSnapshotStatus.ReadFailed
                    : FirmwareSnapshotStatus.InvalidFormat;
                throw new FirmwareSnapshotException(
                    status,
                    image.Diagnostic ?? "firmware image is invalid",
                    computed);
            }

            return new VerifiedFirmwareSnapshot(
                sourcePath,
                snapshotPath,
                snapshotDirectory,
                computed,
                image,
                lease);
        }
        catch
        {
            destination?.Dispose();
            lease?.Dispose();
            TryDeleteDirectory(snapshotDirectory);
            throw;
        }
    }

    private static FileStream OpenSource(string path)
    {
        try
        {
            return new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 64 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
        }
        catch (FileNotFoundException ex)
        {
            throw new FirmwareSnapshotException(
                FirmwareSnapshotStatus.NotFound,
                $"firmware file not found: {path}",
                innerException: ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new FirmwareSnapshotException(
                FirmwareSnapshotStatus.NotFound,
                $"firmware file not found: {path}",
                innerException: ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new FirmwareSnapshotException(
                FirmwareSnapshotStatus.ReadFailed,
                $"firmware file could not be read: {path}",
                innerException: ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lease.Dispose();
        TryDeleteDirectory(_snapshotDirectory);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                if (OperatingSystem.IsWindows())
                {
                    foreach (var file in Directory.EnumerateFiles(path))
                        File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best effort only. The random, private path contains no reusable
            // trust state; a later station cleanup can remove an orphan.
        }
    }
}
