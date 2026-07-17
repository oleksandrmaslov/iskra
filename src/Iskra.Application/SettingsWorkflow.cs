using System.Globalization;
using Iskra.Core;

namespace Iskra.Application;

public interface ISettingsPersistence
{
    AppSettings Load();
    void Save(AppSettings settings);
}

public sealed class AppSettingsPersistence(string? path = null) : ISettingsPersistence
{
    public AppSettings Load() => AppSettingsStore.Load(path);
    public void Save(AppSettings settings) => AppSettingsStore.Save(settings, path);
}

public enum SettingsField
{
    BmpFrequencyHz,
    TimeoutSeconds,
    LogShipIntervalMinutes,
}

public enum SettingsSaveStatus
{
    Saved,
    ValidationFailed,
    WriteFailed,
}

public sealed record SettingsSaveResult(
    SettingsSaveStatus Status,
    AppSettings? Settings,
    SettingsField? InvalidField,
    string? Diagnostic)
{
    public bool IsSaved => Status == SettingsSaveStatus.Saved;
}

/// <summary>
/// Raw frontend values. Numeric text stays raw so every UI uses the same
/// invariant positive-integer validation instead of toolkit-specific parsing.
/// </summary>
public sealed record SettingsDraft(
    string? LanguageCode,
    string? CatalogPath,
    bool RequireSignedCatalog,
    bool CatalogAutoUpdate,
    string? GdbPath,
    string? BmpFrequencyHz,
    PowerMode Power,
    bool ConnectUnderReset,
    string? TimeoutSeconds,
    string? DbPath,
    string? StationId,
    bool BatchesEnabled,
    string? LastOperator,
    string? LastBatch,
    FlashHotkey FlashHotkey,
    bool LogShippingEnabled,
    string? LogShipIntervalMinutes,
    string? LogShipperPrivateKeyPath)
{
    public static SettingsDraft FromSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new SettingsDraft(
            settings.LanguageCode,
            settings.CatalogPath,
            settings.RequireSignedCatalog,
            settings.CatalogAutoUpdate,
            settings.GdbPath,
            settings.BmpFrequencyHz.ToString(CultureInfo.InvariantCulture),
            settings.Power,
            settings.ConnectUnderReset,
            settings.TimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            settings.DbPath,
            settings.StationId,
            settings.BatchesEnabled,
            settings.LastOperator,
            settings.LastBatch,
            settings.FlashHotkey,
            settings.LogShippingEnabled,
            settings.LogShipIntervalMinutes.ToString(CultureInfo.InvariantCulture),
            settings.LogShipperPrivateKeyPath);
    }
}

/// <summary>
/// Shared validation and atomic persistence policy. Frontends retain control
/// mapping, save dialogs, dirty indicators, and localized error presentation.
/// </summary>
public sealed class SettingsWorkflow
{
    private readonly ISettingsPersistence _persistence;

    public SettingsWorkflow(ISettingsPersistence? persistence = null)
    {
        _persistence = persistence ?? new AppSettingsPersistence();
    }

    public AppSettings Load() => _persistence.Load();

    public AppSettings Defaults() => new();

    public SettingsSaveResult Save(AppSettings current, SettingsDraft draft)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(draft);

        var candidateResult = BuildCandidate(current, draft);
        if (!candidateResult.IsSaved) return candidateResult;

        try
        {
            _persistence.Save(candidateResult.Settings!);
            return candidateResult;
        }
        catch (Exception ex)
        {
            return new SettingsSaveResult(
                SettingsSaveStatus.WriteFailed,
                null,
                null,
                ex.Message);
        }
    }

    /// <summary>
    /// Safe narrow update for the Avalonia alpha language selector. Reloading
    /// immediately before save prevents a stale preview session from replacing
    /// unrelated settings recently written by WPF.
    /// </summary>
    public SettingsSaveResult UpdateLanguage(string? languageCode)
    {
        AppSettings latest;
        try
        {
            latest = _persistence.Load();
        }
        catch (Exception ex)
        {
            return new(SettingsSaveStatus.WriteFailed, null, null, ex.Message);
        }

        var draft = SettingsDraft.FromSettings(latest) with
        {
            LanguageCode = languageCode,
        };
        return Save(latest, draft);
    }

    public SettingsSaveResult RememberOperatorSelection(
        AppSettings current,
        string? operatorName,
        string? effectiveBatchId)
    {
        ArgumentNullException.ThrowIfNull(current);
        var draft = SettingsDraft.FromSettings(current) with
        {
            LastOperator = operatorName,
            LastBatch = current.BatchesEnabled ? effectiveBatchId : null,
        };
        return Save(current, draft);
    }

    public SettingsSaveResult BuildCandidate(AppSettings current, SettingsDraft draft)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(draft);

        if (!TryPositiveInt(draft.BmpFrequencyHz, out var frequency))
            return Invalid(SettingsField.BmpFrequencyHz);
        if (!TryPositiveInt(draft.TimeoutSeconds, out var timeout))
            return Invalid(SettingsField.TimeoutSeconds);
        if (!TryPositiveInt(draft.LogShipIntervalMinutes, out var interval))
            return Invalid(SettingsField.LogShipIntervalMinutes);

        var candidate = current.Clone();
        candidate.LanguageCode = IskraLanguages.NormalizeOrDefault(draft.LanguageCode);
        candidate.CatalogPath = NullIfWhiteSpace(draft.CatalogPath);
        candidate.RequireSignedCatalog = !CatalogTrust.IsUnsignedLabModeEnabled()
            || draft.RequireSignedCatalog;
        candidate.CatalogAutoUpdate = draft.CatalogAutoUpdate;
        candidate.CatalogOwner = CatalogTrust.OfficialCatalogSource.Owner;
        candidate.CatalogRepo = CatalogTrust.OfficialCatalogSource.Repo;
        candidate.GdbPath = NullIfWhiteSpace(draft.GdbPath);
        candidate.BmpFrequencyHz = frequency;
        candidate.Power = draft.Power;
        candidate.ConnectUnderReset = draft.ConnectUnderReset;
        candidate.TimeoutSeconds = timeout;
        candidate.DbPath = NullIfWhiteSpace(draft.DbPath);
        candidate.StationId = string.IsNullOrWhiteSpace(draft.StationId)
            ? Environment.MachineName
            : draft.StationId.Trim();
        candidate.BatchesEnabled = draft.BatchesEnabled;
        candidate.LastOperator = NullIfWhiteSpace(draft.LastOperator);
        candidate.LastBatch = draft.BatchesEnabled
            ? NullIfWhiteSpace(draft.LastBatch)
            : null;
        candidate.FlashHotkey = draft.FlashHotkey;
        candidate.LogShippingEnabled = draft.LogShippingEnabled;
        candidate.LogShipIntervalMinutes = interval;
        candidate.LogShipperPrivateKeyPath = string.IsNullOrWhiteSpace(draft.LogShipperPrivateKeyPath)
            ? AppSettings.DefaultLogShipperPrivateKeyPath
            : draft.LogShipperPrivateKeyPath.Trim();

        return new SettingsSaveResult(SettingsSaveStatus.Saved, candidate, null, null);
    }

    private static SettingsSaveResult Invalid(SettingsField field) =>
        new(SettingsSaveStatus.ValidationFailed, null, field, null);

    private static bool TryPositiveInt(string? value, out int parsed) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
        && parsed > 0;

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
