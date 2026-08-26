using System.Buffers;
using System.Text;

namespace Iskra.Core;

public sealed class HttpContentSizeLimitException(long maximumBytes)
    : IOException($"HTTP response exceeded the {maximumBytes}-byte limit");

/// <summary>Streams HTTP bodies through an explicit byte ceiling.</summary>
public static class BoundedHttpContent
{
    public static async Task<string> ReadUtf8StringAsync(
        HttpContent content,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        await CopyToAsync(content, buffer, maximumBytes, cancellationToken)
            .ConfigureAwait(false);
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    public static async Task CopyToAsync(
        HttpContent content,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(destination);
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (content.Headers.ContentLength is { } declared && declared > maximumBytes)
            throw new HttpContentSizeLimitException(maximumBytes);

        await using var source = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var rented = ArrayPool<byte>.Shared.Rent(81920);
        long total = 0;
        try
        {
            while (true)
            {
                var remaining = maximumBytes - total;
                var allowed = remaining >= rented.Length
                    ? rented.Length
                    : checked((int)remaining + 1);
                var read = await source.ReadAsync(
                    rented.AsMemory(0, allowed), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                if (total > maximumBytes)
                    throw new HttpContentSizeLimitException(maximumBytes);
                await destination.WriteAsync(
                    rented.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }
}
