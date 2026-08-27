using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Iskra.Core;

namespace Iskra.Desktop;

public sealed partial class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindowViewModel viewModel;
            try
            {
                _ = AppSettingsStore.Load();
                viewModel = new MainWindowViewModel();
            }
            catch (AppSettingsLoadException ex)
            {
                desktop.MainWindow = BuildSettingsErrorWindow(ex);
                base.OnFrameworkInitializationCompleted();
                return;
            }

            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            // Fire-and-forget startup check against the locked catalog source.
            // It only raises a notice; the operator decides when to reload, so a
            // station mid-batch never has its catalog swapped underneath it.
            _ = viewModel.BackgroundFetchCatalogAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static Window BuildSettingsErrorWindow(AppSettingsLoadException ex) => new()
    {
        Title = "Iskra — settings error",
        Width = 720,
        Height = 320,
        CanResize = true,
        Content = new TextBlock
        {
            Margin = new Thickness(28),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 17,
            Text =
                "Налаштування Iskra пошкоджені або недоступні. Файл збережено без змін. " +
                "Виправте або видаліть його перед запуском.\n\n" +
                "Iskra settings are corrupt or unreadable. The file was preserved unchanged. " +
                "Repair or remove it before starting.\n\n" +
                $"{ex.SettingsPath}\n{ex.InnerException?.Message ?? ex.Message}",
        },
    };
}
