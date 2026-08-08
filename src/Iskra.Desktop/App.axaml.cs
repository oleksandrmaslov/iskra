using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Iskra.Desktop;

public sealed partial class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainWindowViewModel();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            // Fire-and-forget startup check against the locked catalog source.
            // It only raises a notice; the operator decides when to reload, so a
            // station mid-batch never has its catalog swapped underneath it.
            _ = viewModel.BackgroundFetchCatalogAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
