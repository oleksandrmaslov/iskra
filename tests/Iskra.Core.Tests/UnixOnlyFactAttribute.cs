namespace Iskra.Core.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that reports as skipped, rather than run, on
/// Windows. The counterpart of <see cref="WindowsOnlyFactAttribute"/>, for
/// fixtures that need POSIX file-system behaviour such as relative symlinks or
/// ':' in file names.
/// </summary>
public sealed class UnixOnlyFactAttribute : FactAttribute
{
    public UnixOnlyFactAttribute(string reason = "requires a Unix file system")
    {
        if (OperatingSystem.IsWindows()) Skip = reason;
    }
}
