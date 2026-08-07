using System.Diagnostics;
using Avalonia.Controls;
// Avalonia 12 moved SetTextAsync onto ClipboardExtensions in this namespace.
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Iskra.Core;

namespace Iskra.Desktop;

/// <summary>
/// Modal window that drives the GitHub Device Flow polling loop. Shows the
/// verification URL and user code, polls in the background, and closes with the
/// <see cref="TokenResponse"/> on success or <c>null</c> on cancel/error, with
/// the reason left in <see cref="ErrorMessage"/>.
/// </summary>
public sealed partial class DeviceFlowWindow : Window
{
    private readonly GitHubDeviceFlow _flow = null!;
    private readonly DeviceCodeResponse _code = null!;
    private readonly DesktopText _text = DesktopLocalization.For(null);
    private readonly CancellationTokenSource _cts = new();
    private bool _closing;

    public TokenResponse? Token { get; private set; }
    public string? ErrorMessage { get; private set; }

    // Parameterless ctor exists for the XAML previewer only.
    public DeviceFlowWindow() => InitializeComponent();

    public DeviceFlowWindow(GitHubDeviceFlow flow, DeviceCodeResponse code, DesktopText text)
    {
        InitializeComponent();
        _flow = flow ?? throw new ArgumentNullException(nameof(flow));
        _code = code ?? throw new ArgumentNullException(nameof(code));
        _text = text ?? throw new ArgumentNullException(nameof(text));

        Title = _text.AuthSignIn;
        TitleText.Text = _text.DeviceTitle;
        Step1Text.Text = _text.DeviceStep1;
        Step2Text.Text = _text.DeviceStep2;
        OpenBrowserButton.Content = _text.DeviceOpenBrowser;
        CopyCodeButton.Content = _text.DeviceCopyCode;
        CancelButton.Content = _text.Cancel;
        StatusText.Text = _text.DeviceWaiting;
        VerificationUrl.Text = code.VerificationUri;
        UserCode.Text = code.UserCode;

        Opened += async (_, _) => await PollAsync();
        Closing += (_, _) => _cts.Cancel();
    }

    private async Task PollAsync()
    {
        try
        {
            Token = await _flow.PollForTokenAsync(_code, _cts.Token);
            CloseOnce();
        }
        catch (OperationCanceledException)
        {
            // Cancel button already closed the window.
        }
        catch (GitHubAuthException ex)
        {
            ErrorMessage = ex.ErrorCode switch
            {
                "access_denied" => _text.DeviceAccessDenied,
                "expired_token" => _text.DeviceCodeExpired,
                _ => ex.Message,
            };
            await FailAsync($"✗ {ErrorMessage}");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            await FailAsync(_text.DeviceError(ex.Message));
        }
    }

    private async Task FailAsync(string status)
    {
        StatusText.Text = status;
        // Leave the reason on screen briefly; the caller also surfaces it.
        await Task.Delay(2000, CancellationToken.None);
        CloseOnce();
    }

    private void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    private void OpenBrowser_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _code.VerificationUri,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = _text.DeviceBrowserFailed(ex.Message);
        }
    }

    private async void CopyCode_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var clipboard = GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                StatusText.Text = _text.DeviceCopyFailed("clipboard unavailable");
                return;
            }

            await clipboard.SetTextAsync(_code.UserCode);
            StatusText.Text = _text.DeviceCodeCopied;
        }
        catch (Exception ex)
        {
            StatusText.Text = _text.DeviceCopyFailed(ex.Message);
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        CloseOnce();
    }
}
