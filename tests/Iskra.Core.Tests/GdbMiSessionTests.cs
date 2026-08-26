using Iskra.Core;

namespace Iskra.Core.Tests;

public sealed class GdbMiSessionTests
{
    [Fact]
    public void EscapeMiCString_escapes_command_boundaries_and_control_characters()
    {
        var escaped = GdbMiSession.EscapeMiCString("C:\\fw\\a\"b\nnext\t\u0001.elf");

        Assert.Equal("C:\\\\fw\\\\a\\\"b\\nnext\\t\\x01.elf", escaped);
        Assert.DoesNotContain('\n', escaped);
        Assert.DoesNotContain('\r', escaped);
    }

    [Fact]
    public void DecodeMiCString_decodes_standard_hex_and_octal_escapes()
    {
        var decoded = GdbMiSession.DecodeMiCString(
            "\"line\\nquote: \\\" slash: \\\\ hex: \\x41 octal: \\101\\tend\"".AsSpan());

        Assert.Equal("line\nquote: \" slash: \\ hex: A octal: A\tend", decoded);
    }

    [Fact]
    public void DecodeMiCString_preserves_unknown_or_malformed_escapes()
    {
        var decoded = GdbMiSession.DecodeMiCString("\"bad: \\q \\x\"".AsSpan());

        Assert.Equal("bad: \\q \\x", decoded);
    }

    [Fact]
    public void Escape_and_decode_round_trip_supported_text()
    {
        const string original = "BMP Проба\\\"\n\r\t\b\f\v\a\u0001";
        var encoded = $"\"{GdbMiSession.EscapeMiCString(original)}\"";

        Assert.Equal(original, GdbMiSession.DecodeMiCString(encoded.AsSpan()));
    }

    [Fact]
    public void Probe_lock_is_exclusive_and_released_for_the_same_endpoint()
    {
        var root = Path.Combine(Path.GetTempPath(), "Iskra.Core.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var first = GdbMiSession.TryAcquireProbeLock(@"\\.\COM30", root);
            Assert.NotNull(first);
            try
            {
                Assert.Null(GdbMiSession.TryAcquireProbeLock(@"\\.\COM30", root));
                using var other = GdbMiSession.TryAcquireProbeLock(@"\\.\COM31", root);
                Assert.NotNull(other);
            }
            finally
            {
                first!.Dispose();
            }

            using var reacquired = GdbMiSession.TryAcquireProbeLock(@"\\.\COM30", root);
            Assert.NotNull(reacquired);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
