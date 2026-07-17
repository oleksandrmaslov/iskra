using System.Windows;
using Iskra.Core;

namespace Iskra.Wpf;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // CurrentUICulture controls operator text only. CurrentCulture remains
        // untouched so numeric parsing, GDB commands, logs, hashes, and signed
        // data keep their existing invariant behavior.
        var settings = AppSettingsStore.Load();
        UiText.ApplyLanguage(settings.LanguageCode);

        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}

