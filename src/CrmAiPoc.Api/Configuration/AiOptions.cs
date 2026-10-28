namespace CrmAiPoc.Api.Configuration;

/// <summary>
/// Selects which AI backend to use. Both Gemini (AI Studio) and OpenAI remain configured.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>Groq (default), Gemini, or OpenAI.</summary>
    public string Provider { get; set; } = "Groq";
}
