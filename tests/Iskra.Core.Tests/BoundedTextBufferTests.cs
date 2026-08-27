using Iskra.Core;

namespace Iskra.Core.Tests;

public sealed class BoundedTextBufferTests
{
    [Fact]
    public void Retains_recent_complete_lines_within_budget()
    {
        var buffer = new BoundedTextBuffer(96);

        for (var i = 0; i < 20; i++)
            buffer.AppendLine($"line-{i:D2}-abcdefgh");

        var text = buffer.ToString();
        Assert.True(text.Length <= 96);
        Assert.StartsWith("[… older output truncated …]\n", text);
        Assert.DoesNotContain("line-00", text);
        Assert.Contains("line-19", text);
    }

    [Fact]
    public void Single_oversized_line_keeps_only_bounded_suffix()
    {
        var buffer = new BoundedTextBuffer(80);

        var text = buffer.AppendLine(new string('x', 500) + "TAIL");

        Assert.True(text.Length <= 80);
        Assert.EndsWith("TAIL" + Environment.NewLine, text);
    }

    [Fact]
    public void Clear_removes_all_retained_output()
    {
        var buffer = new BoundedTextBuffer(80);
        buffer.AppendLine("diagnostic");

        buffer.Clear();

        Assert.Equal(0, buffer.Length);
        Assert.Equal(string.Empty, buffer.ToString());
    }
}
