using FlashlightApp.Core;

namespace FlashlightApp.Core.Tests;

public class FirmwareIntegrityTests
{
    [Fact]
    public void ComputeSha256Hex_matches_known_value_for_empty_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"empty-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(path, Array.Empty<byte>());
            // SHA-256 of the empty string is e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
            Assert.Equal(
                "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                FirmwareIntegrity.ComputeSha256Hex(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ComputeSha256Hex_matches_known_value_for_short_input()
    {
        var path = Path.Combine(Path.GetTempPath(), $"abc-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(path, new byte[] { (byte)'a', (byte)'b', (byte)'c' });
            // SHA-256("abc") = ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad
            Assert.Equal(
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                FirmwareIntegrity.ComputeSha256Hex(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde",  false)] // 63
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdefa", false)] // 65
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdez", false)] // bad char
    [InlineData("",      false)]
    [InlineData(null,    false)]
    [InlineData("unknown", false)]
    public void IsValidSha256Hex_accepts_only_64_hex_chars(string? s, bool expected)
    {
        Assert.Equal(expected, FirmwareIntegrity.IsValidSha256Hex(s));
    }

    [Fact]
    public void HashesMatch_case_insensitive()
    {
        var lo = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        var hi = lo.ToUpperInvariant();
        Assert.True(FirmwareIntegrity.HashesMatch(lo, hi));
        Assert.True(FirmwareIntegrity.HashesMatch(hi, lo));
    }

    [Fact]
    public void HashesMatch_returns_false_on_actual_mismatch()
    {
        var a = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        var b = "cf83e1357eefb8bdf1542850d66d8007d620e4050b5715dc83f4a921d36ce9ce";
        Assert.False(FirmwareIntegrity.HashesMatch(a, b));
    }

    [Fact]
    public void HashesMatch_returns_false_when_either_side_is_invalid()
    {
        var good = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        Assert.False(FirmwareIntegrity.HashesMatch(good, "unknown"));
        Assert.False(FirmwareIntegrity.HashesMatch("garbage", good));
    }
}
