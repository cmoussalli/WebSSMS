using WebSSMS.Models;

namespace WebSSMS.Services;

/// <summary>
/// The LLM settings in use for this circuit.
///
/// Starts from whatever <see cref="AiSettingsStore"/> has saved -- so a browser
/// that has never seen this app still gets the configured endpoint -- and follows
/// it when another tab saves a change. <see cref="Apply"/> overrides the settings
/// for this circuit only; <see cref="Save"/> makes the override everyone's.
/// </summary>
public sealed class AiSettingsProvider : IDisposable
{
    private readonly AiSettingsStore _store;

    public AiSettingsProvider(AiSettingsStore store)
    {
        _store = store;
        Current = store.Current;
        _store.Changed += HandleStoreChanged;
    }

    public AiSettings Current { get; private set; }

    /// <summary>Raised when the settings change, so open panels can re-render.</summary>
    public event Action? Changed;

    /// <summary>The feature is switched off server-wide.</summary>
    public bool IsEnabled => _store.IsEnabled;

    /// <summary>Enough is filled in to make a call.</summary>
    public bool IsUsable => Current.Validate() == null;

    /// <summary>True once settings have been saved, i.e. there is something to fall back from.</summary>
    public bool HasSaved => _store.HasSaved;

    public string FilePath => _store.FilePath;

    /// <summary>Uses these settings for this circuit, without writing them anywhere.</summary>
    public void Apply(AiSettings settings)
    {
        Current = settings.Clone();
        Changed?.Invoke();
    }

    /// <summary>
    /// Applies and persists. Returns null on success, or why the file could not be
    /// written -- the settings still apply either way.
    /// </summary>
    public string? Save(AiSettings settings)
    {
        Apply(settings);
        return _store.Save(settings);
    }

    /// <summary>Back to whatever appsettings.json says, and forgets the saved file.</summary>
    public void ResetToConfigured()
    {
        _store.Reset();
        Apply(_store.Current);
    }

    /// <summary>
    /// Adopts settings an older build left in the browser's localStorage and saves
    /// them server-side, so an existing setup is not lost to the move. Only ever
    /// called when nothing has been saved yet.
    /// </summary>
    public bool TryImportLegacy(string? json)
    {
        if (_store.HasSaved) return false;

        var restored = AiSettingsStore.Deserialize(json);
        if (restored == null || restored.Validate() != null) return false;

        Save(restored);
        return true;
    }

    private void HandleStoreChanged()
    {
        Current = _store.Current;
        Changed?.Invoke();
    }

    public void Dispose() => _store.Changed -= HandleStoreChanged;
}
