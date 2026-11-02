using Iskra.Core;

namespace Iskra.Core.Tests;

public sealed class GdbOutputRetentionTests
{
    [Fact]
    public async Task Chunked_reader_bounds_a_single_line_before_callback()
    {
        var input = new StringReader(new string('x', GdbProcess.MaxCapturedLineChars * 64));
        var lines = new List<BoundedProcessLine>();

        await BoundedProcessLineReader.ReadAsync(input, lines.Add, CancellationToken.None);

        var line = Assert.Single(lines);
        Assert.True(line.WasTruncated);
        Assert.Equal(GdbProcess.MaxCapturedLineChars, line.Text.Length);
        Assert.EndsWith("…[truncated]", line.Text);
    }

    [Fact]
    public async Task Chunked_reader_handles_crlf_lf_and_final_unterminated_line()
    {
        var input = new StringReader("one\r\ntwo\nthree");
        var lines = new List<BoundedProcessLine>();

        await BoundedProcessLineReader.ReadAsync(input, lines.Add, CancellationToken.None);

        Assert.Equal(new[] { "one", "two", "three" }, lines.Select(line => line.Text));
        Assert.All(lines, line => Assert.False(line.WasTruncated));
    }

    [Fact]
    public void Oversized_line_is_truncated_to_the_exact_per_line_budget()
    {
        var text = BoundedGdbLineBuffer.TruncateLine(
            new string('x', GdbProcess.MaxCapturedLineChars * 2));

        Assert.Equal(GdbProcess.MaxCapturedLineChars, text.Length);
        Assert.EndsWith("…[truncated]", text);
    }

    [Fact]
    public void Aggregate_capture_retains_newest_lines_within_character_budget()
    {
        var buffer = new BoundedGdbLineBuffer();
        for (var index = 0; index < 500; index++)
        {
            buffer.Add(new GdbLine(
                DateTime.UtcNow,
                GdbStream.Stdout,
                $"{index:D4}:" + new string('x', GdbProcess.MaxCapturedLineChars)));
        }

        var snapshot = buffer.Snapshot();
        Assert.NotEmpty(snapshot);
        Assert.True(buffer.RetainedChars <= GdbProcess.MaxCapturedChars);
        Assert.True(snapshot.Sum(line => line.Text.Length) <= GdbProcess.MaxCapturedChars);
        Assert.StartsWith("0499:", snapshot[^1].Text);
        Assert.DoesNotContain(snapshot, line => line.Text.StartsWith("0000:", StringComparison.Ordinal));
    }

    [Fact]
    public void Empty_lines_are_still_bounded_by_count()
    {
        var buffer = new BoundedGdbLineBuffer();
        for (var index = 0; index < GdbProcess.MaxCapturedLines + 100; index++)
            buffer.Add(new GdbLine(DateTime.UtcNow, GdbStream.Stderr, string.Empty));

        Assert.Equal(GdbProcess.MaxCapturedLines, buffer.Snapshot().Count);
    }
}
