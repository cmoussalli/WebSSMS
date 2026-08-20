using System.Text.Json.Serialization;

namespace WebSSMS.Models;

/// <summary>
/// Which endpoint the assistant talks to. Every provider here speaks the OpenAI
/// /chat/completions shape -- Ollama and DeepSeek both expose it -- so only the
/// base URL, the model name and whether a key is required actually differ.
/// </summary>
public enum AiProvider
{
    Ollama,
    DeepSeek,
    OpenAI,
    Custom
}

/// <summary>
/// Everything the AI panel needs to reach an LLM. Bound from the "Ai" section of
/// appsettings.json as the deployment default, then overridable from the settings
/// dialog, which saves server-side -- see <see cref="Services.AiSettingsStore"/>.
/// </summary>
public class AiSettings
{
    public const string SectionName = "Ai";

    /// <summary>Hides the AI panel entirely when false.</summary>
    public bool Enabled { get; set; } = true;

    public AiProvider Provider { get; set; } = AiProvider.Ollama;

    /// <summary>Root of the OpenAI-compatible API, e.g. http://localhost:11434/v1. Empty = the provider default.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Negative = leave it to the server. Low values keep generated SQL predictable.</summary>
    public double Temperature { get; set; } = 0.1;

    /// <summary>0 = omit the field, which some models require.</summary>
    public int MaxTokens { get; set; } = 4096;

    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Off for local models that cannot call functions. The assistant then gets a
    /// one-shot schema digest of the current database instead of live lookups.
    /// </summary>
    public bool UseTools { get; set; } = true;

    /// <summary>Ceiling on lookups per question, so a confused model cannot loop forever.</summary>
    public int MaxToolCalls { get; set; } = 12;

    /// <summary>Lets the assistant read rows, not just structure. Read-only either way.</summary>
    public bool AllowDataQueries { get; set; } = true;

    /// <summary>Rows handed back from one look at the data.</summary>
    public int MaxRows { get; set; } = 20;

    /// <summary>
    /// Write the API key into the saved settings file alongside the rest. Off keeps
    /// the key in memory for this run of the app only.
    /// </summary>
    public bool StoreApiKey { get; set; } = true;

    public static string DefaultBaseUrl(AiProvider provider) => provider switch
    {
        AiProvider.Ollama => "http://localhost:11434/v1",
        AiProvider.DeepSeek => "https://api.deepseek.com/v1",
        AiProvider.OpenAI => "https://api.openai.com/v1",
        _ => string.Empty
    };

    public static string DefaultModel(AiProvider provider) => provider switch
    {
        AiProvider.Ollama => "qwen2.5-coder:7b",
        AiProvider.DeepSeek => "deepseek-chat",
        AiProvider.OpenAI => "gpt-4o-mini",
        _ => string.Empty
    };

    /// <summary>Ollama serves unauthenticated on localhost; the hosted providers do not.</summary>
    public static bool RequiresApiKey(AiProvider provider) =>
        provider is AiProvider.DeepSeek or AiProvider.OpenAI;

    public static string DisplayName(AiProvider provider) => provider switch
    {
        AiProvider.Ollama => "Ollama (local)",
        AiProvider.DeepSeek => "DeepSeek",
        AiProvider.OpenAI => "OpenAI",
        _ => "Custom (OpenAI-compatible)"
    };

    [JsonIgnore]
    public string ResolvedBaseUrl =>
        (string.IsNullOrWhiteSpace(BaseUrl) ? DefaultBaseUrl(Provider) : BaseUrl).TrimEnd('/');

    [JsonIgnore]
    public string ResolvedModel =>
        string.IsNullOrWhiteSpace(Model) ? DefaultModel(Provider) : Model.Trim();

    /// <summary>Short label for the panel header, e.g. "deepseek-chat @ DeepSeek".</summary>
    [JsonIgnore]
    public string ShortLabel => $"{ResolvedModel} @ {DisplayName(Provider)}";

    /// <summary>Null when the settings could be used to make a call.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(ResolvedBaseUrl))
            return "Enter the endpoint URL of the OpenAI-compatible API.";

        if (!Uri.TryCreate(ResolvedBaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return $"'{ResolvedBaseUrl}' is not a valid http:// or https:// URL.";

        if (string.IsNullOrWhiteSpace(ResolvedModel))
            return "Enter the model name to generate with.";

        if (RequiresApiKey(Provider) && string.IsNullOrWhiteSpace(ApiKey))
            return $"{DisplayName(Provider)} needs an API key.";

        return null;
    }

    public AiSettings Clone() => (AiSettings)MemberwiseClone();
}
