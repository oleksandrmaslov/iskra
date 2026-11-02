using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Iskra.Desktop;

namespace Iskra.Desktop.Tests;

/// <summary>
/// The user code panel is dark navy. Any control placed on it must be legible
/// there, which the theme's default button styling does not guarantee: Fluent's
/// light-theme button fill is a translucent white, so on a dark parent it
/// composites down toward the panel colour and leaves near-black label text on
/// dark blue.
/// </summary>
public class DeviceFlowContrastTests
{
    /// <summary>WCAG 2.1 minimum contrast for normal-size text.</summary>
    private const double MinimumContrast = 4.5;

    [AvaloniaFact]
    public void Copy_button_is_legible_on_the_dark_code_panel()
    {
        var window = new DeviceFlowWindow();
        window.Show();

        var button = Assert.IsType<Button>(window.FindControl<Button>("CopyCodeButton"));
        var panel = Assert.IsType<Border>(FindAncestorBorder(button));

        var panelColor = SolidColor(panel.Background);
        var buttonFill = Composite(SolidColor(button.Background), panelColor);
        var textColor = Composite(SolidColor(button.Foreground), buttonFill);
        var contrast = ContrastRatio(textColor, buttonFill);

        Assert.True(
            contrast >= MinimumContrast,
            $"copy button label {Describe(textColor)} on {Describe(buttonFill)} "
            + $"(panel {Describe(panelColor)}) has contrast {contrast:F2}:1, "
            + $"below the {MinimumContrast}:1 minimum");
    }

    [AvaloniaFact]
    public void User_code_itself_is_legible_on_the_dark_code_panel()
    {
        var window = new DeviceFlowWindow();
        window.Show();

        var code = Assert.IsType<SelectableTextBlock>(
            window.FindControl<SelectableTextBlock>("UserCode"));
        var panel = Assert.IsType<Border>(FindAncestorBorder(code));

        var panelColor = SolidColor(panel.Background);
        var textColor = Composite(SolidColor(code.Foreground), panelColor);

        Assert.True(
            ContrastRatio(textColor, panelColor) >= MinimumContrast,
            $"user code {Describe(textColor)} on {Describe(panelColor)} is not legible");
    }

    private static Border? FindAncestorBorder(Control control)
    {
        for (var parent = control.Parent; parent is not null; parent = parent.Parent)
            if (parent is Border border && border.Background is not null) return border;
        return null;
    }

    private static Color SolidColor(IBrush? brush) => brush switch
    {
        ISolidColorBrush solid => Color.FromArgb(
            (byte)Math.Clamp(Math.Round(solid.Color.A * solid.Opacity), 0, 255),
            solid.Color.R,
            solid.Color.G,
            solid.Color.B),
        // No brush of our own means the parent shows through unchanged.
        null => Colors.Transparent,
        _ => Colors.Transparent,
    };

    /// <summary>Alpha-composites <paramref name="over"/> onto an opaque backdrop.</summary>
    private static Color Composite(Color over, Color backdrop)
    {
        var alpha = over.A / 255.0;
        return Color.FromRgb(
            (byte)Math.Round(over.R * alpha + backdrop.R * (1 - alpha)),
            (byte)Math.Round(over.G * alpha + backdrop.G * (1 - alpha)),
            (byte)Math.Round(over.B * alpha + backdrop.B * (1 - alpha)));
    }

    private static double ContrastRatio(Color a, Color b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        var (hi, lo) = la >= lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double RelativeLuminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static string Describe(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
}
