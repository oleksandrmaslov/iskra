using System.Text;

namespace Iskra.Core;

internal readonly record struct BoundedProcessLine(string Text, bool WasTruncated);

/// <summary>
/// Reads child-process text in fixed-size chunks without ever asking
/// <see cref="TextReader.ReadLineAsync()"/> to allocate an attacker-sized
/// string. Characters beyond the per-line ceiling are drained and discarded
/// until the next line boundary.
/// </summary>
internal static class BoundedProcessLineReader
{
    private const int ReadBufferChars = 2_048;

    internal static async Task ReadAsync(
        TextReader reader,
        Action<BoundedProcessLine> onLine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(onLine);

        var buffer = new char[ReadBufferChars];
        var current = new StringBuilder(Math.Min(
            ReadBufferChars,
            GdbProcess.MaxCapturedLineChars));
        var truncated = false;
        var skipLfAfterCr = false;

        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;

            for (var index = 0; index < read; index++)
            {
                var value = buffer[index];
                if (skipLfAfterCr)
                {
                    skipLfAfterCr = false;
                    if (value == '\n') continue;
                }

                if (value is '\r' or '\n')
                {
                    Emit(current, truncated, onLine);
                    current.Clear();
                    truncated = false;
                    skipLfAfterCr = value == '\r';
                    continue;
                }

                if (current.Length < GdbProcess.MaxCapturedLineChars)
                    current.Append(value);
                else
                    truncated = true;
            }
        }

        if (current.Length > 0 || truncated)
            Emit(current, truncated, onLine);
    }

    private static void Emit(
        StringBuilder current,
        bool truncated,
        Action<BoundedProcessLine> onLine)
    {
        var text = current.ToString();
        if (truncated) text = BoundedGdbLineBuffer.MarkTruncated(text);
        onLine(new BoundedProcessLine(text, truncated));
    }
}
