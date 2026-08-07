using System.ComponentModel;
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
    }

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
