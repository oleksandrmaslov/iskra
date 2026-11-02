namespace Iskra.Core.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that reports as skipped, rather than run, on
/// anything other than Windows.
/// <para>This suite targets xunit v2, whose execution engine does not
/// understand v3's dynamic skip. Throwing <c>Xunit.Sdk.SkipException</c> from a
/// fixture constructor compiles, but on Linux and macOS every affected test is
/// reported as a <em>failure</em> carrying the raw
/// <c>$XunitDynamicSkip$</c> marker — which is how the whole native CI matrix
/// went red for Windows-only DPAPI coverage. Setting <see cref="FactAttribute.Skip"/>
/// is the v2-native way to express the same intent.</para>
/// </summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute(string reason = "requires Windows")
    {
        if (!OperatingSystem.IsWindows()) Skip = reason;
    }
}
