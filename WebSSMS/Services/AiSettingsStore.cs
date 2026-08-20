using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization;
using WebSSMS.Models;

namespace WebSSMS.Services;

/// <summary>
/// The saved LLM settings, kept in a file on the server so they outlive both the
/// process and the browser.
///
/// appsettings.json supplies the deployment default; whatever the settings dialog
/// last saved wins over it. The file is written next to the app -- App_Data by
/// default, "Ai:SettingsFile" to put it elsewhere -- and holds the API key in
/// plain text unless "Store the API key" is cleared, so it belongs on a machine
/// whose disk you trust. Nothing is written until someone presses Save.
/// </summary>
public sealed class AiSettingsStore
{
    public const string DefaultFileName = "ai-settings.json";

    private static readonly JsonSerializerOptions StorageOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly AiSettings _configured;
    private readonly ILogger<AiSettingsStore> _logger;
    private readonly object _gate = new();

    private AiSettings _current;

    public AiSettingsStore(
        IOptions<AiSettings> options,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<AiSettingsStore> logger)
    {
        _configured = options.Value;
        _logger = logger;
        FilePath = ResolvePath(configuration[$"{AiSettings.SectionName}:SettingsFile"], environment.ContentRootPath);
        _current = LoadOrDefault();
    }

    /// <summary>Where the saved settings live. Shown in the dialog when a write fails.</summary>
    public string FilePath { get; }

    /// <summary>True once someone has saved settings, i.e. the file exists and parsed.</summary>
    public bool HasSaved { get; private set; }

    /// <summary>The feature is switched off server-wide. Never a saved decision.</summary>
    public bool IsEnabled => _configured.Enabled;

    /// <summary>Raised after a save, so circuits already open pick the change up.</summary>
    public event Action? Changed;

    /// <summary>What appsettings.json alone says, ignoring anything saved.</summary>
    public AiSettings Configured => Normalize(_configured.Clone());

    /// <summary>The settings in force. A copy -- mutate it freely.</summary>
    public AiSettings Current
    {
        get { lock (_gate) return _current.Clone(); }
    }

    /// <summary>
    /// Makes <paramref name="settings"/> the saved settings. Returns null on success,
    /// or the reason the file could not be written -- in which case the settings still
    /// apply to this run of the app, they just will not survive a restart.
    /// </summary>
    public string? Save(AiSettings settings)
    {
        var stored = Normalize(settings.Clone());

        lock (_gate)
        {
            _current = stored;
        }

        Changed?.Invoke();

        var onDisk = stored.Clone();
        if (!onDisk.StoreApiKey) onDisk.ApiKey = string.Empty;

        try
        {
            lock (_gate)
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                File.WriteAllText(FilePath, JsonSerializer.Serialize(onDisk, StorageOptions));
                HasSaved = true;
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Could not write the AI settings to {Path}.", FilePath);
            return $"Could not write {FilePath}: {ex.Message}";
        }
    }

    /// <summary>Discards the saved settings and goes back to appsettings.json.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _current = Configured;
            HasSaved = false;

            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not delete the AI settings at {Path}.", FilePath);
            }
        }

        Changed?.Invoke();
    }

    /// <summary>Reads settings the old build left in a browser. Bad JSON is ignored, not thrown.</summary>
    public static AiSettings? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<AiSettings>(json, StorageOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private AiSettings LoadOrDefault()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var saved = Deserialize(File.ReadAllText(FilePath));
                if (saved != null)
                {
                    HasSaved = true;
                    _logger.LogInformation("Loaded the AI assistant settings from {Path}.", FilePath);
                    return Normalize(saved);
                }

                _logger.LogWarning("{Path} is not valid JSON; using the appsettings.json defaults.", FilePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the AI settings at {Path}.", FilePath);
        }

        return Configured;
    }

    /// <summary>
    /// Reconciles a set of settings with the two things that are the deployment's call,
    /// not the saved copy's: whether the panel exists at all, and a key held only in
    /// appsettings.json (which a save that deliberately left the key out must not lose).
    /// </summary>
    private AiSettings Normalize(AiSettings settings)
    {
        settings.Enabled = _configured.Enabled;

        if (string.IsNullOrEmpty(settings.ApiKey) && settings.Provider == _configured.Provider)
            settings.ApiKey = _configured.ApiKey;

        return settings;
    }

    private static string ResolvePath(string? configured, string contentRoot)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return Path.Combine(contentRoot, "App_Data", DefaultFileName);

        var path = Environment.ExpandEnvironmentVariables(configured.Trim());

        // A folder, or a bare name, is taken relative to the app.
        if (!Path.IsPathRooted(path)) path = Path.Combine(contentRoot, path);

        return Directory.Exists(path) ? Path.Combine(path, DefaultFileName) : path;
    }
}
