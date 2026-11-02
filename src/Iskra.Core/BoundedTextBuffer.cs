using System.Text;

namespace Iskra.Core;

/// <summary>
/// Keeps the newest diagnostic text within a fixed character budget. This is
/// intended for live process consoles: retaining output is useful, but a noisy
/// or malicious child process must not grow the operator UI without bound.
/// </summary>
public sealed class BoundedTextBuffer
{
    public const int DefaultMaxChars = 512 * 1024;
    private const string TruncationMarker = "[… older output truncated …]\n";

    private readonly StringBuilder _text = new();

    public BoundedTextBuffer(int maxChars = DefaultMaxChars)
    {
        if (maxChars <= TruncationMarker.Length)
            throw new ArgumentOutOfRangeException(nameof(maxChars));
        MaxChars = maxChars;
    }

    public int MaxChars { get; }
    public int Length => _text.Length;

    public void Clear() => _text.Clear();

    public string AppendLine(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var availableForValue = MaxChars - TruncationMarker.Length - 1;
        if (value.Length > availableForValue)
            value = value[^availableForValue..];

        _text.AppendLine(value);
        TrimOldestLines();
        return _text.ToString();
    }

    public override string ToString() => _text.ToString();

    private void TrimOldestLines()
    {
        if (_text.Length <= MaxChars) return;

        var removeAtLeast = _text.Length - MaxChars + TruncationMarker.Length;
        var removeThrough = removeAtLeast;
        while (removeThrough < _text.Length && _text[removeThrough] != '\n')
            removeThrough++;
        if (removeThrough < _text.Length)
            removeThrough++;

        _text.Remove(0, removeThrough);
        _text.Insert(0, TruncationMarker);
        if (_text.Length > MaxChars)
            _text.Remove(TruncationMarker.Length, _text.Length - MaxChars);
    }
}
