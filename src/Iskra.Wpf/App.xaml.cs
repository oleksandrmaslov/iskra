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
        AppSettings settings;
        try
        {
            settings = AppSettingsStore.Load();
        }
        catch (AppSettingsLoadException ex)
        {
            MessageBox.Show(
                "Налаштування Iskra пошкоджені або недоступні. Файл збережено без змін. " +
                "Виправте або видаліть його перед запуском.\n\n" +
                "Iskra settings are corrupt or unreadable. The file was preserved unchanged. " +
                "Repair or remove it before starting.\n\n" +
                $"{ex.SettingsPath}\n{ex.InnerException?.Message ?? ex.Message}",
                "Iskra — settings error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(2);
            return;
        }
        UiText.ApplyLanguage(settings.LanguageCode);

        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}

