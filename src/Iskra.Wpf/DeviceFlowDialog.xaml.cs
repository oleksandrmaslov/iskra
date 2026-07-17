using System.Diagnostics;
using System.Windows;
using Iskra.Core;

namespace Iskra.Wpf;

/// <summary>
/// Modal dialog that drives the GitHub Device Flow polling loop. Shows the
/// verification URL + user code, polls in the background, and closes with
/// <c>DialogResult = true</c> on success (token in <see cref="Token"/>) or
/// <c>false</c> on cancel/error (reason in <see cref="ErrorMessage"/>).
/// </summary>
public partial class DeviceFlowDialog : Window
{
    private readonly GitHubDeviceFlow _flow;
    private readonly DeviceCodeResponse _code;
    private readonly CancellationTokenSource _cts = new();

    public TokenResponse? Token { get; private set; }
    public string? ErrorMessage { get; private set; }

    public DeviceFlowDialog(GitHubDeviceFlow flow, DeviceCodeResponse code)
    {
        InitializeComponent();
        _flow = flow;
        _code = code;
        VerificationUrl.Text = code.VerificationUri;
        UserCode.Text = code.UserCode;
        Loaded += async (_, _) => await PollAsync();
        Closing += (_, _) => _cts.Cancel();
    }

    private async Task PollAsync()
    {
        try
        {
            Token = await _flow.PollForTokenAsync(_code, _cts.Token);
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            // User clicked Cancel — DialogResult already set by Cancel_Click.
        }
        catch (GitHubAuthException ex)
        {
            ErrorMessage = ex.ErrorCode switch
            {
                "access_denied" => UiText.Get("Device.AccessDenied"),
                "expired_token" => UiText.Get("Device.CodeExpired"),
                _               => ex.Message,
            };
            StatusText.Text = $"✗ {ErrorMessage}";
            await Task.Delay(2000, CancellationToken.None);
            DialogResult = false;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusText.Text = UiText.Get("Device.Error", ex.Message);
            await Task.Delay(2000, CancellationToken.None);
            DialogResult = false;
        }
    }

    private void OpenBrowser_Click(object sender, RoutedEventArgs e)
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
            StatusText.Text = UiText.Get("Device.BrowserFailed", ex.Message);
        }
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_code.UserCode);
            StatusText.Text = UiText.Get("Device.CodeCopied");
        }
        catch (Exception ex)
        {
            StatusText.Text = UiText.Get("Device.CopyFailed", ex.Message);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        DialogResult = false;
    }
}
