namespace Iskra.Core.Tests;

public sealed class CatalogActivationPolicyTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"iskra-catalog-floor-{Guid.NewGuid():N}");

    private string FloorPath => Path.Combine(_directory, "latest.generated_at");

    [Fact]
    public void First_activation_creates_floor_and_same_catalog_can_reopen()
    {
        var generatedAt = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);

        var first = CatalogActivationPolicy.ValidateAndAdvance(
            generatedAt, FloorPath, generatedAt.AddHours(1), catalogSha256: new string('a', 64));
        var reopened = CatalogActivationPolicy.ValidateAndAdvance(
            generatedAt, FloorPath, generatedAt.AddHours(1), catalogSha256: new string('a', 64));

        Assert.True(first.IsAccepted);
        Assert.True(reopened.IsAccepted);
        using var state = System.Text.Json.JsonDocument.Parse(File.ReadAllText(FloorPath));
        Assert.Equal(
            generatedAt,
            DateTime.Parse(state.RootElement.GetProperty("generated_at_utc").GetString()!).ToUniversalTime());
        Assert.Equal(new string('a', 64), state.RootElement.GetProperty("catalog_sha256").GetString());
    }

    [Fact]
    public void Older_signed_catalog_is_rejected_during_activation()
    {
        var floor = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(CatalogActivationPolicy.ValidateAndAdvance(
            floor, FloorPath, floor.AddHours(1)).IsAccepted);

        var result = CatalogActivationPolicy.ValidateAndAdvance(
            floor.AddSeconds(-1), FloorPath, floor.AddHours(1));

        Assert.Equal(CatalogActivationStatus.RollbackRejected, result.Status);
        Assert.Equal(floor, result.PreviousFloorUtc);
    }

    [Fact]
    public void Download_policy_requires_strictly_newer_timestamp()
    {
        var floor = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(CatalogActivationPolicy.ValidateAndAdvance(
            floor, FloorPath, floor.AddHours(1)).IsAccepted);

        var result = CatalogActivationPolicy.ValidateAndAdvance(
            floor, FloorPath, floor.AddHours(1), requireNewer: true);

        Assert.Equal(CatalogActivationStatus.RollbackRejected, result.Status);
    }

    [Fact]
    public void Download_retry_at_same_timestamp_is_idempotent_only_for_same_digest()
    {
        var floor = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        var digest = new string('d', 64);
        Assert.True(CatalogActivationPolicy.ValidateAndAdvance(
            floor,
            FloorPath,
            floor.AddHours(1),
            catalogSha256: digest).IsAccepted);

        var retry = CatalogActivationPolicy.ValidateAndAdvance(
            floor,
            FloorPath,
            floor.AddHours(1),
            requireNewer: true,
            catalogSha256: digest);
        var replacement = CatalogActivationPolicy.ValidateAndAdvance(
            floor,
            FloorPath,
            floor.AddHours(1),
            requireNewer: true,
            catalogSha256: new string('e', 64));

        Assert.True(retry.IsAccepted);
        Assert.Equal(CatalogActivationStatus.RollbackRejected, replacement.Status);
    }

    [Fact]
    public void Malformed_floor_and_future_catalog_fail_closed()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FloorPath, "not-a-timestamp");
        var now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);

        var malformed = CatalogActivationPolicy.ValidateAndAdvance(now, FloorPath, now);
        var future = CatalogActivationPolicy.ValidateAndAdvance(now.AddHours(25), FloorPath, now);

        Assert.Equal(CatalogActivationStatus.StateError, malformed.Status);
        Assert.Equal(CatalogActivationStatus.FutureRejected, future.Status);
    }

    [Fact]
    public void Same_timestamp_with_different_signed_digest_fails_closed()
    {
        var now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(CatalogActivationPolicy.ValidateAndAdvance(
            now, FloorPath, now.AddMinutes(1), catalogSha256: new string('a', 64)).IsAccepted);

        var result = CatalogActivationPolicy.ValidateAndAdvance(
            now, FloorPath, now.AddMinutes(1), catalogSha256: new string('b', 64));

        Assert.Equal(CatalogActivationStatus.RollbackRejected, result.Status);
        Assert.Contains("digest differs", result.Diagnostic);
    }

    [Fact]
    public void Validation_only_does_not_advance_floor()
    {
        var now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);

        var result = CatalogActivationPolicy.ValidateAndAdvance(
            now,
            FloorPath,
            now.AddMinutes(1),
            catalogSha256: new string('c', 64),
            advance: false);

        Assert.True(result.IsAccepted);
        Assert.False(File.Exists(FloorPath));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch { }
    }
}
