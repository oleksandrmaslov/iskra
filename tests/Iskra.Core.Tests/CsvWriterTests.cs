using Iskra.Core;

namespace Iskra.Core.Tests;

public class CsvWriterTests
{
    [Theory]
    [InlineData("simple",      "simple")]
    [InlineData("",            "")]
    [InlineData("with,comma",  "\"with,comma\"")]
    [InlineData("with\"quote", "\"with\"\"quote\"")]
    [InlineData("line1\nline2","\"line1\nline2\"")]
    [InlineData("line1\r\nl2", "\"line1\r\nl2\"")]
    [InlineData("  spaces ",   "  spaces ")]
    [InlineData("=HYPERLINK(\"https://example.invalid\")", "\"'=HYPERLINK(\"\"https://example.invalid\"\")\"")]
    [InlineData("  +cmd", "'  +cmd")]
    [InlineData("-1", "'-1")]
    [InlineData("@SUM(A1:A2)", "'@SUM(A1:A2)")]
    public void EscapeField_quotes_only_when_required(string input, string expected)
    {
        Assert.Equal(expected, CsvWriter.EscapeField(input));
    }

    [Fact]
    public void EscapeField_null_becomes_empty()
    {
        Assert.Equal("", CsvWriter.EscapeField(null));
    }

    [Fact]
    public void JoinRow_comma_separates_and_escapes_each_field()
    {
        var row = CsvWriter.JoinRow(new[] { "a", "b,c", "d\"e", null });
        Assert.Equal("a,\"b,c\",\"d\"\"e\",", row);
    }

    [Fact]
    public void JoinRow_no_trailing_separator()
    {
        Assert.Equal("x,y,z", CsvWriter.JoinRow(new[] { "x", "y", "z" }));
    }
}
