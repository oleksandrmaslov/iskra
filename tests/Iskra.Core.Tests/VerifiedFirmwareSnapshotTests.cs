using System.Security.Cryptography;
using Iskra.Core;

namespace Iskra.Core.Tests;

public sealed class VerifiedFirmwareSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"iskra-snapshot-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Snapshot_is_hashed_parsed_held_and_deleted_on_dispose()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.hex");
        await File.WriteAllTextAsync(source, ":0400000001020304F2\n:00000001FF\n");
        var expected = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source)))
            .ToLowerInvariant();

        string snapshotPath;
        using (var snapshot = await VerifiedFirmwareSnapshot.CreateAsync(
            source,
            expected,
            FirmwareKind.Hex,
            stagingRoot: Path.Combine(_root, "stage")))
        {
            snapshotPath = snapshot.SnapshotPath;
            Assert.NotEqual(source, snapshotPath);
            Assert.Equal(expected, snapshot.ComputedSha256);
            Assert.Equal(FirmwareImageStatus.Ok, snapshot.Image.Status);
            Assert.True(File.Exists(snapshotPath));

            if (OperatingSystem.IsWindows())
            {
                var writeError = Record.Exception(() => File.Open(
                    snapshotPath,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.ReadWrite).Dispose());
                Assert.True(writeError is IOException or UnauthorizedAccessException);
            }
        }

        Assert.False(File.Exists(snapshotPath));
    }

    [Fact]
    public async Task Hash_mismatch_and_precancel_leave_no_staged_firmware()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.hex");
        await File.WriteAllTextAsync(source, ":0400000001020304F2\n:00000001FF\n");
        var staging = Path.Combine(_root, "stage");

        var mismatch = await Assert.ThrowsAsync<FirmwareSnapshotException>(() =>
            VerifiedFirmwareSnapshot.CreateAsync(
                source,
                new string('0', 64),
                FirmwareKind.Hex,
                stagingRoot: staging));
        Assert.Equal(FirmwareSnapshotStatus.HashMismatch, mismatch.Status);
        Assert.Empty(Directory.Exists(staging)
            ? Directory.EnumerateFileSystemEntries(staging)
            : []);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            VerifiedFirmwareSnapshot.CreateAsync(
                source,
                expectedSha256: null,
                FirmwareKind.Hex,
                cancellation.Token,
                staging));
        Assert.Empty(Directory.Exists(staging)
            ? Directory.EnumerateFileSystemEntries(staging)
            : []);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
