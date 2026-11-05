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
    /// Max seconds to wait for the AI provider tool-call response.
    /// On timeout, chat falls back to deterministic entity resolution when possible.
    /// </summary>
    public int ProviderTimeoutSeconds { get; set; } = 25;
}
