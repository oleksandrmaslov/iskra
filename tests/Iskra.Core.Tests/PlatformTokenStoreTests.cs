using System.Text;
using System.Text.Json;

namespace Iskra.Core.Tests;

public sealed class PlatformTokenStoreTests
{
    private static readonly StoredTokens Tokens = new(
        AccessToken: "access-token-sensitive",
        RefreshToken: "refresh-token-sensitive",
        AccessTokenExpiresAtUtc: new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        RefreshTokenExpiresAtUtc: new DateTime(2030, 7, 2, 3, 4, 5, DateTimeKind.Utc),
        Scope: "repo");

    [Fact]
    public void Linux_save_sends_json_only_over_stdin()
    {
        var runner = new FakeRunner(Result(0));
        var store = new LinuxSecretServiceTokenStore("/usr/bin/secret-tool", runner);

        store.Save(Tokens);

        var request = Assert.Single(runner.Requests);
        Assert.Equal("/usr/bin/secret-tool", request.Executable);
        Assert.Equal("store", request.Arguments[0]);
        Assert.DoesNotContain(Tokens.AccessToken, string.Join(' ', request.Arguments));
        Assert.DoesNotContain(Tokens.RefreshToken, string.Join(' ', request.Arguments));
        Assert.NotNull(request.StandardInput);
        using var document = JsonDocument.Parse(request.StandardInput!);
        Assert.Equal(Tokens.AccessToken,
            document.RootElement.GetProperty("access_token").GetString());
        Assert.Equal(Tokens.RefreshToken,
            document.RootElement.GetProperty("refresh_token").GetString());
    }

    [Fact]
    public void Linux_load_round_trips_and_wipes_helper_output()
    {
        var output = StoredTokensCodec.Serialize(Tokens);
        var runner = new FakeRunner(new CredentialProcessResult(0, output, []));
        var store = new LinuxSecretServiceTokenStore("/usr/bin/secret-tool", runner);

        var loaded = store.Load();

        Assert.Equal(Tokens, loaded);
        Assert.All(output, value => Assert.Equal(0, value));
    }

    [Fact]
    public void Linux_lookup_exit_one_without_diagnostic_means_not_signed_in()
    {
        var store = new LinuxSecretServiceTokenStore(
            "/usr/bin/secret-tool",
            new FakeRunner(Result(1)));

        Assert.Null(store.Load());
    }

    [Fact]
    public void Linux_helper_failure_is_not_misreported_as_missing_credentials()
    {
        var store = new LinuxSecretServiceTokenStore(
            "/usr/bin/secret-tool",
            new FakeRunner(Result(1, error: "Cannot autolaunch D-Bus")));

        var error = Assert.Throws<TokenStoreException>(() => store.Load());

        Assert.Contains("Cannot autolaunch D-Bus", error.Message);
    }

    [Fact]
    public void Mac_save_uses_interactive_stdin_and_never_places_tokens_in_argv()
    {
        var runner = new FakeRunner(Result(0));
        var store = new MacOsKeychainTokenStore("/usr/bin/security", runner);

        store.Save(Tokens);

        var request = Assert.Single(runner.Requests);
        Assert.Equal(["-q", "-i"], request.Arguments);
        var arguments = string.Join(' ', request.Arguments);
        Assert.DoesNotContain(Tokens.AccessToken, arguments);
        Assert.DoesNotContain(Tokens.RefreshToken, arguments);

        var command = Encoding.ASCII.GetString(request.StandardInput!);
        Assert.DoesNotContain(Tokens.AccessToken, command);
        Assert.DoesNotContain(Tokens.RefreshToken, command);
        var encoded = command.Split(" -w ", StringSplitOptions.None)[1]
            .Split(" -U", StringSplitOptions.None)[0];
        var json = Convert.FromBase64String(encoded);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(Tokens.AccessToken,
            document.RootElement.GetProperty("access_token").GetString());
    }

    [Fact]
    public void Mac_load_decodes_keychain_value_and_wipes_helper_output()
    {
        var json = StoredTokensCodec.Serialize(Tokens);
        var encoded = Encoding.ASCII.GetBytes(Convert.ToBase64String(json) + "\n");
        Array.Clear(json);
        var store = new MacOsKeychainTokenStore(
            "/usr/bin/security",
            new FakeRunner(new CredentialProcessResult(0, encoded, [])));

        var loaded = store.Load();

        Assert.Equal(Tokens, loaded);
        Assert.All(encoded, value => Assert.Equal(0, value));
    }

    [Fact]
    public void Mac_item_not_found_exit_is_a_normal_logged_out_state()
    {
        var store = new MacOsKeychainTokenStore(
            "/usr/bin/security",
            new FakeRunner(Result(44, error: "item not found")));

        Assert.Null(store.Load());
    }

    [Fact]
    public void Mac_invalid_keychain_payload_fails_closed()
    {
        var store = new MacOsKeychainTokenStore(
            "/usr/bin/security",
            new FakeRunner(new CredentialProcessResult(
                0, Encoding.ASCII.GetBytes("not-base64\n"), [])));

        Assert.Throws<TokenStoreException>(() => store.Load());
    }

    [Fact]
    public void Credential_runner_enforces_a_hard_timeout()
    {
        string executable;
        string[] arguments;
        if (OperatingSystem.IsWindows())
        {
            executable = Environment.GetEnvironmentVariable("ComSpec")!;
            arguments = ["/d", "/c", "ping -n 6 127.0.0.1 >nul"];
        }
        else
        {
            executable = "/bin/sh";
            arguments = ["-c", "sleep 5"];
        }

        var runner = new CredentialProcessRunner();
        var error = Assert.Throws<TokenStoreException>(() =>
            runner.Run(executable, arguments, standardInput: null, TimeSpan.FromMilliseconds(100)));

        Assert.Contains("timed out", error.Message);
    }

    private static CredentialProcessResult Result(
        int exitCode,
        string output = "",
        string error = "") =>
        new(
            exitCode,
            Encoding.UTF8.GetBytes(output),
            Encoding.UTF8.GetBytes(error));

    private sealed class FakeRunner(params CredentialProcessResult[] results)
        : ICredentialProcessRunner
    {
        private readonly Queue<CredentialProcessResult> _results = new(results);

        internal List<Request> Requests { get; } = [];

        public bool IsExecutableAvailable(string executable) => true;

        public CredentialProcessResult Run(
            string executable,
            IReadOnlyList<string> arguments,
            byte[]? standardInput,
            TimeSpan timeout)
        {
            Requests.Add(new Request(
                executable,
                [.. arguments],
                standardInput is null ? null : [.. standardInput],
                timeout));
            return _results.Dequeue();
        }
    }

    private sealed record Request(
        string Executable,
        string[] Arguments,
        byte[]? StandardInput,
        TimeSpan Timeout);
}
