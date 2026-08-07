using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Iskra.Core;

namespace Iskra.Desktop;

/// <summary>
/// Window-owned interactions the view model needs but must not own itself:
/// native file pickers, the modal Device Flow window, and message boxes. The
/// view model calls through this seam so it stays constructible in a test
/// without a display.
/// </summary>
public interface IDesktopDialogs
{
    Task<string?> OpenFileAsync(string title, string filterName, IReadOnlyList<string> patterns);
    Task<string?> SaveFileAsync(string title, string filterName, IReadOnlyList<string> patterns, string suggestedName);
    Task<DeviceFlowOutcome> RunDeviceFlowAsync(GitHubDeviceFlow flow, DeviceCodeResponse code, DesktopText text);
    Task ShowMessageAsync(string title, string message);
}

public sealed record DeviceFlowOutcome(TokenResponse? Token, string? ErrorMessage)
{
    public bool IsSuccess => Token is not null;
}

internal sealed class WindowDesktopDialogs(Window owner) : IDesktopDialogs
{
    public async Task<string?> OpenFileAsync(
        string title,
        string filterName,
        IReadOnlyList<string> patterns)
    {
        var storage = owner.StorageProvider;
        if (storage is null || !storage.CanOpen) return null;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [BuildFilter(filterName, patterns)],
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> SaveFileAsync(
        string title,
        string filterName,
        IReadOnlyList<string> patterns,
        string suggestedName)
    {
        var storage = owner.StorageProvider;
        if (storage is null || !storage.CanSave) return null;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = [BuildFilter(filterName, patterns)],
        });
        return file?.TryGetLocalPath();
    }

    public async Task<DeviceFlowOutcome> RunDeviceFlowAsync(
        GitHubDeviceFlow flow,
        DeviceCodeResponse code,
        DesktopText text)
    {
        var dialog = new DeviceFlowWindow(flow, code, text);
        await dialog.ShowDialog(owner);
        return new DeviceFlowOutcome(dialog.Token, dialog.ErrorMessage);
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new MessageWindow(title, message);
        await dialog.ShowDialog(owner);
    }

    private static FilePickerFileType BuildFilter(string name, IReadOnlyList<string> patterns) =>
        new(name)
        {
            Patterns = [.. patterns],
        };
}
