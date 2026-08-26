using System.Runtime.Versioning;
using System.Security.Cryptography;
using Iskra.Core;

namespace Iskra.Core.Tests;

/// <summary>
/// Round-trip tests for <see cref="TokenStore"/>. Use <see cref="DataProtectionScope.CurrentUser"/>
/// + a temp-file override so the suite doesn't pollute %PROGRAMDATA% or
/// need admin. Production wiring uses <c>LocalMachine</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public class TokenStoreTests : IDisposable
{
    private readonly string _path;

    public TokenStoreTests()
    {
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip("DPAPI token-store tests require Windows");

        _path = Path.Combine(Path.GetTempPath(),
            $"iskra-tokenstore-{Guid.NewGuid():N}.bin");
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        var tmp = _path + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
    }

    private TokenStore NewStore() => new(_path, DataProtectionScope.CurrentUser);

    private static StoredTokens SampleTokens(DateTime? now = null)
    {
        var t = now ?? new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc);
        return new StoredTokens(
            AccessToken:              "gho_ABC",
            RefreshToken:             "ghr_DEF",
            AccessTokenExpiresAtUtc:  t.AddHours(8),
            RefreshTokenExpiresAtUtc: t.AddMonths(6),
            Scope:                    "");
    }

    [Fact]
    public void Save_then_Load_round_trips_all_fields()
    {
        var store = NewStore();
        var original = SampleTokens();

        store.Save(original);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(original.AccessToken,              loaded!.AccessToken);
        Assert.Equal(original.RefreshToken,             loaded.RefreshToken);
        Assert.Equal(original.AccessTokenExpiresAtUtc,  loaded.AccessTokenExpiresAtUtc);
        Assert.Equal(original.RefreshTokenExpiresAtUtc, loaded.RefreshTokenExpiresAtUtc);
        Assert.Equal(original.Scope,                    loaded.Scope);
    }

    [Fact]
    public void Save_overwrites_existing_blob()
    {
        var store = NewStore();
        store.Save(SampleTokens() with { AccessToken = "first_at", RefreshToken = "first_rt" });
        store.Save(SampleTokens() with { AccessToken = "second_at", RefreshToken = "second_rt" });

        var loaded = store.Load()!;
        Assert.Equal("second_at", loaded.AccessToken);
        Assert.Equal("second_rt", loaded.RefreshToken);
    }

    [Fact]
    public void Load_returns_null_when_file_missing()
    {
        Assert.Null(NewStore().Load());
    }

    [Fact]
    public void Exists_reflects_file_presence()
    {
        var store = NewStore();
        Assert.False(store.Exists());
        store.Save(SampleTokens());
        Assert.True(store.Exists());
    }

    [Fact]
    public void Delete_removes_the_file()
    {
        var store = NewStore();
        store.Save(SampleTokens());
        Assert.True(store.Exists());

        store.Delete();
        Assert.False(store.Exists());
    }

    [Fact]
    public void Delete_on_missing_file_is_a_no_op()
    {
        var store = NewStore();
        Assert.False(store.Exists());
        store.Delete(); // must not throw
    }

    [Fact]
    public void Load_throws_on_corrupted_cipher()
    {
        var store = NewStore();
        store.Save(SampleTokens());
        // Flip a byte deep in the cipher so DPAPI integrity check fails.
        var bytes = File.ReadAllBytes(_path);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(_path, bytes);

        var ex = Assert.Throws<TokenStoreException>(() => store.Load());
        Assert.Contains("could not be decrypted", ex.Message);
        Assert.IsType<CryptographicException>(ex.InnerException);
    }

    [Fact]
    public void Load_rejects_an_oversized_encrypted_blob()
    {
        File.WriteAllBytes(_path, new byte[TokenStore.MaxEncryptedTokenBytes + 1]);

        var ex = Assert.Throws<TokenStoreException>(() => NewStore().Load());

        Assert.Contains("could not read", ex.Message);
        Assert.IsType<FileSizeLimitExceededException>(ex.InnerException);
    }

    [Fact]
    public void Save_rejects_tokens_with_empty_access_token()
    {
        var store = NewStore();
        var bad = SampleTokens() with { AccessToken = "" };
        Assert.Throws<TokenStoreException>(() => store.Save(bad));
        Assert.False(store.Exists()); // nothing partial on disk
    }

    [Fact]
    public void Save_rejects_tokens_with_empty_refresh_token()
    {
        var store = NewStore();
        var bad = SampleTokens() with { RefreshToken = "" };
        Assert.Throws<TokenStoreException>(() => store.Save(bad));
    }

    [Fact]
    public void Save_creates_parent_directory_if_missing()
    {
        var nested = Path.Combine(Path.GetTempPath(),
            $"iskra-{Guid.NewGuid():N}", "sub", "auth.bin");
        try
        {
            var store = new TokenStore(nested, DataProtectionScope.CurrentUser);
            store.Save(SampleTokens());
            Assert.True(File.Exists(nested));
        }
        finally
        {
            var dir = Path.GetDirectoryName(Path.GetDirectoryName(nested));
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Save_does_not_leave_temp_file_behind()
    {
        var store = NewStore();
        store.Save(SampleTokens());
        Assert.False(File.Exists(_path + ".tmp"));
        Assert.Empty(Directory.GetFiles(
            Path.GetDirectoryName(_path)!,
            Path.GetFileName(_path) + ".*.tmp"));
    }

    [Fact]
    public void DefaultPath_is_under_ProgramData_Iskra()
    {
        var p = TokenStore.DefaultPath();
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        Assert.StartsWith(programData, p);
        Assert.EndsWith(@"Iskra\auth.bin", p);
    }

    [Fact]
    public void From_TokenResponse_computes_expiry_timestamps()
    {
        var now = new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc);
        var resp = new TokenResponse(
            AccessToken: "gho_X", TokenType: "bearer",
            ExpiresIn: 28800, RefreshToken: "ghr_Y",
            RefreshTokenExpiresIn: 15897600, Scope: "");

        var stored = StoredTokens.From(resp, now);

        Assert.Equal("gho_X", stored.AccessToken);
        Assert.Equal("ghr_Y", stored.RefreshToken);
        Assert.Equal(now.AddSeconds(28800),   stored.AccessTokenExpiresAtUtc);
        Assert.Equal(now.AddSeconds(15897600), stored.RefreshTokenExpiresAtUtc);
    }

    [Fact]
    public void AccessTokenIsFresh_returns_true_well_before_expiry()
    {
        var now = new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc);
        var t = SampleTokens(now);
        Assert.True(t.AccessTokenIsFresh(now,                          TimeSpan.FromMinutes(1)));
        Assert.True(t.AccessTokenIsFresh(now.AddHours(7),              TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void AccessTokenIsFresh_returns_false_inside_skew_window()
    {
        var now = new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc);
        var t = SampleTokens(now); // access expires at now+8h
        Assert.False(t.AccessTokenIsFresh(now.AddHours(8),              TimeSpan.FromMinutes(1)));
        Assert.False(t.AccessTokenIsFresh(now.AddHours(8).AddMinutes(-30),
                                           TimeSpan.FromHours(1)));
    }

    [Fact]
    public void RefreshTokenIsExpired_uses_now_against_refresh_expiry()
    {
        var now = new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc);
        var t = SampleTokens(now);
        Assert.False(t.RefreshTokenIsExpired(now));
        Assert.False(t.RefreshTokenIsExpired(now.AddMonths(5)));
        Assert.True(t.RefreshTokenIsExpired(now.AddMonths(7)));
    }
}
