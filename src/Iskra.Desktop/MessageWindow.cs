using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Iskra.Desktop;

/// <summary>
/// Minimal owned modal for the few blocking notices the operator app shows.
/// Built in code rather than XAML because it carries no bindings or styling
/// beyond the window chrome.
/// </summary>
internal sealed class MessageWindow : Window
{
    public MessageWindow() : this("Iskra", string.Empty)
    {
    }

    public MessageWindow(string title, string message)
    {
        Title = title;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 460;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#FFF4F6F9"));

        var okButton = new Button
        {
            Content = "OK",
            Padding = new Avalonia.Thickness(24, 7),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true,
        };
        okButton.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24, 20),
            Spacing = 18,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Color.Parse("#FF162033")),
                },
                okButton,
            },
        };
    }
}
