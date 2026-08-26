using Iskra.Core;

namespace Iskra.Core.Tests;

public class AuditDatabasePathPolicyTests
{
    [Theory]
    [InlineData(":memory:")]
    [InlineData("file::memory:")]
    [InlineData("file:audit.db?mode=memory&cache=shared")]
    public void Transient_or_uri_sources_are_rejected(string value)
    {
        Assert.Throws<ArgumentException>(() =>
            AuditDatabasePathPolicy.ValidateAndNormalize(value));
    }

    [Fact]
    public void Relative_file_is_normalized_to_a_persistent_absolute_path()
    {
        var result = AuditDatabasePathPolicy.ValidateAndNormalize("audit-test.db");
        Assert.True(Path.IsPathFullyQualified(result));
        Assert.EndsWith("audit-test.db", result);
    }
}
