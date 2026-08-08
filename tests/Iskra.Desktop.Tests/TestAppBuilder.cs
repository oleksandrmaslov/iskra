using Avalonia;
using Avalonia.Headless;
using Iskra.Desktop;
using Iskra.Desktop.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Iskra.Desktop.Tests;

/// <summary>
/// Boots the real <see cref="App"/> on Avalonia's headless platform so view
/// models run against a genuine dispatcher and styling system without needing a
/// display. Tests marked <c>[AvaloniaFact]</c> execute on that UI thread.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
