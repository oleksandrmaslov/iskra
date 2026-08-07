using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Iskra.Application;
using Iskra.Core;

namespace Iskra.Desktop;

/// <summary>
/// Editable Settings surface. Every value round-trips through the shared
/// <see cref="SettingsWorkflow"/>, so validation and atomic persistence stay
/// identical to WPF and no parsing rules are duplicated in this frontend.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private static readonly IBrush StatusOkBrush = new SolidColorBrush(Color.FromRgb(0x1B, 0x8A, 0x1B));
    private static readonly IBrush StatusWarnBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x66, 0x00));
    private static readonly IBrush StatusErrorBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
    private static readonly IBrush StatusMutedBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));

    private bool _applyingSettings;
    private bool _settingsDirty;
    private string _settingsStatusText = string.Empty;
    private IBrush _settingsStatusBrush = StatusMutedBrush;

    private string _catalogPathInput = string.Empty;
    private bool _requireSignedCatalogInput = true;
    private bool _catalogAutoUpdateInput = true;
    private string _gdbPathInput = string.Empty;
    private string _bmpFrequencyInput = string.Empty;
    private bool _powerIsExternal = true;
    private bool _powerIsProbe;
    private bool _connectUnderResetInput;
    private string _timeoutInput = string.Empty;
    private string _dbPathInput = string.Empty;
    private string _stationIdInput = string.Empty;
    private bool _batchesEnabledInput;
    private HotkeyOption _selectedHotkey = HotkeyOption.All[0];
    private bool _logShippingEnabledInput;
    private string _logShipIntervalInput = string.Empty;
    private string _logShipperKeyInput = string.Empty;

    public ObservableCollection<HotkeyOption> HotkeyOptions { get; } = [];

    public RelayCommand SaveSettingsCommand { get; private set; } = null!;
    public RelayCommand ResetSettingsCommand { get; private set; } = null!;
    public AsyncRelayCommand PickCatalogPathCommand { get; private set; } = null!;
    public AsyncRelayCommand PickGdbPathCommand { get; private set; } = null!;
    public AsyncRelayCommand PickDbPathCommand { get; private set; } = null!;
    public AsyncRelayCommand PickLogKeyPathCommand { get; private set; } = null!;

    private void InitializeSettingsSurface()
    {
        SaveSettingsCommand = new RelayCommand(() => SaveSettings(showUnchangedNotice: true));
        ResetSettingsCommand = new RelayCommand(ResetSettingsToDefaults);
        PickCatalogPathCommand = new AsyncRelayCommand(PickCatalogPathAsync);
        PickGdbPathCommand = new AsyncRelayCommand(PickGdbPathAsync);
        PickDbPathCommand = new AsyncRelayCommand(PickDbPathAsync);
        PickLogKeyPathCommand = new AsyncRelayCommand(PickLogKeyPathAsync);
        RebuildHotkeyOptions();
        ApplySettingsToInputs();
    }

    // ============================================================
    // Bound inputs
    // ============================================================

    public string CatalogPathInput { get => _catalogPathInput; set => SetInput(ref _catalogPathInput, value ?? string.Empty); }
    public bool RequireSignedCatalogInput { get => _requireSignedCatalogInput; set => SetInput(ref _requireSignedCatalogInput, value); }
    public bool CatalogAutoUpdateInput { get => _catalogAutoUpdateInput; set => SetInput(ref _catalogAutoUpdateInput, value); }
    public string GdbPathInput { get => _gdbPathInput; set => SetInput(ref _gdbPathInput, value ?? string.Empty); }
    public string BmpFrequencyInput { get => _bmpFrequencyInput; set => SetInput(ref _bmpFrequencyInput, value ?? string.Empty); }
    public bool ConnectUnderResetInput { get => _connectUnderResetInput; set => SetInput(ref _connectUnderResetInput, value); }
    public string TimeoutInput { get => _timeoutInput; set => SetInput(ref _timeoutInput, value ?? string.Empty); }
    public string DbPathInput { get => _dbPathInput; set => SetInput(ref _dbPathInput, value ?? string.Empty); }
    public string StationIdInput { get => _stationIdInput; set => SetInput(ref _stationIdInput, value ?? string.Empty); }
    public bool BatchesEnabledInput { get => _batchesEnabledInput; set => SetInput(ref _batchesEnabledInput, value); }
    public bool LogShippingEnabledInput { get => _logShippingEnabledInput; set => SetInput(ref _logShippingEnabledInput, value); }
    public string LogShipIntervalInput { get => _logShipIntervalInput; set => SetInput(ref _logShipIntervalInput, value ?? string.Empty); }
    public string LogShipperKeyInput { get => _logShipperKeyInput; set => SetInput(ref _logShipperKeyInput, value ?? string.Empty); }

    public bool PowerIsExternal
    {
        get => _powerIsExternal;
        set
        {
            if (!SetInput(ref _powerIsExternal, value)) return;
            if (value && _powerIsProbe) SetInput(ref _powerIsProbe, false, nameof(PowerIsProbe));
        }
    }

    public bool PowerIsProbe
    {
        get => _powerIsProbe;
        set
        {
            if (!SetInput(ref _powerIsProbe, value)) return;
            if (value && _powerIsExternal) SetInput(ref _powerIsExternal, false, nameof(PowerIsExternal));
        }
    }

    public HotkeyOption SelectedHotkey
    {
        get => _selectedHotkey;
        set
        {
            if (value is null) return;
            SetInput(ref _selectedHotkey, value);
        }
    }

    /// <summary>
    /// Signature enforcement can only be relaxed when the lab-mode escape hatch
    /// is present. On a production build the checkbox is shown ticked and
    /// disabled so the operator can see the station cannot be downgraded.
    /// </summary>
    public bool CanEditRequireSignedCatalog => CatalogTrust.IsUnsignedLabModeEnabled();

    public string LockedCatalogSourceText =>
        $"{CatalogTrust.OfficialCatalogSource.Owner}/{CatalogTrust.OfficialCatalogSource.Repo}";

    public string LockedLogsSourceText =>
        $"{GitHubAppConfig.LogsRepoOwner}/{GitHubAppConfig.LogsRepoName}";

    public string LockedAppUpdateSourceText =>
        $"{GitHubAppConfig.AppUpdatesRepoOwner}/{GitHubAppConfig.AppUpdatesRepoName}";

    public bool IsSettingsDirty
    {
        get => _settingsDirty;
        private set => SetProperty(ref _settingsDirty, value);
    }

    public string SettingsStatusText { get => _settingsStatusText; private set => SetProperty(ref _settingsStatusText, value); }
    public IBrush SettingsStatusBrush { get => _settingsStatusBrush; private set => SetProperty(ref _settingsStatusBrush, value); }

    // ============================================================
    // Apply / save
    // ============================================================

    private void ApplySettingsToInputs()
    {
        _applyingSettings = true;
        try
        {
            var invariant = CultureInfo.InvariantCulture;
            CatalogPathInput = _settings.CatalogPath ?? string.Empty;
            RequireSignedCatalogInput = _settings.RequireSignedCatalog;
            CatalogAutoUpdateInput = _settings.CatalogAutoUpdate;
            GdbPathInput = _settings.GdbPath ?? string.Empty;
            BmpFrequencyInput = _settings.BmpFrequencyHz.ToString(invariant);
            PowerIsExternal = _settings.Power == PowerMode.External;
            PowerIsProbe = _settings.Power == PowerMode.Probe;
            ConnectUnderResetInput = _settings.ConnectUnderReset;
            TimeoutInput = _settings.TimeoutSeconds.ToString(invariant);
            DbPathInput = _settings.DbPath ?? string.Empty;
            StationIdInput = _settings.StationId;
            BatchesEnabledInput = _settings.BatchesEnabled;
            SelectedHotkey = HotkeyOptions.FirstOrDefault(o => o.Value == _settings.FlashHotkey)
                ?? HotkeyOptions[0];
            LogShippingEnabledInput = _settings.LogShippingEnabled;
            LogShipIntervalInput = _settings.LogShipIntervalMinutes.ToString(invariant);
            LogShipperKeyInput = _settings.LogShipperPrivateKeyPath;
        }
        finally
        {
            _applyingSettings = false;
        }

        IsSettingsDirty = false;
    }

    /// <summary>
    /// Called by the window when the operator leaves the Settings tab or closes
    /// the app, matching the WPF auto-save behavior. Returns false only when a
    /// validation or write error left the values unsaved.
    /// </summary>
    public bool SaveSettingsIfDirty()
    {
        if (!_settingsDirty) return true;
        return SaveSettings(showUnchangedNotice: false);
    }

    private bool SaveSettings(bool showUnchangedNotice)
    {
        if (!_settingsDirty && showUnchangedNotice)
        {
            SetSettingsStatus(Text.SettingsNoChanges, StatusMutedBrush);
            return true;
        }

        var draft = new SettingsDraft(
            LanguageCode: Text.LanguageCode,
            CatalogPath: _catalogPathInput,
            RequireSignedCatalog: _requireSignedCatalogInput,
            CatalogAutoUpdate: _catalogAutoUpdateInput,
            GdbPath: _gdbPathInput,
            BmpFrequencyHz: _bmpFrequencyInput,
            Power: _powerIsProbe ? PowerMode.Probe : PowerMode.External,
            ConnectUnderReset: _connectUnderResetInput,
            TimeoutSeconds: _timeoutInput,
            DbPath: _dbPathInput,
            StationId: _stationIdInput,
            BatchesEnabled: _batchesEnabledInput,
            LastOperator: _operatorName,
            LastBatch: _batchId,
            FlashHotkey: _selectedHotkey.Value,
            LogShippingEnabled: _logShippingEnabledInput,
            LogShipIntervalMinutes: _logShipIntervalInput,
            LogShipperPrivateKeyPath: _logShipperKeyInput);

        var result = _settingsWorkflow.Save(_settings, draft);
        switch (result.Status)
        {
            case SettingsSaveStatus.ValidationFailed:
                SetSettingsStatus(Text.SettingsInvalidField(FieldLabel(result.InvalidField)), StatusErrorBrush);
                return false;

            case SettingsSaveStatus.WriteFailed:
                SetSettingsStatus(Text.SettingsSaveFailed(result.Diagnostic ?? string.Empty), StatusErrorBrush);
                return false;

            default:
                _settings = result.Settings!;
                IsSettingsDirty = false;
                SetSettingsStatus(Text.SettingsSaved, StatusOkBrush);
                OnSettingsPersisted();
                return true;
        }
    }

    private void ResetSettingsToDefaults()
    {
        _settings = _settingsWorkflow.Defaults();
        ApplySettingsToInputs();
        IsSettingsDirty = true;
        SetSettingsStatus(Text.SettingsResetNotice, StatusWarnBrush);
    }

    /// <summary>
    /// A saved change can move the gdb path, catalog source, database, batch
    /// mode, or hotkey, so every dependent surface is rebuilt from the new
    /// settings rather than left showing the previous station configuration.
    /// </summary>
    private void OnSettingsPersisted()
    {
        if (!_settings.BatchesEnabled) _batchId = string.Empty;
        OnPropertyChanged(nameof(BatchId));
        OnPropertyChanged(nameof(BatchesEnabled));
        OnPropertyChanged(nameof(BatchModeStatusText));
        OnPropertyChanged(nameof(LogShippingStatusText));
        OnPropertyChanged(nameof(LogKeyPath));
        OnPropertyChanged(nameof(StationId));
        OnPropertyChanged(nameof(DatabasePath));
        OnPropertyChanged(nameof(FlashHotkey));
        OnPropertyChanged(nameof(HotkeyLabel));
        OnPropertyChanged(nameof(HotkeyHintText));
        OnPropertyChanged(nameof(FlashTooltipText));
        ExportBatchCommand.RaiseCanExecuteChanged();
        RefreshReadiness();
        RefreshCloudStatus();
    }

    private void SetSettingsStatus(string text, IBrush brush)
    {
        SettingsStatusText = text;
        SettingsStatusBrush = brush;
    }

    private string FieldLabel(SettingsField? field) => field switch
    {
        SettingsField.BmpFrequencyHz => Text.SettingsSwdFrequency,
        SettingsField.TimeoutSeconds => Text.SettingsTimeout,
        SettingsField.LogShipIntervalMinutes => Text.SettingsCloudInterval,
        _ => string.Empty,
    };

    private bool SetInput<T>(ref T field, T value, string? propertyName = null)
    {
        var changed = propertyName is null
            ? SetProperty(ref field, value)
            : SetPropertyNamed(ref field, value, propertyName);
        if (changed && !_applyingSettings)
        {
            IsSettingsDirty = true;
            SetSettingsStatus(Text.SettingsUnsaved, StatusWarnBrush);
        }

        return changed;
    }

    private bool SetPropertyNamed<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void RebuildHotkeyOptions()
    {
        var previous = _selectedHotkey.Value;
        HotkeyOptions.Clear();
        foreach (var option in HotkeyOption.Build(Text))
            HotkeyOptions.Add(option);

        _applyingSettings = true;
        try
        {
            SelectedHotkey = HotkeyOptions.FirstOrDefault(o => o.Value == previous) ?? HotkeyOptions[0];
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    // ============================================================
    // File pickers
    // ============================================================

    private async Task PickCatalogPathAsync()
    {
        var picked = await PickOpenAsync(Text.DialogCatalogTitle, Text.DialogFilterCatalog, ["*.json"]);
        if (picked is not null) CatalogPathInput = picked;
    }

    private async Task PickGdbPathAsync()
    {
        var patterns = OperatingSystem.IsWindows()
            ? new[] { "arm-none-eabi-gdb.exe", "*.exe" }
            : ["arm-none-eabi-gdb", "*"];
        var picked = await PickOpenAsync(Text.DialogGdbTitle, Text.DialogFilterGdb, patterns);
        if (picked is not null) GdbPathInput = picked;
    }

    private async Task PickDbPathAsync()
    {
        if (Dialogs is null) { ReportDialogsUnavailable(); return; }
        var picked = await Dialogs.SaveFileAsync(
            Text.DialogLogTitle, Text.DialogFilterSqlite, ["*.db"], "flash_log.db");
        if (picked is not null) DbPathInput = picked;
    }

    private async Task PickLogKeyPathAsync()
    {
        var picked = await PickOpenAsync(Text.DialogPemTitle, Text.DialogFilterPem, ["*.pem"]);
        if (picked is not null) LogShipperKeyInput = picked;
    }

    private async Task<string?> PickOpenAsync(string title, string filterName, IReadOnlyList<string> patterns)
    {
        if (Dialogs is null) { ReportDialogsUnavailable(); return null; }
        return await Dialogs.OpenFileAsync(title, filterName, patterns);
    }

    private void ReportDialogsUnavailable() =>
        SetSettingsStatus(Text.DialogUnavailable, StatusErrorBrush);
}

public sealed record HotkeyOption(FlashHotkey Value, string Label)
{
    internal static IReadOnlyList<HotkeyOption> All { get; } =
    [
        new(FlashHotkey.None, "—"),
        new(FlashHotkey.Enter, "Enter"),
        new(FlashHotkey.Space, "Space"),
        new(FlashHotkey.F2, "F2"),
        new(FlashHotkey.F5, "F5"),
    ];

    internal static IReadOnlyList<HotkeyOption> Build(DesktopText text) =>
    [
        new(FlashHotkey.None, text.HotkeyDisabled),
        new(FlashHotkey.Enter, "Enter"),
        new(FlashHotkey.Space, text.HotkeySpace),
        new(FlashHotkey.F2, "F2"),
        new(FlashHotkey.F5, "F5"),
    ];
}
