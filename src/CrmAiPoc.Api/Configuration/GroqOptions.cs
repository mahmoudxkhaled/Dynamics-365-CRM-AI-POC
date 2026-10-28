namespace CrmAiPoc.Api.Configuration;

public sealed class GroqOptions
{
    public const string SectionName = "Groq";

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Groq model id, e.g. openai/gpt-oss-120b.</summary>
    public string ModelName { get; set; } = "openai/gpt-oss-120b";

    /// <summary>OpenAI-compatible Groq base URL.</summary>
    public string BaseUrl { get; set; } = "https://api.groq.com/openai/v1";
}
