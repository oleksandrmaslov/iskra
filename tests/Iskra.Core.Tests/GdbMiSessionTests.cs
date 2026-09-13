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

    [Theory]
    [InlineData("COM30", @"-target-select extended-remote \\.\COM30")]
    [InlineData(@"\\.\COM7", @"-target-select extended-remote \\.\COM7")]
    [InlineData("/dev/ttyACM0", "-target-select extended-remote /dev/ttyACM0")]
    public void Target_select_passes_the_endpoint_to_gdb_verbatim(string endpoint, string expected)
    {
        // gdb hands -target-select arguments to its CLI without MI unescaping.
        // Quoting them made gdb open a name containing the quotes and doubled
        // backslashes, so no probe could be opened on any platform.
        Assert.Equal(expected, GdbMiSession.BuildTargetSelectCommand(endpoint));
    }

    [Fact]
    public void Probe_lock_is_exclusive_and_released_for_the_same_endpoint()
    {
        // Ports no lab hardware uses. Discovery gives a real probe's GDB and UART
        // ports one physical lock identity, so COM30/COM31 collide whenever a
        // Black Magic Probe is plugged into the machine running the tests.
        var root = Path.Combine(Path.GetTempPath(), "Iskra.Core.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var first = GdbMiSession.TryAcquireProbeLock(@"\\.\COM250", root);
            Assert.NotNull(first);
            try
            {
                Assert.Null(GdbMiSession.TryAcquireProbeLock(@"\\.\COM250", root));
                using var other = GdbMiSession.TryAcquireProbeLock(@"\\.\COM251", root);
                Assert.NotNull(other);
            }
            finally
            {
                first!.Dispose();
            }

            using var reacquired = GdbMiSession.TryAcquireProbeLock(@"\\.\COM250", root);
            Assert.NotNull(reacquired);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Probe_lock_collides_for_endpoint_aliases_with_the_same_physical_serial()
    {
        var lockNamespace = Guid.NewGuid().ToString("N");
        using var first = GdbMiSession.TryAcquireProbeLock(
            "COM30",
            lockNamespace,
            ProbeDiscovery.ResolveProbeLockIdentity("COM30", "bmp-001"));

        Assert.NotNull(first);
        Assert.Null(GdbMiSession.TryAcquireProbeLock(
            @"\\.\COM99",
            lockNamespace,
            ProbeDiscovery.ResolveProbeLockIdentity(@"\\.\COM99", " BMP-001 ")));

        using var otherPhysicalProbe = GdbMiSession.TryAcquireProbeLock(
            @"\\.\COM99",
            lockNamespace,
            ProbeDiscovery.ResolveProbeLockIdentity(@"\\.\COM99", "BMP-002"));
        Assert.NotNull(otherPhysicalProbe);
    }

    [Fact]
    public async Task Probe_lock_can_be_released_after_an_async_thread_handoff()
    {
        var lockNamespace = Guid.NewGuid().ToString("N");
        var first = GdbMiSession.TryAcquireProbeLock("COM30", lockNamespace);
        Assert.NotNull(first);

        await Task.Run(first!.Dispose);

        using var reacquired = GdbMiSession.TryAcquireProbeLock(@"\\.\COM30", lockNamespace);
        Assert.NotNull(reacquired);
    }
}
