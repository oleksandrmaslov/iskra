using Iskra.Application;
using Iskra.Core;

namespace Iskra.Application.Tests;

public sealed class BatchPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  stale batch  ")]
    public void Disabled_mode_always_returns_blank_valid_non_reserving_id(string? entered)
    {
        var result = BatchPolicy.Resolve(batchesEnabled: false, entered);

        Assert.True(result.IsValid);
        Assert.False(result.BatchesEnabled);
        Assert.False(result.ShouldReserve);
        Assert.Equal(string.Empty, result.EffectiveBatchId);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void Enabled_mode_requires_a_non_empty_id()
    {
        var result = BatchPolicy.Resolve(batchesEnabled: true, " \t ");

        Assert.False(result.IsValid);
        Assert.False(result.ShouldReserve);
        Assert.Equal(string.Empty, result.EffectiveBatchId);
        Assert.Equal(BatchPolicy.RequiredErrorCode, result.ErrorCode);
    }

    [Fact]
    public void Enabled_mode_trims_the_effective_id_and_reserves()
    {
        var result = BatchPolicy.Resolve(batchesEnabled: true, "  B-2026-42  ");

        Assert.True(result.IsValid);
        Assert.True(result.ShouldReserve);
        Assert.Equal("B-2026-42", result.EffectiveBatchId);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void Settings_overload_uses_the_persisted_toggle()
    {
        var result = BatchPolicy.Resolve(new AppSettings { BatchesEnabled = false }, "B-1");

        Assert.Equal(string.Empty, result.EffectiveBatchId);
        Assert.False(result.ShouldReserve);
    }
}
