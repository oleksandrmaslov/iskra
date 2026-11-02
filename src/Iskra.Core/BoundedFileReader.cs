namespace Iskra.Core;

/// <summary>
/// Reads a file into one stable in-memory snapshot without ever consuming more
/// than the configured maximum plus the single byte needed to detect an
/// over-limit file. The open handle also prevents in-place writers on Windows;
/// replacing a path on Unix does not change the already-open file descriptor.
/// </summary>
public static class BoundedFileReader
{
    public static string ReadUtf8String(string path, int maxBytes) =>
        System.Text.Encoding.UTF8.GetString(ReadAllBytes(path, maxBytes));

    public static byte[] ReadAllBytes(string path, int maxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));

        using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);

        if (input.Length > maxBytes)
            throw new FileSizeLimitExceededException(path, maxBytes);

        var initialCapacity = (int)Math.Min(input.Length, maxBytes);
        using var output = new MemoryStream(initialCapacity);
        var buffer = new byte[(int)Math.Min(81920L, (long)maxBytes + 1)];
        var total = 0;

        while (true)
        {
            // Read at most one byte beyond the limit, even if the file grows
            // after the initial Length check.
            var readLimit = Math.Min(buffer.Length, maxBytes - total + 1);
            var read = input.Read(buffer, 0, readLimit);
            if (read == 0)
                return output.ToArray();

            total += read;
            if (total > maxBytes)
                throw new FileSizeLimitExceededException(path, maxBytes);

            output.Write(buffer, 0, read);
        }
    }
}

public sealed class FileSizeLimitExceededException(string path, int maxBytes)
    : IOException($"file exceeds the {maxBytes}-byte limit: {path}");
