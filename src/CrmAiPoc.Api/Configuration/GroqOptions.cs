namespace CrmAiPoc.Api.Configuration;

public sealed class GroqOptions
{
    public const string SectionName = "Groq";

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Preferred Groq model id (config key <c>Groq:Model</c>), e.g. openai/gpt-oss-120b.</summary>
    public string? Model { get; set; }

    /// <summary>Legacy alias for <see cref="Model"/> (<c>Groq:ModelName</c>).</summary>
    public string ModelName { get; set; } = "openai/gpt-oss-120b";

    /// <summary>
    /// Groq <c>reasoning_effort</c> for reasoning models (gpt-oss: low|medium|high).
    /// Default medium.
    /// </summary>
    public string ReasoningEffort { get; set; } = "medium";

    /// <summary>Sampling temperature sent on Groq chat completions.</summary>
    public float Temperature { get; set; }

    /// <summary>OpenAI-compatible Groq base URL.</summary>
    public string BaseUrl { get; set; } = "https://api.groq.com/openai/v1";

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

        return "openai/gpt-oss-120b";
    }
}
