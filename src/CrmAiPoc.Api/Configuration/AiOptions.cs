namespace CrmAiPoc.Api.Configuration;

/// <summary>
/// Selects which AI backend to use. Both Gemini (AI Studio) and OpenAI remain configured.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>Groq (default), Gemini, or OpenAI.</summary>
    public string Provider { get; set; } = "Groq";

    /// <summary>
    /// Max seconds to wait for a single AI provider planning response.
    /// Client-side timeouts do not automatically retry another full wait.
    /// Bound in ChatService to 8–90 seconds.
    /// </summary>
    public int ProviderTimeoutSeconds { get; set; } = 60;
}
