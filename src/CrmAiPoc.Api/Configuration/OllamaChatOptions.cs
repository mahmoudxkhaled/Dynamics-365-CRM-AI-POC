namespace CrmAiPoc.Api.Configuration;

/// <summary>
/// Chat LLM settings for a remote/local Ollama server (Ai:Ollama).
/// Separate from any embedding configuration.
/// </summary>
public sealed class OllamaChatOptions
{
    public const string SectionName = "Ai:Ollama";

    /// <summary>Ollama HTTP base URL, e.g. http://192.168.1.6:11434.</summary>
    public string BaseUrl { get; set; } = "http://192.168.1.6:11434";

    /// <summary>Ollama model tag, e.g. qwen3.5:4b-q4_K_M.</summary>
    public string Model { get; set; } = "qwen3.5:4b-q4_K_M";

    /// <summary>Legacy alias for <see cref="Model"/>.</summary>
    public string? ModelName { get; set; }

    /// <summary>Ollama <c>num_ctx</c> context window size.</summary>
    public int ContextSize { get; set; } = 16384;

    /// <summary>Sampling temperature (0 for deterministic planning).</summary>
    public float Temperature { get; set; }

    /// <summary>
    /// When true, enables Qwen/Ollama thinking tokens (mapped to MEAI ReasoningOptions).
    /// Thinking content is returned as <c>TextReasoningContent</c> and must not be treated as the user answer.
    /// Default false for predictable tool calling.
    /// </summary>
    public bool EnableThinking { get; set; }

    public string ResolveModel()
    {
        if (!string.IsNullOrWhiteSpace(Model))
        {
            return Model.Trim();
        }

        if (!string.IsNullOrWhiteSpace(ModelName))
        {
            return ModelName.Trim();
        }

        return "qwen3.5:4b-q4_K_M";
    }

    public string ResolveBaseUrl()
    {
        var url = string.IsNullOrWhiteSpace(BaseUrl) ? "http://192.168.1.6:11434" : BaseUrl.Trim();
        return url.TrimEnd('/');
    }
}
