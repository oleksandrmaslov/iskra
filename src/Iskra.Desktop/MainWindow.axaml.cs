using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Iskra.Core;

namespace Iskra.Desktop;

public sealed partial class MainWindow : Window
{
    private const int FlashTabIndex = 0;
    private const int SettingsTabIndex = 3;

    private MainWindowViewModel? _viewModel;
    private int _previousTabIndex;

    public MainWindow()
    {
        InitializeComponent();

        // Tunnel so the operator hotkey fires even while the Operator or Batch
        // box has focus. Barcode scanners terminate a scan with Enter, which is
        // what makes "scan batch -> flash" a single swipe.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += OnDataContextChanged;
        MainTabs.SelectionChanged += OnTabSelectionChanged;
        Closing += OnWindowClosing;
        Opened += OnWindowOpened;
    }

    /// <summary>
    /// Clamp to the screen the window actually landed on. The designed size is
    /// only a preference: on a 1366x768 panel, or a 1080p screen at 125%
    /// scaling, an unclamped window puts its own title bar off the top edge and
    /// leaves the operator unable to move or resize it.
    /// </summary>
    private void OnWindowOpened(object? sender, EventArgs e)
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;

        // WorkingArea excludes the taskbar; it is in physical pixels, so convert
        // through the screen's own scaling rather than assuming 1.0.
        var scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        var availableWidth = screen.WorkingArea.Width / scale;
        var availableHeight = screen.WorkingArea.Height / scale;

        // Leave a margin so the frame and shadow stay grabbable.
        var width = Math.Min(Width, Math.Max(MinWidth, availableWidth - 40));
        var height = Math.Min(Height, Math.Max(MinHeight, availableHeight - 40));
        if (Math.Abs(width - Width) < 0.5 && Math.Abs(height - Height) < 0.5) return;

        Width = width;
        Height = height;
        Position = new PixelPoint(
            screen.WorkingArea.X + (int)((availableWidth - width) / 2 * scale),
            screen.WorkingArea.Y + (int)((availableHeight - height) / 2 * scale));
    }

    private void FullScreen_Click(object? sender, RoutedEventArgs e) => ToggleFullScreen();

    /// <summary>
    /// Full screen is the intended factory-floor mode: the operator sees only
    /// the PASS/FAIL band and the FLASH button, with no desktop behind it.
    /// </summary>
    private void ToggleFullScreen() =>
        WindowState = WindowState == WindowState.FullScreen
            ? WindowState.Normal
            : WindowState.FullScreen;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Dialogs = null;
        }

        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.Dialogs = new WindowDesktopDialogs(this);
        }

        _previousTabIndex = MainTabs.SelectedIndex;
    }

    /// <summary>
    /// Auto-save on leaving the Settings tab, matching WPF. Edits are committed
    /// without an extra click, and a validation failure is left visible on the
    /// tab the operator just left rather than silently discarded.
    /// </summary>
    private void OnTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_previousTabIndex == SettingsTabIndex && MainTabs.SelectedIndex != SettingsTabIndex)
            _viewModel?.SaveSettingsIfDirty();

        _previousTabIndex = MainTabs.SelectedIndex;
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e) =>
        _viewModel?.SaveSettingsIfDirty();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.GdbLogText)) return;
        ScrollGdbLogToEnd();
    }

    private void ScrollGdbLogToEnd()
    {
        GdbLogBox.CaretIndex = GdbLogBox.Text?.Length ?? 0;
        GdbLogBox.GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault()
            ?.ScrollToEnd();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // F11 everywhere, and Escape to leave full screen — the conventional
        // pair, and Escape matters because a full-screen window hides the
        // close button an operator would otherwise reach for.
        if (e.Key == Key.F11)
        {
            e.Handled = true;
            ToggleFullScreen();
            return;
        }

        if (e.Key == Key.Escape && WindowState == WindowState.FullScreen)
        {
            e.Handled = true;
            WindowState = WindowState.Normal;
            return;
        }

        if (_viewModel is null || _viewModel.FlashHotkey == FlashHotkey.None) return;
        if (MainTabs.SelectedIndex != FlashTabIndex) return;
        if (!MatchesHotkey(_viewModel.FlashHotkey, e.Key)) return;

        // Space must still type a space inside the operator/batch boxes.
        if (_viewModel.FlashHotkey == FlashHotkey.Space
            && TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox { IsReadOnly: false })
        {
            return;
        }

        if (!_viewModel.FlashCommand.CanExecute(null)) return;
        e.Handled = true;
        _viewModel.FlashCommand.Execute(null);
    }

    private static bool MatchesHotkey(FlashHotkey hotkey, Key key) => hotkey switch
    {
        FlashHotkey.Space => key == Key.Space,
        FlashHotkey.Enter => key == Key.Enter,
        FlashHotkey.F2 => key == Key.F2,
        FlashHotkey.F5 => key == Key.F5,
        _ => false,
    };
}
