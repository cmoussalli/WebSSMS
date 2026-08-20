using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WebSSMS.Models;

namespace WebSSMS.Services;

/// <summary>
/// Talks to any OpenAI-compatible /chat/completions endpoint.
///
/// Ollama, DeepSeek and OpenAI all expose that same shape -- including the
/// function-calling fields -- so one client covers every provider WebSSMS offers,
/// and "Custom" covers whatever else speaks the protocol (vLLM, LM Studio,
/// OpenRouter, a corporate gateway).
///
/// Deliberately non-streaming: a generated script is only useful once it is
/// complete, and the panel shows the model's lookups as progress instead.
/// </summary>
public sealed class LlmClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly IHttpClientFactory _httpClientFactory;

    public LlmClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<LlmMessage> CompleteAsync(
        AiSettings settings,
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmTool>? tools,
        CancellationToken cancellationToken = default)
    {
        var request = new ChatRequest
        {
            Model = settings.ResolvedModel,
            Messages = messages.Select(ToWire).ToList(),
            Tools = tools is { Count: > 0 } ? tools.Select(ToWire).ToList() : null,
            Temperature = settings.Temperature >= 0 ? settings.Temperature : null,
            MaxTokens = settings.MaxTokens > 0 ? settings.MaxTokens : null,
            Stream = false
        };

        var payload = await PostAsync(settings, "chat/completions", request, cancellationToken);

        var choice = payload["choices"]?.AsArray().FirstOrDefault()
            ?? throw new LlmException("The model returned no choices.");

        var message = choice["message"]
            ?? throw new LlmException("The model returned a choice with no message.");

        return ReadMessage(message);
    }

    /// <summary>Lists the models the endpoint serves. Used by the settings dialog's Test button.</summary>
    public async Task<List<string>> ListModelsAsync(AiSettings settings, CancellationToken cancellationToken = default)
    {
        var payload = await GetAsync(settings, "models", cancellationToken);

        var models = new List<string>();
        if (payload["data"] is JsonArray data)
        {
            foreach (var item in data)
            {
                var id = item?["id"]?.ToString();
                if (!string.IsNullOrEmpty(id)) models.Add(id);
            }
        }
        models.Sort(StringComparer.OrdinalIgnoreCase);
        return models;
    }

    // ------------------------------------------------------------------ transport

    private HttpClient CreateClient(AiSettings settings)
    {
        var client = _httpClientFactory.CreateClient("llm");
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 5, 600));

        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
        }

        return client;
    }

    private async Task<JsonNode> PostAsync(
        AiSettings settings, string path, object body, CancellationToken cancellationToken)
    {
        var client = CreateClient(settings);
        var url = $"{settings.ResolvedBaseUrl}/{path}";

        var json = JsonSerializer.Serialize(body, SerializerOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync(url, content, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmException($"{url} did not answer within {settings.TimeoutSeconds} seconds.");
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException($"Could not reach {url}: {ex.Message}");
        }

        return await ReadPayloadAsync(response, url, cancellationToken);
    }

    private async Task<JsonNode> GetAsync(AiSettings settings, string path, CancellationToken cancellationToken)
    {
        var client = CreateClient(settings);
        var url = $"{settings.ResolvedBaseUrl}/{path}";

        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(url, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmException($"{url} did not answer within {settings.TimeoutSeconds} seconds.");
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException($"Could not reach {url}: {ex.Message}");
        }

        return await ReadPayloadAsync(response, url, cancellationToken);
    }

    private static async Task<JsonNode> ReadPayloadAsync(
        HttpResponseMessage response, string url, CancellationToken cancellationToken)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new LlmException(
                    $"{url} returned {(int)response.StatusCode} {response.ReasonPhrase}: {ExtractError(body)}");
            }

            JsonNode? payload;
            try
            {
                payload = JsonNode.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new LlmException($"{url} returned a response that is not JSON: {ex.Message}");
            }

            if (payload == null)
                throw new LlmException($"{url} returned an empty response.");

            // Some gateways answer 200 with an error body.
            if (payload["error"] != null)
                throw new LlmException(ExtractError(body));

            return payload;
        }
    }

    /// <summary>Digs the human-readable part out of an error body, whatever shape it came in.</summary>
    private static string ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "no details returned.";

        try
        {
            var node = JsonNode.Parse(body);
            var error = node?["error"];

            var message = error switch
            {
                JsonValue value => value.ToString(),
                JsonObject obj => obj["message"]?.ToString(),
                _ => node?["message"]?.ToString()
            };

            if (!string.IsNullOrWhiteSpace(message)) return message;
        }
        catch (JsonException)
        {
            // Fall through to the raw body.
        }

        return body.Length > 400 ? body[..400] + "..." : body;
    }

    // -------------------------------------------------------------------- mapping

    private static ChatMessage ToWire(LlmMessage message) => new()
    {
        Role = message.Role,
        Content = message.Content,
        ToolCallId = message.ToolCallId,
        ToolCalls = message.ToolCalls is { Count: > 0 }
            ? message.ToolCalls.Select(call => new ChatToolCall
            {
                Id = call.Id,
                Type = "function",
                Function = new ChatToolCallFunction { Name = call.Name, Arguments = call.Arguments }
            }).ToList()
            : null
    };

    private static ChatTool ToWire(LlmTool tool) => new()
    {
        Type = "function",
        Function = new ChatToolFunction
        {
            Name = tool.Name,
            Description = tool.Description,
            Parameters = JsonNode.Parse(tool.ParametersJson)
        }
    };

    private static LlmMessage ReadMessage(JsonNode message)
    {
        var calls = new List<LlmToolCall>();

        if (message["tool_calls"] is JsonArray toolCalls)
        {
            foreach (var call in toolCalls)
            {
                var function = call?["function"];
                var name = function?["name"]?.ToString();
                if (string.IsNullOrEmpty(name)) continue;

                var id = call?["id"]?.ToString();

                calls.Add(new LlmToolCall
                {
                    // Ollama omits the id that OpenAI always sends; the tool reply
                    // has to quote one back, so mint a stand-in when it is missing.
                    Id = string.IsNullOrEmpty(id) ? $"call_{calls.Count + 1}" : id,
                    Name = name,
                    Arguments = ReadArguments(function?["arguments"])
                });
            }
        }

        return new LlmMessage
        {
            Role = "assistant",
            Content = ReadContent(message["content"]),
            ToolCalls = calls
        };
    }

    /// <summary>The spec says arguments is a JSON string; some servers send the object itself.</summary>
    private static string ReadArguments(JsonNode? arguments) => arguments switch
    {
        null => "{}",
        JsonValue value => value.ToString(),
        _ => arguments.ToJsonString()
    };

    /// <summary>Content is usually a string, but the multimodal shape is a parts array.</summary>
    private static string ReadContent(JsonNode? content) => content switch
    {
        null => string.Empty,
        JsonValue value => value.ToString(),
        JsonArray parts => string.Concat(parts.Select(part => part?["text"]?.ToString() ?? string.Empty)),
        _ => content.ToString()
    };

    // ----------------------------------------------------------------- wire types

    private sealed class ChatRequest
    {
        public string Model { get; set; } = string.Empty;
        public List<ChatMessage> Messages { get; set; } = new();
        public List<ChatTool>? Tools { get; set; }
        public double? Temperature { get; set; }
        public int? MaxTokens { get; set; }
        public bool Stream { get; set; }
    }

    private sealed class ChatMessage
    {
        public string Role { get; set; } = string.Empty;
        public string? Content { get; set; }
        public string? ToolCallId { get; set; }
        public List<ChatToolCall>? ToolCalls { get; set; }
    }

    private sealed class ChatToolCall
    {
        public string Id { get; set; } = string.Empty;
        public string Type { get; set; } = "function";
        public ChatToolCallFunction Function { get; set; } = new();
    }

    private sealed class ChatToolCallFunction
    {
        public string Name { get; set; } = string.Empty;
        public string Arguments { get; set; } = "{}";
    }

    private sealed class ChatTool
    {
        public string Type { get; set; } = "function";
        public ChatToolFunction Function { get; set; } = new();
    }

    private sealed class ChatToolFunction
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public JsonNode? Parameters { get; set; }
    }
}

/// <summary>A message in the conversation sent to the model.</summary>
public sealed class LlmMessage
{
    public string Role { get; set; } = "user";
    public string? Content { get; set; }

    /// <summary>Set on a "tool" message to say which call it answers.</summary>
    public string? ToolCallId { get; set; }

    /// <summary>Set on an "assistant" message that asked for lookups.</summary>
    public List<LlmToolCall> ToolCalls { get; set; } = new();

    public static LlmMessage System(string content) => new() { Role = "system", Content = content };
    public static LlmMessage User(string content) => new() { Role = "user", Content = content };

    public static LlmMessage Tool(string toolCallId, string content) =>
        new() { Role = "tool", ToolCallId = toolCallId, Content = content };
}

public sealed class LlmToolCall
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Raw JSON object, as the model wrote it.</summary>
    public string Arguments { get; set; } = "{}";
}

/// <summary>A function the model may call, described with JSON Schema.</summary>
public sealed class LlmTool
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ParametersJson { get; set; } = "{\"type\":\"object\",\"properties\":{}}";
}

public sealed class LlmException : Exception
{
    public LlmException(string message) : base(message) { }
}
