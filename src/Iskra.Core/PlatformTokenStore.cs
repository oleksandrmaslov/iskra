using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iskra.Core;

/// <summary>
/// Selects the encrypted credential store provided by the current desktop OS.
/// There is deliberately no file-backed Unix fallback: if the native helper is
/// absent, callers receive <c>null</c> and must keep private firmware disabled.
/// </summary>
public static class PlatformTokenStoreFactory
{
    internal const string LinuxSecretToolPath = "/usr/bin/secret-tool";
    internal const string MacOsSecurityPath = "/usr/bin/security";

    public static ITokenStore? Create()
    {
        if (OperatingSystem.IsWindows()) return new TokenStore();

        var runner = new CredentialProcessRunner();
        if (OperatingSystem.IsLinux())
        {
            return runner.IsExecutableAvailable(LinuxSecretToolPath)
                ? new LinuxSecretServiceTokenStore(LinuxSecretToolPath, runner)
                : null;
        }

        if (OperatingSystem.IsMacOS())
        {
            return runner.IsExecutableAvailable(MacOsSecurityPath)
                ? new MacOsKeychainTokenStore(MacOsSecurityPath, runner)
                : null;
        }

        return null;
    }
}

/// <summary>
/// Per-user Linux credential storage backed by Secret Service/libsecret.
/// <c>secret-tool store</c> reads the token document from stdin, so access and
/// refresh tokens never appear in argv, environment variables, or a plaintext
/// staging file.
/// </summary>
internal sealed class LinuxSecretServiceTokenStore : ITokenStore
{
    private static readonly string[] Attributes =
    [
        "application", "iskra",
        "credential", "github-device-flow-v1",
    ];

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    private readonly string _executable;
    private readonly ICredentialProcessRunner _runner;
    private readonly TimeSpan _timeout;

    internal LinuxSecretServiceTokenStore(
        string executable,
        ICredentialProcessRunner runner,
        TimeSpan? timeout = null)
    {
        _executable = string.IsNullOrWhiteSpace(executable)
            ? throw new ArgumentException("secret-tool path required", nameof(executable))
            : executable;
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _timeout = timeout ?? DefaultTimeout;
    }

    public string Path => "secret-service://default/iskra/github-device-flow-v1";

    public bool Exists()
    {
        using var result = Lookup();
        if (result.ExitCode == 0) return result.StandardOutput.Length > 0;
        if (IsMissing(result)) return false;
        throw HelperFailure("lookup", result);
    }

    public StoredTokens? Load()
    {
        using var result = Lookup();
        if (IsMissing(result)) return null;
        if (result.ExitCode != 0) throw HelperFailure("lookup", result);
        if (result.StandardOutput.Length == 0)
            throw new TokenStoreException($"credential at {Path} is empty");

        return StoredTokensCodec.Deserialize(result.StandardOutput, Path);
    }

    public void Save(StoredTokens tokens)
    {
        var secret = StoredTokensCodec.Serialize(tokens);
        try
        {
            var args = new List<string> { "store", "--label=Iskra GitHub authentication" };
            args.AddRange(Attributes);
            using var result = _runner.Run(_executable, args, secret, _timeout);
            if (result.ExitCode != 0) throw HelperFailure("store", result);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public void Delete()
    {
        var args = new List<string> { "clear" };
        args.AddRange(Attributes);
        using var result = _runner.Run(_executable, args, standardInput: null, _timeout);
        if (result.ExitCode == 0 || IsMissing(result)) return;
        throw HelperFailure("clear", result);
    }

    private CredentialProcessResult Lookup()
    {
        var args = new List<string> { "lookup" };
        args.AddRange(Attributes);
        return _runner.Run(_executable, args, standardInput: null, _timeout);
    }

    // libsecret returns 1 with no diagnostic when no matching item exists.
    private static bool IsMissing(CredentialProcessResult result) =>
        result.ExitCode == 1 && result.StandardError.Length == 0;

    private TokenStoreException HelperFailure(string operation, CredentialProcessResult result) =>
        new($"Secret Service {operation} failed for {Path} "
            + $"(exit {result.ExitCode}): {result.ErrorText("no diagnostic")}");
}

/// <summary>
/// Per-user macOS credential storage backed by the login Keychain.
///
/// <para>Apple's non-interactive <c>security add-generic-password</c> accepts a
/// password only as an argv value. To avoid that exposure, Save uses the
/// tool's stdin-driven interactive mode and sends one base64-safe command.
/// Lookup and delete contain no secret input and use the ordinary commands.</para>
/// </summary>
internal sealed class MacOsKeychainTokenStore : ITokenStore
{
    private const string Service = "com.iskra.github-auth";
    private const string Account = "github-device-flow-v1";
    private const int ItemNotFoundExitCode = 44; // errSecItemNotFound (-25300) modulo 256.
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    private static readonly byte[] SavePrefix = Encoding.ASCII.GetBytes(
        $"add-generic-password -a {Account} -s {Service} "
        + "-D Iskra-GitHub-authentication -l Iskra-GitHub-authentication -w ");
    private static readonly byte[] SaveSuffix = Encoding.ASCII.GetBytes(" -U\n");

    private readonly string _executable;
    private readonly ICredentialProcessRunner _runner;
    private readonly TimeSpan _timeout;

    internal MacOsKeychainTokenStore(
        string executable,
        ICredentialProcessRunner runner,
        TimeSpan? timeout = null)
    {
        _executable = string.IsNullOrWhiteSpace(executable)
            ? throw new ArgumentException("security path required", nameof(executable))
            : executable;
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _timeout = timeout ?? DefaultTimeout;
    }

    public string Path => "keychain://login/iskra/github-device-flow-v1";

    public bool Exists()
    {
        using var result = Lookup();
        if (result.ExitCode == 0) return result.StandardOutput.Length > 0;
        if (result.ExitCode == ItemNotFoundExitCode) return false;
        throw HelperFailure("lookup", result);
    }

    public StoredTokens? Load()
    {
        using var result = Lookup();
        if (result.ExitCode == ItemNotFoundExitCode) return null;
        if (result.ExitCode != 0) throw HelperFailure("lookup", result);

        var encoded = TrimAsciiWhitespace(result.StandardOutput);
        if (encoded.Length == 0)
            throw new TokenStoreException($"credential at {Path} is empty");

        var decoded = new byte[Base64.GetMaxDecodedFromUtf8Length(encoded.Length)];
        try
        {
            var status = Base64.DecodeFromUtf8(encoded, decoded, out var consumed, out var written);
            if (status != OperationStatus.Done || consumed != encoded.Length)
                throw new TokenStoreException($"credential at {Path} is not valid base64");
            return StoredTokensCodec.Deserialize(decoded.AsSpan(0, written), Path);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }

    public void Save(StoredTokens tokens)
    {
        var json = StoredTokensCodec.Serialize(tokens);
        var encoded = new byte[Base64.GetMaxEncodedToUtf8Length(json.Length)];
        byte[]? command = null;
        try
        {
            var status = Base64.EncodeToUtf8(json, encoded, out var consumed, out var written);
            if (status != OperationStatus.Done || consumed != json.Length)
                throw new TokenStoreException("could not encode the macOS Keychain credential");

            command = new byte[SavePrefix.Length + written + SaveSuffix.Length];
            SavePrefix.CopyTo(command, 0);
            encoded.AsSpan(0, written).CopyTo(command.AsSpan(SavePrefix.Length));
            SaveSuffix.CopyTo(command, SavePrefix.Length + written);

            // -q suppresses the interactive command's secondary error line;
            // the actual Keychain diagnostic remains on stderr.
            using var result = _runner.Run(
                _executable, ["-q", "-i"], command, _timeout);
            if (result.ExitCode != 0) throw HelperFailure("store", result);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
            CryptographicOperations.ZeroMemory(encoded);
            if (command is not null) CryptographicOperations.ZeroMemory(command);
        }
    }

    public void Delete()
    {
        using var result = _runner.Run(
            _executable,
            ["delete-generic-password", "-a", Account, "-s", Service],
            standardInput: null,
            _timeout);
        if (result.ExitCode is 0 or ItemNotFoundExitCode) return;
        throw HelperFailure("delete", result);
    }

    private CredentialProcessResult Lookup() => _runner.Run(
        _executable,
        ["find-generic-password", "-a", Account, "-s", Service, "-w"],
        standardInput: null,
        _timeout);

    private TokenStoreException HelperFailure(string operation, CredentialProcessResult result) =>
        new($"macOS Keychain {operation} failed for {Path} "
            + $"(exit {result.ExitCode}): {result.ErrorText("no diagnostic")}");

    private static ReadOnlySpan<byte> TrimAsciiWhitespace(byte[] value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && IsAsciiWhitespace(value[start])) start++;
        while (end > start && IsAsciiWhitespace(value[end - 1])) end--;
        return value.AsSpan(start, end - start);
    }

    private static bool IsAsciiWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
}

/// <summary>
/// Shared codec for native stores. The snake_case document matches the existing
/// DPAPI payload so a token snapshot has one schema on every OS.
/// </summary>
internal static class StoredTokensCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static byte[] Serialize(StoredTokens tokens)
    {
        Validate(tokens);
        return JsonSerializer.SerializeToUtf8Bytes(tokens, Options);
    }

    internal static StoredTokens Deserialize(ReadOnlySpan<byte> value, string location)
    {
        try
        {
            var tokens = JsonSerializer.Deserialize<StoredTokens>(value, Options)
                ?? throw new TokenStoreException($"credential at {location} deserialised to null");
            Validate(tokens);
            return tokens;
        }
        catch (JsonException ex)
        {
            throw new TokenStoreException(
                $"credential at {location} is not valid JSON: {ex.Message}", ex);
        }
    }

    private static void Validate(StoredTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (string.IsNullOrWhiteSpace(tokens.AccessToken))
            throw new TokenStoreException("stored tokens: access_token missing");
        if (string.IsNullOrWhiteSpace(tokens.RefreshToken))
            throw new TokenStoreException("stored tokens: refresh_token missing");
    }
}

internal interface ICredentialProcessRunner
{
    bool IsExecutableAvailable(string executable);

    CredentialProcessResult Run(
        string executable,
        IReadOnlyList<string> arguments,
        byte[]? standardInput,
        TimeSpan timeout);
}

/// <summary>
/// Bounded native credential-helper runner. It never invokes a shell, never
/// puts stdin in a command line or environment variable, caps captured output,
/// and kills the full helper process tree on timeout or capture failure.
/// </summary>
internal sealed class CredentialProcessRunner : ICredentialProcessRunner
{
    private const int MaxCapturedBytes = 64 * 1024;

    public bool IsExecutableAvailable(string executable) =>
        System.IO.Path.IsPathRooted(executable) && File.Exists(executable);

    public CredentialProcessResult Run(
        string executable,
        IReadOnlyList<string> arguments,
        byte[]? standardInput,
        TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new TokenStoreException($"could not start credential helper {executable}");
        }
        catch (Exception ex) when (ex is not TokenStoreException)
        {
            throw new TokenStoreException(
                $"could not start credential helper {executable}: {ex.Message}", ex);
        }

        using var timeoutSource = new CancellationTokenSource(timeout);
        var cancellationToken = timeoutSource.Token;
        var stdoutTask = ReadBoundedAsync(
            process.StandardOutput.BaseStream, MaxCapturedBytes, cancellationToken);
        var stderrTask = ReadBoundedAsync(
            process.StandardError.BaseStream, MaxCapturedBytes, cancellationToken);
        var outputTransferred = false;

        try
        {
            if (standardInput is { Length: > 0 })
            {
                process.StandardInput.BaseStream
                    .WriteAsync(standardInput, cancellationToken)
                    .AsTask().GetAwaiter().GetResult();
                process.StandardInput.BaseStream.Flush();
            }
            process.StandardInput.Close();

            process.WaitForExitAsync(cancellationToken).GetAwaiter().GetResult();
            Task.WhenAll(stdoutTask, stderrTask)
                .WaitAsync(cancellationToken).GetAwaiter().GetResult();

            var result = new CredentialProcessResult(
                process.ExitCode,
                stdoutTask.GetAwaiter().GetResult(),
                stderrTask.GetAwaiter().GetResult());
            outputTransferred = true;
            return result;
        }
        catch (OperationCanceledException ex)
        {
            KillProcessTree(process);
            throw new TokenStoreException(
                $"credential helper {executable} timed out after {timeout.TotalSeconds:0} seconds", ex);
        }
        catch (Exception ex) when (ex is not TokenStoreException)
        {
            KillProcessTree(process);
            throw new TokenStoreException(
                $"credential helper {executable} failed: {ex.Message}", ex);
        }
        finally
        {
            if (!outputTransferred)
            {
                ZeroCompletedOutput(stdoutTask);
                ZeroCompletedOutput(stderrTask);
            }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var capture = new byte[maximumBytes];
        var overflowProbe = new byte[1];
        var written = 0;
        try
        {
            while (written < maximumBytes)
            {
                var read = await stream.ReadAsync(
                    capture.AsMemory(written, maximumBytes - written),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                written += read;
            }

            if (written == maximumBytes
                && await stream.ReadAsync(overflowProbe, cancellationToken).ConfigureAwait(false) > 0)
            {
                throw new InvalidDataException(
                    $"credential helper output exceeded {maximumBytes} bytes");
            }

            return capture.AsSpan(0, written).ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(capture);
            CryptographicOperations.ZeroMemory(overflowProbe);
        }
    }

    private static void ZeroCompletedOutput(Task<byte[]> outputTask)
    {
        _ = outputTask.ContinueWith(
            static task =>
            {
                if (task.Status == TaskStatus.RanToCompletion)
                    CryptographicOperations.ZeroMemory(task.Result);
                else
                    _ = task.Exception; // Observe a bounded-reader failure.
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort: the helper may already have exited between checks.
        }
    }
}

internal sealed class CredentialProcessResult : IDisposable
{
    internal CredentialProcessResult(int exitCode, byte[] standardOutput, byte[] standardError)
    {
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    internal int ExitCode { get; }
    internal byte[] StandardOutput { get; }
    internal byte[] StandardError { get; }

    internal string ErrorText(string fallback)
    {
        var text = Encoding.UTF8.GetString(StandardError).Trim();
        return text.Length == 0 ? fallback : text;
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(StandardOutput);
        CryptographicOperations.ZeroMemory(StandardError);
    }
}
