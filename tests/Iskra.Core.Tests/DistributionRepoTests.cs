using Iskra.Core;

namespace Iskra.Core.Tests;

/// <summary>
/// The catalog must be able to serve firmware from a dedicated artefact
/// repository rather than from each product's source repository.
///
/// <para>GitHub read access is repository-wide and cannot be narrowed to
/// "releases only", so granting an operator the flashable binary from a source
/// repo would also grant them the source and its full history. Pointing
/// <c>elf_source</c> at a separate distribution repo is what keeps those two
/// grants apart.</para>
/// </summary>
public sealed class DistributionRepoTests
{
    private static TargetSidecar Sidecar(string productId, string version) => new(
        ProductId: productId,
        Version: version,
        PartNumber: "PY32F002Ax5",
        BmpMatch: "PY32Fxxx",
        FlashKb: 32,
        ElfSha256: new string('a', 64),
        FirmwareKind: FirmwareKind.Elf);

    [Fact]
    public void Without_a_distribution_repo_the_source_repo_convention_is_preserved()
    {
        // Catalogs generated before this option existed must keep working.
        var catalog = CatalogGenerator.Build(
            [Sidecar("ci-clop", "1.0.0")],
            owner: "oleksandrmaslov",
            generatedAtUtc: DateTime.UtcNow);

        var release = catalog.Products[0].Releases[0];
        Assert.Equal("oleksandrmaslov/ci-clop-firmware", release.ElfSource!.Repo);
    }

    [Fact]
    public void A_distribution_repo_replaces_every_source_repo_reference()
    {
        var catalog = CatalogGenerator.Build(
            [Sidecar("ci-clop", "1.0.0"), Sidecar("venovisor", "2.3.4")],
            owner: "oleksandrmaslov",
            generatedAtUtc: DateTime.UtcNow,
            revoked: null,
            distributionRepo: "oleksandrmaslov/iskra-firmware");

        var repos = catalog.Products
            .SelectMany(p => p.Releases)
            .Select(r => r.ElfSource!.Repo)
            .Distinct()
            .ToList();

        // One repo for everything, and crucially no "-firmware" source repo.
        Assert.Equal(["oleksandrmaslov/iskra-firmware"], repos);
        Assert.DoesNotContain(repos, r => r.EndsWith("ci-clop-firmware", StringComparison.Ordinal));
    }

    [Fact]
    public void The_tag_and_asset_still_identify_the_exact_artefact()
    {
        // Redirecting the repo must not disturb which bytes get downloaded.
        var catalog = CatalogGenerator.Build(
            [Sidecar("ci-clop", "1.0.3")],
            owner: "oleksandrmaslov",
            generatedAtUtc: DateTime.UtcNow,
            revoked: null,
            distributionRepo: "oleksandrmaslov/iskra-firmware");

        var source = catalog.Products[0].Releases[0].ElfSource!;
        Assert.Equal("v1.0.3", source.Tag);
        Assert.Equal("ci-clop_v1.0.3_PY32F002Ax5.elf", source.Asset);
    }

    [Theory]
    [InlineData("no-slash")]
    [InlineData("too/many/slashes")]
    [InlineData("/repo")]
    [InlineData("owner/")]
    public void A_malformed_distribution_repo_is_refused(string value)
    {
        // Silently accepting this would redirect every station's firmware
        // download to a nonexistent or attacker-chosen location.
        Assert.Throws<ArgumentException>(() => CatalogGenerator.Build(
            [Sidecar("ci-clop", "1.0.0")],
            owner: "oleksandrmaslov",
            generatedAtUtc: DateTime.UtcNow,
            revoked: null,
            distributionRepo: value));
    }
}
